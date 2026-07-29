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

    /// <summary>Resume wall-clock interpolation from the last notified PTS.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_advancing) return;
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
            // A deliberate reposition: elapsed may move backwards, which is only legal across an epoch.
            _epochId = PlaybackEpoch.Next();
        }
    }

    private TimeSpan ComputeElapsedUnlocked()
    {
        if (!_advancing)
            return _frozenElapsed;
        var wallNow = Stopwatch.GetTimestamp();
        var delta = _lastPts - _sessionOriginPts + Stopwatch.GetElapsedTime(_lastWallTicks, wallNow);
        return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
    }
}
