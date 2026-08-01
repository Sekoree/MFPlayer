namespace S.Control;

/// <summary>One keyframe of an outbound ramp: a value the sender should be at, at a time.</summary>
/// <param name="Time">Offset from the ramp's start.</param>
/// <param name="Value">The value to send.</param>
public readonly record struct OutboundRampPoint(TimeSpan Time, double Value);

/// <summary>Why a ramp stopped, which decides whether its final value is still sent.</summary>
public enum OutboundRampCompletion
{
    /// <summary>Ran to the end of its last keyframe.</summary>
    Completed,

    /// <summary>The owning cue stopped, or the show panicked, part-way through.</summary>
    Interrupted,
}

/// <summary>
/// Drives a time-varying value out to an external system (an OSC address, a MIDI controller) at an
/// explicit rate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not an audio/video automation lane.</b> Internal automation multiplies into a
/// composition chain the framework owns and is sampled per chunk or per frame. An outbound lane is the
/// sole authority for a value living in someone else's console, and it is sampled at a rate chosen for
/// that console's sake. The editor is shared; the actuator is not - which is why this lives beside the
/// senders in the control layer rather than inside the media session, where no media is involved.
/// </para>
/// <para><b>Three contract points, each of which is a field failure if got wrong:</b></para>
/// <list type="number">
/// <item><description><b>An explicit send rate, never per-frame.</b> Nobody wants a lighting desk taking
/// sixty messages a second per lane. 25 Hz is the default and the author can lower it.</description></item>
/// <item><description><b>The ramp must land exactly on its final keyframe value.</b> Emitting at a fixed
/// rate means the last tick generally falls short of the end, which leaves a desk holding 0.7482 instead
/// of 0.75 - invisible in rehearsal and wrong for the rest of the show. The terminal value is therefore
/// sent explicitly when the ramp ends, <i>including</i> when it is interrupted: an outbound value is not
/// undone by stopping the cue, because it belongs to another system. That is the opposite of the rule for
/// internal lanes, and conflating the two is how a desk gets left mid-fade.</description></item>
/// <item><description><b>Coalesce, never queue.</b> If the endpoint is slow or unreachable, drop the
/// intermediate values rather than accumulate a backlog of stale ones - and still send the final value
/// when it recovers. A backlog would replay a fade the show has already moved past.</description></item>
/// </list>
/// <para>
/// Time is caller-supplied, exactly like the timecode decoders: <see cref="Advance"/> takes the elapsed
/// time, so the runner owns no timer and is deterministic under test.
/// </para>
/// </remarks>
public sealed class OutboundRampRunner
{
    /// <summary>Default emission rate. Fast enough to look continuous on a fader, slow enough not to
    /// flood a desk.</summary>
    public const double DefaultSendRateHz = 25d;

    private readonly IReadOnlyList<OutboundRampPoint> _points;
    private readonly Action<double> _send;
    private readonly TimeSpan _sendInterval;
    private readonly TimeSpan _duration;

    private TimeSpan _lastSentAt = TimeSpan.MinValue;
    private bool _finished;

    /// <param name="points">Keyframes, sorted by time. At least one is required.</param>
    /// <param name="send">The actuator. May be slow or may throw; see the coalescing note.</param>
    /// <param name="sendRateHz">Emission rate. Must be positive.</param>
    public OutboundRampRunner(
        IReadOnlyList<OutboundRampPoint> points,
        Action<double> send,
        double sendRateHz = DefaultSendRateHz)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(send);
        if (points.Count == 0)
            throw new ArgumentException("an outbound ramp needs at least one keyframe", nameof(points));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendRateHz);
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Time < points[i - 1].Time)
                throw new ArgumentException("keyframes must be sorted by time", nameof(points));
        }

        _points = points;
        _send = send;
        _sendInterval = TimeSpan.FromSeconds(1d / sendRateHz);
        _duration = points[^1].Time;
    }

    /// <summary>True once the ramp has finished and sent its terminal value.</summary>
    public bool IsFinished => _finished;

    /// <summary>The ramp's final value - what the external system is left holding.</summary>
    public double FinalValue => _points[^1].Value;

    /// <summary>
    /// Advances the ramp to <paramref name="elapsed"/>, emitting at most one value. Call as often as you
    /// like: emission is rate-limited internally, so an over-eager caller cannot flood the endpoint, and
    /// a late caller simply skips the values it slept through rather than replaying them.
    /// </summary>
    /// <returns>True when a value was sent.</returns>
    public bool Advance(TimeSpan elapsed)
    {
        if (_finished)
            return false;

        if (elapsed >= _duration)
        {
            Finish(OutboundRampCompletion.Completed);
            return true;
        }

        // Coalescing lives here: whatever the caller's cadence, at most one value goes out per interval
        // and it is always the CURRENT one. Nothing is buffered, so there is no stale backlog to replay.
        if (_lastSentAt != TimeSpan.MinValue && elapsed - _lastSentAt < _sendInterval)
            return false;

        _lastSentAt = elapsed;
        Emit(ValueAt(elapsed));
        return true;
    }

    /// <summary>
    /// Ends the ramp early - the cue stopped, or the show panicked. The terminal value is still sent:
    /// the receiving system owns that value, and leaving it stranded mid-fade is precisely the failure
    /// this class exists to prevent.
    /// </summary>
    public void Interrupt() => Finish(OutboundRampCompletion.Interrupted);

    private void Finish(OutboundRampCompletion completion)
    {
        if (_finished)
            return;
        _finished = true;
        _ = completion; // both paths land on the final value; the distinction is for callers' logging
        Emit(FinalValue);
    }

    /// <summary>Linear interpolation between the bracketing keyframes; flat outside the ramp.</summary>
    private double ValueAt(TimeSpan elapsed)
    {
        if (elapsed <= _points[0].Time)
            return _points[0].Value;

        for (var i = 1; i < _points.Count; i++)
        {
            var to = _points[i];
            if (elapsed > to.Time)
                continue;
            var from = _points[i - 1];
            var span = (to.Time - from.Time).TotalSeconds;
            if (span <= 0)
                return to.Value;
            var t = (elapsed - from.Time).TotalSeconds / span;
            return from.Value + (to.Value - from.Value) * t;
        }

        return FinalValue;
    }

    /// <summary>A failing endpoint must not kill the ramp - the next tick tries again, and the terminal
    /// value is still attempted at the end, which is how a recovering endpoint ends up correct.</summary>
    private void Emit(double value)
    {
        try
        {
            _send(value);
        }
        catch (Exception ex)
        {
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "OutboundRampRunner: a send failed ({Error}); the ramp continues and still lands on its " +
                "final value", ex.Message);
        }
    }
}
