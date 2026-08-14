using S.Media.Time;

namespace HaCue2.Engine;

/// <summary>One immutable observation of a pause-aware authored-time run.</summary>
internal readonly record struct RunClockSnapshot(TimeSpan Position, long Generation);

/// <summary>
/// A seekable, pause-aware coordinate over the show's late-bound master clock. Group timelines,
/// controller automation and outbound actuators all consume this clock so none invent wall time.
/// </summary>
internal sealed class AutomationRunClock
{
    private static readonly TimeSpan MaxPoll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MinimumPoll = TimeSpan.FromMilliseconds(1);

    /// <summary>Inside this distance of the target the fine 1–5 ms poll runs, exactly as it always
    /// has, so dispatch lateness is unchanged.</summary>
    private static readonly TimeSpan FineApproach = TimeSpan.FromSeconds(1);

    /// <summary>The coarse tier's ceiling. Far from the target the clock cannot need millisecond
    /// answers - but it CAN be seeked or paused at any moment, so the nap stays short enough that a
    /// jump toward the edge is noticed well inside the fine-approach window.</summary>
    private static readonly TimeSpan CoarsePollCeiling = TimeSpan.FromMilliseconds(250);

    private readonly ICueExecutionHost _host;
    private readonly SessionClock _master;
    private readonly object _gate = new();
    private TimeSpan _masterAnchor;
    private TimeSpan _pausedAnchor;
    private TimeSpan _positionAnchor;
    private long _generation;

    public AutomationRunClock(ICueExecutionHost host, TimeSpan initialPosition)
    {
        _host = host;
        _master = new SessionClock(host.TimelineClock);
        _masterAnchor = _master.Now;
        _pausedAnchor = host.TimelinePausedElapsed;
        _positionAnchor = initialPosition > TimeSpan.Zero ? initialPosition : TimeSpan.Zero;
    }

    public TimeSpan Position => Read().Position;

    public RunClockSnapshot Read()
    {
        lock (_gate)
        {
            var position = _positionAnchor
                           + (_master.Now - _masterAnchor)
                           - (_host.TimelinePausedElapsed - _pausedAnchor);
            return new RunClockSnapshot(position > TimeSpan.Zero ? position : TimeSpan.Zero, _generation);
        }
    }

    /// <summary>Moves authored time immediately while preserving future pause-aware progression.</summary>
    public RunClockSnapshot Seek(TimeSpan position)
    {
        lock (_gate)
        {
            _positionAnchor = position > TimeSpan.Zero ? position : TimeSpan.Zero;
            _masterAnchor = _master.Now;
            _pausedAnchor = _host.TimelinePausedElapsed;
            _generation++;
            return new RunClockSnapshot(_positionAnchor, _generation);
        }
    }

    /// <remarks>
    /// Two-tier: far from the target it naps at up to a quarter second, and only inside the last
    /// second does it drop to the 1–5 ms fine poll. The fine approach is what bounds dispatch
    /// lateness, and it is unchanged - but a timeline whose next authored edge is ten minutes away
    /// used to wake two hundred times a second for the whole ten minutes. Pause and seek both move
    /// the coordinate this reads, so the coarse tier still notices them within one nap.
    /// </remarks>
    public async Task WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = target - Position;
            if (remaining <= TimeSpan.Zero)
                return;

            TimeSpan wait;
            if (remaining > FineApproach)
            {
                // Half the distance, capped: a seek can only shorten the remaining time by so much
                // per nap before the next pass re-measures.
                var coarse = TimeSpan.FromTicks((remaining - FineApproach).Ticks / 2);
                wait = coarse > CoarsePollCeiling ? CoarsePollCeiling
                    : coarse < MaxPoll ? MaxPoll : coarse;
            }
            else
            {
                wait = remaining > MaxPoll ? MaxPoll : remaining;
                if (wait < MinimumPoll)
                    wait = MinimumPoll;
            }

            await _host.DelayTimelineAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
