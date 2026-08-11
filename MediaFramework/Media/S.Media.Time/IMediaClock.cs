namespace S.Media.Time;

/// <summary>
/// Master playback driver: <see cref="IPlayhead"/> plus tick events, transport controls,
/// and optional slaving to an <see cref="IPlaybackClock"/> (typically the audio output).
/// </summary>
public interface IMediaClock : IPlayhead
{
    /// <summary>Raised when the position is explicitly re-established (<see cref="Seek"/>,
    /// <see cref="Reset"/>). This is a re-anchor notification, NOT a cadence - poll
    /// <see cref="IPlayhead.CurrentPosition"/> at your own display rate for a continuous readout.</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Render-at-this-cadence signal from the clock's driver. Wall-driven regardless of master
    /// attachment; it does not mean "media time advanced by one frame".</summary>
    public event EventHandler? VideoTick;

    public void Start();
    public void Stop(CancellationToken cancellationToken = default);
    public void Reset();

    /// <param name="cancellationToken">Thrown through while blocking on the timing driver shutdown.</param>
    public void Pause(CancellationToken cancellationToken = default);

    /// <summary>
    /// Slave the clock's position to an external <see cref="IPlaybackClock"/>
    /// (typically the audio output). Pass <c>null</c> to revert to the internal
    /// stopwatch. Position is preserved across the swap - no jump.
    /// </summary>
    public void SetMaster(IPlaybackClock? master);
}
