namespace S.Media.Time;

/// <summary>
/// Public playhead surface: position, running state, nominal rate, and cooperative seek.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IMediaClock"/> extends this with tick events, start/stop/pause, optional
/// <see cref="IPlaybackClock"/> mastering, and <see cref="IMediaClock.PositionChanged"/>.
/// </para>
/// <para>
/// For a seek-free read-only dependency, use <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/>.
/// </para>
/// <para>
/// Other public clocks: <see cref="MediaClock"/> (driver), <see cref="VideoPtsClock"/>,
/// and NDI's <c>NDIIngestPlaybackClock</c> (ingest master). To hand a clock off between
/// references without a position jump, use <see cref="SessionClock.SetReference"/> - an explicit,
/// continuity-preserving swap - rather than letting a merge infer the handoff from which leaf
/// happens to be advancing.
/// </para>
/// </remarks>
public interface IPlayhead
{
    /// <summary>
    /// The playhead position on the media timeline. Naming disambiguation for the three
    /// position-like properties in the framework: this is the <em>clock-side</em> playhead;
    /// <c>ISeekableSource.Position</c> is the <em>decoder-side</em> consumed-samples position
    /// (can run ahead of audible output by the buffered amount); and
    /// <c>IPlaybackClock.ElapsedSinceStart</c> is the master clock's raw played-time feed
    /// the playhead is derived from.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    /// <summary>Effective speed relative to real time (1.0 = normal).</summary>
    double PlaybackRate { get; }

    /// <summary>
    /// Identity of the timebase <see cref="CurrentPosition"/> is measured in - the playhead-side twin of
    /// <see cref="IPlaybackClock.EpochId"/>. It takes a fresh <see cref="PlaybackEpoch.Next"/> id whenever the
    /// position is explicitly re-established (seek, reset, master swap) and never for position-continuous
    /// changes, so <see cref="CurrentPosition"/> is monotonic within one id. Defaults to
    /// <see cref="PlaybackEpoch.Single"/> for playheads that never reposition.
    /// </summary>
    long PositionEpoch => PlaybackEpoch.Single;

    /// <summary>One atomic (epoch, position, running) sample - see <see cref="IPlaybackClock.Read"/> for why
    /// the pair must come from a single read. Override wherever the three can change together.</summary>
    ClockReading ReadPosition() => new(PositionEpoch, CurrentPosition, IsRunning);

    void Seek(TimeSpan position);
}
