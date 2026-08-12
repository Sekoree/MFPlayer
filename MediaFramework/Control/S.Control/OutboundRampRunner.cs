using S.Media.Session;

namespace S.Control;

/// <summary>One keyframe of an outbound ramp: a value the sender should be at, at a time.</summary>
/// <param name="Time">Offset from the ramp's start.</param>
/// <param name="Value">The value to send.</param>
/// <param name="CurveToNext">Shape of the segment beginning at this point.</param>
public readonly record struct OutboundRampPoint(
    TimeSpan Time,
    double Value,
    FadeCurve CurveToNext = FadeCurve.Linear);

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
    private readonly Action<double>? _send;
    private readonly Func<double, CancellationToken, ValueTask>? _sendAsync;
    private readonly TimeSpan _sendInterval;
    private readonly TimeSpan _duration;

    private TimeSpan _lastSentAt = TimeSpan.MinValue;
    private volatile bool _finished;
    private volatile bool _finishRequested;
    private readonly Lock _sendGate = new();
    private bool _hasPending;
    private double _pendingValue;
    private bool _pendingIsFinal;
    private Task? _sendWorker;

    /// <param name="points">Keyframes, sorted by time. At least one is required.</param>
    /// <param name="send">A synchronous actuator. Use the async overload for an endpoint that may block.</param>
    /// <param name="sendRateHz">Emission rate. Must be positive.</param>
    public OutboundRampRunner(
        IReadOnlyList<OutboundRampPoint> points,
        Action<double> send,
        double sendRateHz = DefaultSendRateHz)
    {
        ArgumentNullException.ThrowIfNull(send);
        Validate(points, sendRateHz);
        _points = points;
        _send = send;
        _sendInterval = TimeSpan.FromSeconds(1d / sendRateHz);
        _duration = points[^1].Time;
    }

    /// <summary>
    /// Async actuator overload for endpoints whose send can block. At most one send is in flight and one
    /// newest value is retained; intermediate values are overwritten. A failed terminal send remains
    /// pending and is retried by the next <see cref="Advance"/> or <see cref="Interrupt"/> call.
    /// </summary>
    public OutboundRampRunner(
        IReadOnlyList<OutboundRampPoint> points,
        Func<double, CancellationToken, ValueTask> sendAsync,
        double sendRateHz = DefaultSendRateHz)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        Validate(points, sendRateHz);
        _points = points;
        _sendAsync = sendAsync;
        _sendInterval = TimeSpan.FromSeconds(1d / sendRateHz);
        _duration = points[^1].Time;
    }

    private static void Validate(IReadOnlyList<OutboundRampPoint> points, double sendRateHz)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("an outbound ramp needs at least one keyframe", nameof(points));
        if (!double.IsFinite(sendRateHz) || sendRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sendRateHz));
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Time < TimeSpan.Zero)
                throw new ArgumentException("keyframe times cannot be negative", nameof(points));
            if (!double.IsFinite(points[i].Value))
                throw new ArgumentException("keyframe values must be finite", nameof(points));
            if (!Enum.IsDefined(points[i].CurveToNext))
                throw new ArgumentException("keyframe curves must be defined", nameof(points));
            if (i > 0 && points[i].Time < points[i - 1].Time)
                throw new ArgumentException("keyframes must be sorted by time", nameof(points));
        }
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
    /// <returns>True when a value was offered to the actuator.</returns>
    public bool Advance(TimeSpan elapsed)
    {
        if (_finished)
            return false;

        if (_finishRequested)
        {
            if (AsyncSendIsInFlight())
                return false;
            Finish(OutboundRampCompletion.Completed); // retry a failed/pending terminal delivery
            return true;
        }

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
        Emit(ValueAt(elapsed), isFinal: false);
        return true;
    }

    /// <summary>
    /// Samples a discontinuous position immediately. Used for transport seeks and loop wraps: the
    /// receiving system must land at the sought value before ordinary rate-limited progression resumes.
    /// </summary>
    public void Reposition(TimeSpan elapsed)
    {
        var at = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > _duration ? _duration : elapsed;
        _finishRequested = false;
        _finished = false;
        _lastSentAt = at;
        Emit(ValueAt(at), isFinal: false);
    }

    /// <summary>
    /// Ends early at the value sampled at <paramref name="elapsed"/>. Unlike <see cref="Interrupt"/>,
    /// this never jumps an external controller to the authored terminal key.
    /// </summary>
    public void Freeze(TimeSpan elapsed)
    {
        if (_finished)
            return;
        var at = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > _duration ? _duration : elapsed;
        _finishRequested = true;
        if (Emit(ValueAt(at), isFinal: true) && _send is not null)
            _finished = true;
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
        _finishRequested = true;
        _ = completion; // both paths land on the final value; the distinction is for callers' logging
        if (Emit(FinalValue, isFinal: true) && _send is not null)
            _finished = true;
    }

    private bool AsyncSendIsInFlight()
    {
        if (_sendAsync is null)
            return false;
        lock (_sendGate)
            return _sendWorker is { IsCompleted: false };
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
            var shaped = to.Value < from.Value
                ? FadeCurves.ShapeProgress(1d - t, from.CurveToNext)
                : FadeCurves.ShapeProgress(t, from.CurveToNext);
            return to.Value < from.Value
                ? to.Value + (from.Value - to.Value) * shaped
                : from.Value + (to.Value - from.Value) * shaped;
        }

        return FinalValue;
    }

    /// <summary>A failing endpoint must not kill the ramp - the next tick tries again, and the terminal
    /// value is still attempted at the end, which is how a recovering endpoint ends up correct.</summary>
    private bool Emit(double value, bool isFinal)
    {
        if (_sendAsync is not null)
        {
            lock (_sendGate)
            {
                _pendingValue = value;
                _pendingIsFinal = isFinal;
                _hasPending = true;
                if (_sendWorker is null || _sendWorker.IsCompleted)
                    _sendWorker = Task.Run(DrainAsync);
            }
            return true;
        }

        try
        {
            _send!(value);
            return true;
        }
        catch (Exception ex)
        {
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "OutboundRampRunner: a send failed ({Error}); the ramp continues and still lands on its " +
                "final value", ex.Message);
            return false;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            double value;
            bool isFinal;
            lock (_sendGate)
            {
                if (!_hasPending)
                {
                    _sendWorker = null;
                    return;
                }
                value = _pendingValue;
                isFinal = _pendingIsFinal;
                _hasPending = false;
            }

            try
            {
                await _sendAsync!(value, CancellationToken.None).ConfigureAwait(false);
                if (isFinal)
                {
                    lock (_sendGate)
                    {
                        // A seek/loop can coalesce a newer non-terminal value while the old terminal
                        // send is in flight. That newer position re-opened the runner and must win.
                        if (!_hasPending)
                            _finished = true;
                    }
                }
            }
            catch (Exception ex)
            {
                S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                    "OutboundRampRunner: an async send failed ({0}); the newest value remains coalesced for retry",
                    ex.Message);
                var newerPending = false;
                if (isFinal)
                {
                    lock (_sendGate)
                    {
                        // Never overwrite a value offered after this failed send. Once final is requested,
                        // all later offers are final too, but preserving the newest is the general rule.
                        if (!_hasPending)
                        {
                            _pendingValue = value;
                            _pendingIsFinal = true;
                            _hasPending = true;
                        }
                        _sendWorker = null;
                    }
                }
                else
                {
                    lock (_sendGate)
                    {
                        newerPending = _hasPending;
                        if (!newerPending)
                            _sendWorker = null;
                    }
                }
                if (newerPending)
                    continue; // discard the failed stale value and deliver the newest coalesced one
                return; // retry only on a future caller tick; never spin on an unreachable desk
            }
        }
    }

    /// <summary>Waits for the currently in-flight async send. A failed terminal value may remain pending.</summary>
    public async ValueTask WaitForPendingSendAsync()
    {
        Task? worker;
        lock (_sendGate)
            worker = _sendWorker;
        if (worker is not null)
            await worker.ConfigureAwait(false);
    }
}
