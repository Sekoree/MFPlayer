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

    public async Task WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = target - Position;
            if (remaining <= TimeSpan.Zero)
                return;

            var wait = remaining > MaxPoll ? MaxPoll : remaining;
            if (wait < MinimumPoll)
                wait = MinimumPoll;
            await _host.DelayTimelineAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
