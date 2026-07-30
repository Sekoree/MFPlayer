using System.Diagnostics;

namespace S.Media.Time;

/// <summary>
/// Public master clock: driven by the most recently presented video frame PTS plus
/// wall-clock delta. Intended for file or VFR video when no audio
/// <see cref="IPlaybackClock"/> is available as a <see cref="MediaClock"/> master.
/// </summary>
/// <remarks>
/// Call <see cref="NotifyFramePts"/> whenever a frame is shown (or decoded as a
/// proxy). <see cref="ElapsedSinceStart"/> is
/// <c>(lastPts - sessionOriginPts) + wallDeltaSinceLastPts</c>, clamped to be
/// non‑negative. Pause/resume freezes or restarts wall advancement without
/// changing the media anchor.
/// </remarks>
public sealed class VideoPtsClock : IPlaybackClock
{
    private readonly Lock _gate = new();

    private TimeSpan _sessionOriginPts;
    private TimeSpan _lastPts;
    private long _lastWallTicks;
    private bool _advancing;
    private TimeSpan _frozenElapsed;
    /// <summary>Current epoch; a new id at each re-anchor of the PTS origin (<see cref="BeginSession"/>,
    /// <see cref="Seek"/>) - both make elapsed jump discontinuously.</summary>
    private long _epochId = PlaybackEpoch.Next();
    /// <summary>Highest elapsed reported in the current epoch - enforcement of
    /// <see cref="IPlaybackClock"/>'s per-epoch monotonic contract (same role as
    /// <c>NDIIngestPlaybackClock._maxReportedElapsed</c>). A late PTS, or a
    /// <see cref="Pause"/>/<see cref="Resume"/> pair, must not move the report backwards inside one epoch;
    /// only the deliberate re-anchors that bump <see cref="_epochId"/> may reset it.</summary>
    private TimeSpan _maxReportedElapsed;

    /// <inheritdoc />
    public bool IsAdvancing
    {
        get
        {
            lock (_gate) return _advancing;
        }
    }

    /// <inheritdoc />
    public long EpochId
    {
        get
        {
            lock (_gate) return _epochId;
        }
    }

    /// <inheritdoc />
    public ClockReading Read()
    {
        lock (_gate) return new ClockReading(_epochId, ComputeElapsedUnlocked(), _advancing);
    }

    /// <inheritdoc />
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate) return ComputeElapsedUnlocked();
        }
    }

    /// <summary>Anchor session time so the first presented PTS maps to zero elapsed (unless adjusted by <see cref="Seek"/>).</summary>
    public void BeginSession(TimeSpan firstPresentationPts)
    {
        lock (_gate)
        {
            _sessionOriginPts = firstPresentationPts;
            _lastPts = firstPresentationPts;
            _lastWallTicks = Stopwatch.GetTimestamp();
            _advancing = true;
            _frozenElapsed = TimeSpan.Zero;
            _maxReportedElapsed = TimeSpan.Zero;
            _epochId = PlaybackEpoch.Next();
        }
    }

    /// <summary>Updates the last known PTS and the wall-clock anchor used for interpolation.</summary>
    public void NotifyFramePts(TimeSpan presentationPts)
    {
        lock (_gate)
        {
            if (!_advancing) return;
            _lastPts = presentationPts;
            _lastWallTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Freeze <see cref="ElapsedSinceStart"/> at its current value.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_advancing) return;
            _frozenElapsed = ComputeElapsedUnlocked();
            _advancing = false;
        }
    }

    /// <summary>
    /// Resume wall-clock interpolation from the position <see cref="Pause"/> froze at.
    /// </summary>
    /// <remarks>
    /// A resume is position-CONTINUOUS, so it takes no new epoch - which makes it bound by the per-epoch
    /// monotonic contract. <see cref="Pause"/> froze at <c>(lastPts - origin) + wallDelta</c>, so simply
    /// flipping <see cref="_advancing"/> back on made the next read return <c>(lastPts - origin)</c>: LOWER
    /// than the value already reported, by however much wall time had accrued past the last frame. The PTS
    /// origin is re-derived from the frozen value instead, so the resumed reading starts exactly where the
    /// pause left off and advances from there.
    /// </remarks>
    public void Resume()
    {
        lock (_gate)
        {
            if (_advancing) return;
            // (lastPts - origin) == frozenElapsed ⇒ the first resumed read equals the frozen value.
            _sessionOriginPts = _lastPts - _frozenElapsed;
            _lastWallTicks = Stopwatch.GetTimestamp();
            _advancing = true;
        }
    }

    /// <summary>Re-anchor so that <see cref="ElapsedSinceStart"/> equals <paramref name="mediaPosition"/> at this instant.</summary>
    public void Seek(TimeSpan mediaPosition)
    {
        if (mediaPosition < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mediaPosition));

        lock (_gate)
        {
            var wallNow = Stopwatch.GetTimestamp();
            var current = _advancing
                ? _lastPts - _sessionOriginPts + Stopwatch.GetElapsedTime(_lastWallTicks, wallNow)
                : _frozenElapsed;
            if (current < TimeSpan.Zero) current = TimeSpan.Zero;

            var shift = mediaPosition - current;
            _sessionOriginPts -= shift;
            if (!_advancing)
                _frozenElapsed = mediaPosition;
            // A deliberate reposition: elapsed may move backwards, which is only legal across an epoch - so
            // the monotonic high-water is reset with the id rather than pinning the new position up.
            _maxReportedElapsed = mediaPosition;
            _epochId = PlaybackEpoch.Next();
        }
    }

    private TimeSpan ComputeElapsedUnlocked()
    {
        if (!_advancing)
            return _frozenElapsed;
        var wallNow = Stopwatch.GetTimestamp();
        var delta = _lastPts - _sessionOriginPts + Stopwatch.GetElapsedTime(_lastWallTicks, wallNow);
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;
        if (delta < _maxReportedElapsed)
            return _maxReportedElapsed;
        _maxReportedElapsed = delta;
        return delta;
    }
}
