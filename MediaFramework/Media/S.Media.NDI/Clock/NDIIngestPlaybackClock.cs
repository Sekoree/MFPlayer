using System.Diagnostics;
using NDILib;
using S.Media.Time;
using S.Media.NDI;

namespace S.Media.NDI.Clock;

/// <summary>
/// Public NDI ingest master: <see cref="IPlaybackClock"/> driven by receiver audio timecode / timestamp
/// (100 ns units, same as <see cref="TimeSpan.Ticks"/>) plus wall-clock extrapolation between
/// captures - analogous to <see cref="VideoPtsClock"/> but fed from ingest.
/// </summary>
/// <remarks>
/// <para>
/// Wire <see cref="MediaClock.SetMaster"/> to this instance when playing out with NDI as the
/// timing authority. Call <see cref="Audio.AudioRouterNDIExtensions.SlaveToNDI"/> on an
/// <see cref="S.Media.Core.Audio.AudioRouter"/> to pace decode from ingest media time.
/// Pass the clock into <see cref="NDISource.Open"/> options so the capture
/// thread calls <see cref="NotifyAudioFrame"/> before <c>NDIlib_recv_free_audio</c>.
/// </para>
/// <para>
/// When neither timecode nor timestamp is valid, the clock chains sample durations from the
/// last known media end so progress still tracks audio block size.
/// </para>
/// <para>
/// <see cref="NDISource"/> invokes <see cref="AttachReceiver"/> when its capture starts so
/// a clock instance can be reused across receiver lifetimes.
/// </para>
/// </remarks>
public sealed class NDIIngestPlaybackClock : IPlaybackClock
{
    private readonly Lock _gate = new();
    private readonly Func<long> _getTimestamp;

    private long _liveWallEpochTicks;
    private long _sessionOriginTicks;
    private long _lastStreamEndTicks;
    private long _lastWallTicks;
    private bool _sessionStarted;
    private bool _advancing;
    private bool _paused;
    private bool _captureStopped;
    private TimeSpan _frozenElapsed;
    // High-water mark enforcing IPlaybackClock's per-epoch monotonic contract: _lastStreamEndTicks already
    // includes the full duration of the last frame, so a frame arriving LATER than its duration
    // would otherwise step the computed elapsed backwards (pre-arrival = prevEnd + wall, post =
    // prevEnd + duration). Reset only by Seek (a deliberate reposition) and AttachReceiver.
    private TimeSpan _maxReportedElapsed;
    // Both of those reset the reported timeline discontinuously, which is legal only across an epoch
    // boundary - so both take a fresh id and consumers compare instead of inferring one.
    private long _epochId = PlaybackEpoch.Next();

    public NDIIngestPlaybackClock()
        : this(Stopwatch.GetTimestamp)
    {
    }

    internal NDIIngestPlaybackClock(Func<long> getTimestamp)
    {
        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
    }

    /// <inheritdoc />
    public bool IsAdvancing
    {
        get
        {
            lock (_gate) return _advancing;
        }
    }

    /// <inheritdoc />
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate) return ComputeReportedElapsedUnlocked();
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
        lock (_gate) return new ClockReading(_epochId, ComputeReportedElapsedUnlocked(), _advancing);
    }

    private TimeSpan ComputeReportedElapsedUnlocked()
    {
        if (!_sessionStarted)
            return TimeSpan.Zero;
        return _advancing ? ComputeElapsedUnlocked() : _frozenElapsed;
    }

    /// <summary>
    /// Resets session state for a new receiver session using this clock.
    /// Called automatically by the receiver constructor.
    /// </summary>
    public void AttachReceiver()
    {
        lock (_gate)
        {
            _liveWallEpochTicks = _getTimestamp();
            _captureStopped = false;
            _paused = false;
            _sessionStarted = false;
            _sessionOriginTicks = 0;
            _lastStreamEndTicks = 0;
            _maxReportedElapsed = TimeSpan.Zero;
            _advancing = false;
            _epochId = PlaybackEpoch.Next(); // ingest relocate: the timeline restarts at zero
        }
    }

    /// <summary>
    /// Wall time since the last <see cref="AttachReceiver"/> - used for live playout PTS so video
    /// matches the audio ring (arrival order) and PortAudio, not the sender's NDI timecode.
    /// </summary>
    public TimeSpan SnapshotWallPresentationPosition()
    {
        lock (_gate)
            return Stopwatch.GetElapsedTime(_liveWallEpochTicks);
    }

    /// <summary>Updates the media timeline from a captured <see cref="NDIAudioFrameV3"/> (call before freeing the frame).</summary>
    public void NotifyAudioFrame(ref readonly NDIAudioFrameV3 audio) =>
        NotifyAudioFrame(audio.SampleRate, audio.NoSamples, audio.Timecode, audio.Timestamp);

    /// <summary>Updates the media timeline from a captured <see cref="NDIVideoFrameV2"/> (call before freeing the frame).</summary>
    public void NotifyVideoFrame(ref readonly NDIVideoFrameV2 video) =>
        NotifyVideoFrame(video.FrameRateN, video.FrameRateD, video.Timecode, video.Timestamp);

    /// <summary>Updates the media timeline from raw SDK fields (100 ns timebase where applicable).</summary>
    public void NotifyAudioFrame(int sampleRate, int noSamples, long timecode100Ns, long timestamp100Ns)
    {
        if (sampleRate <= 0 || noSamples <= 0)
            return;

        var durationTicks = FrameDurationTicks(sampleRate, noSamples);
        if (durationTicks <= 0)
            return;

        lock (_gate)
        {
            if (_captureStopped)
                return;
            if (_paused && _sessionStarted)
                return;

            var wallNow = _getTimestamp();
            long startTicks;
            if (NDIFrameTiming.TryGetFrameStartTicks(timecode100Ns, timestamp100Ns, out var absoluteStart))
                startTicks = absoluteStart;
            else
                startTicks = _sessionStarted ? _lastStreamEndTicks : 0;

            var endTicks = startTicks + durationTicks;
            if (_sessionStarted)
                endTicks = Math.Max(endTicks, _lastStreamEndTicks);

            if (!_sessionStarted)
            {
                _sessionOriginTicks = startTicks;
                _sessionStarted = true;
            }

            _lastStreamEndTicks = endTicks;
            _lastWallTicks = wallNow;
            _advancing = true;
        }
    }

    /// <summary>Updates the media timeline from a video frame (100 ns timebase where applicable).</summary>
    public void NotifyVideoFrame(int frameRateN, int frameRateD, long timecode100Ns, long timestamp100Ns)
    {
        var durationTicks = NDIFrameTiming.FrameDurationTicks(frameRateN, frameRateD);
        if (durationTicks <= 0)
            return;

        lock (_gate)
        {
            if (_captureStopped)
                return;
            if (_paused && _sessionStarted)
                return;

            var wallNow = _getTimestamp();
            long startTicks;
            if (NDIFrameTiming.TryGetFrameStartTicks(timecode100Ns, timestamp100Ns, out var absoluteStart))
                startTicks = absoluteStart;
            else
                startTicks = _sessionStarted ? _lastStreamEndTicks : 0;

            var endTicks = startTicks + durationTicks;
            if (_sessionStarted)
                endTicks = Math.Max(endTicks, _lastStreamEndTicks);

            if (!_sessionStarted)
            {
                _sessionOriginTicks = startTicks;
                _sessionStarted = true;
            }

            _lastStreamEndTicks = endTicks;
            _lastWallTicks = wallNow;
            _advancing = true;
        }
    }

    /// <summary>Call when the receiver capture thread stops (e.g. <see cref="NDISource.Dispose"/>).</summary>
    public void NotifyCaptureStopped()
    {
        lock (_gate)
        {
            if (_captureStopped)
                return;
            if (_sessionStarted)
            {
                if (_advancing)
                    _frozenElapsed = ComputeElapsedUnlocked();
            }
            else
                _frozenElapsed = TimeSpan.Zero;

            _advancing = false;
            _paused = false;
            _captureStopped = true;
        }
    }

    /// <inheritdoc cref="VideoPtsClock.Pause" />
    public void Pause()
    {
        lock (_gate)
        {
            if (!_advancing)
                return;
            _frozenElapsed = ComputeElapsedUnlocked();
            _advancing = false;
            _paused = true;
        }
    }

    /// <inheritdoc cref="VideoPtsClock.Resume" />
    public void Resume()
    {
        lock (_gate)
        {
            if (_captureStopped)
                return;
            if (_advancing)
                return;
            if (!_paused)
                return;
            _lastWallTicks = _getTimestamp();
            _advancing = true;
            _paused = false;
        }
    }

    /// <inheritdoc cref="VideoPtsClock.Seek" />
    public void Seek(TimeSpan mediaPosition)
    {
        if (mediaPosition < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mediaPosition));

        lock (_gate)
        {
            if (!_sessionStarted)
                return;

            var current = _advancing ? ComputeElapsedUnlocked() : _frozenElapsed;
            if (current < TimeSpan.Zero)
                current = TimeSpan.Zero;

            var shiftTicks = mediaPosition.Ticks - current.Ticks;
            _sessionOriginTicks -= shiftTicks;
            _maxReportedElapsed = mediaPosition; // deliberate reposition may go backwards
            _epochId = PlaybackEpoch.Next();     // ...which is only legal across an epoch boundary
            if (!_advancing)
                _frozenElapsed = mediaPosition;
        }
    }

    private TimeSpan ComputeElapsedUnlocked()
    {
        var wallNow = _getTimestamp();
        var media = TimeSpan.FromTicks(_lastStreamEndTicks - _sessionOriginTicks);
        var wallExtras = Stopwatch.GetElapsedTime(_lastWallTicks, wallNow);
        var total = media + wallExtras;
        if (total < TimeSpan.Zero)
            total = TimeSpan.Zero;
        if (total < _maxReportedElapsed)
            return _maxReportedElapsed;
        _maxReportedElapsed = total;
        return total;
    }

    private static long FrameDurationTicks(int sampleRate, int samples) =>
        (long)Math.Round(samples * (double)TimeSpan.TicksPerSecond / sampleRate);
}
