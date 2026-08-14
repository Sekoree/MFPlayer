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

    // No Stop(): it was exactly Pause() with a "may diverge later" note - a semantic trap beside a
    // separate Reset() (2026-08-14 review F-13; removed per owner decision while the API has no
    // external consumers). A transport-stop is Pause() then Reset(), composed by the caller; an
    // atomic form can be designed if a consumer ever needs one, with its notification semantics
    // chosen deliberately rather than inherited.
    public void Start();
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
