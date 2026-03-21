using Seko.OwnAudioNET.Video.Decoders;
using Seko.OwnAudioNET.Video.Clocks;

namespace Seko.OwnAudioNET.Video.Sources;


/// <summary>
/// Clock-driven video source that decodes frames via an <see cref="IVideoDecoder"/> and presents
/// them against an attached shared playback clock.
/// Intended for source-based transport/mixer scenarios; direct decoder-to-output playback should use
/// <see cref="FFVideoDecoder"/> + <c>OpenGLVideoEngine.PushFrame</c> instead.
/// <para>
/// A dedicated background thread pre-fills a bounded queue of decoded <see cref="VideoFrame"/>
/// objects. On each call to <see cref="RequestNextFrame"/> (or <see cref="TryGetFrameAtTime"/>) the
/// source promotes the next due frame, raises its frame-ready events,
/// and returns the current frame to the caller.
/// </para>
/// </summary>
public sealed class VideoStreamSource : BaseVideoSource
{
    private readonly record struct DecodedFrameEntry(VideoFrame Frame, long SeekEpoch);
    private const double DuplicateRequestWindowSeconds = 0.002;
    private const double LateFrameToleranceFloorSeconds = 0.004;
    private const double StarvationRecoveryToleranceFloorSeconds = 0.120;
    private const int ProactiveInlineDecodeLowQueueThreshold = 1;
    private const int InlineRecoveryPrefetchQueueTarget = 4;
    private const int DefaultQueueCapacity = 6;

    private readonly Lock _syncLock = new();
    private readonly Lock _decoderLock = new();
    private readonly IVideoDecoder _videoDecoder;
    private readonly bool _ownsDecoder;
    private readonly VideoStreamSourceOptions _options;
    private readonly int _queueCapacity;
    private readonly bool _useDedicatedDecodeThread;

    private readonly Queue<DecodedFrameEntry> _decodeQueue;
    private readonly AutoResetEvent _decodeWakeEvent = new(false);
    private Thread? _decodeThread;
    private volatile bool _decodeThreadRunning;

    private IVideoClock? _masterClock;
    private VideoFrame? _currentFrame;
    private bool _hasCurrentFrame;
    private VideoFrame? _pendingFrame;
    private bool _hasPendingFrame;
    private double _lastServedMasterTimestamp = double.NegativeInfinity;
    private bool _disposed;
    private long _decodedFrameCount;
    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private long _decodeQueueDepth;
    private double _startOffsetSeconds;
    private double _driftCorrectionOffsetSeconds;
    private double _currentFramePtsSeconds = double.NaN;
    private double _lastPromotedMasterTimestamp = double.NaN;
    private double _frameDurationSeconds;
    private long _seekEpoch;
    private double _seekPresentationFloorSeconds = double.NegativeInfinity;
    private const double SeekFrameToleranceFloorSeconds = 0.001;


    /// <summary>Initializes a new instance for the given decoder with default options.</summary>
    public VideoStreamSource(IVideoDecoder videoDecoder, bool ownsDecoder = false)
        : this(videoDecoder, new VideoStreamSourceOptions(), ownsDecoder)
    {
    }

    /// <summary>Initializes a new instance for the given decoder with the specified options.</summary>
    public VideoStreamSource(IVideoDecoder videoDecoder, VideoStreamSourceOptions options, bool ownsDecoder = false)
    {
        ArgumentNullException.ThrowIfNull(videoDecoder);
        _options = options;
        _videoDecoder = videoDecoder;
        _ownsDecoder = ownsDecoder;
        _queueCapacity = ResolveQueueCapacity(_videoDecoder);
        _useDedicatedDecodeThread = ResolveUseDedicatedDecodeThread(_videoDecoder);
        _frameDurationSeconds = ResolveFrameDurationSeconds(_videoDecoder.StreamInfo.FrameRate);
        _videoDecoder.StreamInfoChanged += OnDecoderStreamInfoChanged;
        _decodeQueue = new Queue<DecodedFrameEntry>(_queueCapacity);
        StartDecodeThreadIfNeeded();
    }

    /// <summary>Metadata describing the underlying video stream.</summary>
    public override VideoStreamInfo StreamInfo => _videoDecoder.StreamInfo;

    /// <summary><see langword="true"/> when a hardware-accelerated decode context is active.</summary>
    public override bool IsHardwareDecoding => _videoDecoder.IsHardwareDecoding;

    /// <summary><see langword="true"/> once the decoder has consumed and flushed all frames.</summary>
    public override bool IsEndOfStream =>
        State == VideoPlaybackState.EndOfStream ||
        (_videoDecoder.IsEndOfStream && !_hasCurrentFrame && !_hasPendingFrame && QueueDepth == 0);

    /// <summary>
    /// Master clock offset in seconds. The source treats <c>masterTimestamp - StartOffset</c> as
    /// the stream-relative playback position.
    /// </summary>
    public override double StartOffset
    {
        get => Volatile.Read(ref _startOffsetSeconds);
        set
        {
            Volatile.Write(ref _startOffsetSeconds, value);
            Volatile.Write(ref _driftCorrectionOffsetSeconds, 0);
        }
    }

    /// <summary><see langword="true"/> when a playback clock is attached.</summary>
    public override bool IsAttachedToClock => _masterClock != null;

    /// <summary>Total number of frames decoded by the background thread.</summary>
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrameCount);

    /// <summary>Total number of frames promoted to the current-frame slot and fired via <see cref="FrameReady"/>.</summary>
    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);

    /// <summary>Total number of frames discarded due to late arrival or queue overflow.</summary>
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);

    /// <summary>PTS (seconds) of the most recently promoted frame, or <see cref="double.NaN"/> before the first frame.</summary>
    public double CurrentFramePtsSeconds => Interlocked.CompareExchange(ref _currentFramePtsSeconds, 0, 0);

    /// <summary>Master clock timestamp at which the last frame was promoted.</summary>
    public double LastPromotedMasterTimestamp => Interlocked.CompareExchange(ref _lastPromotedMasterTimestamp, 0, 0);

    /// <summary>Current timing correction offset applied by the owning transport/mixer.</summary>
    public double CurrentDriftCorrectionOffsetSeconds => Volatile.Read(ref _driftCorrectionOffsetSeconds);

    internal void ResetDriftCorrectionOffset()
    {
        lock (_syncLock)
            ResetDriftCorrectionOffsetUnsafe();
    }

    internal void ApplyDriftCorrectionDelta(double deltaSeconds, double maxAbsoluteOffsetSeconds)
    {
        if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || Math.Abs(deltaSeconds) < 1e-9)
            return;

        lock (_syncLock)
        {
            var currentCorrection = Volatile.Read(ref _driftCorrectionOffsetSeconds);
            var nextCorrection = Math.Clamp(currentCorrection + deltaSeconds, -Math.Abs(maxAbsoluteOffsetSeconds), Math.Abs(maxAbsoluteOffsetSeconds));
            var appliedDelta = nextCorrection - currentCorrection;
            if (Math.Abs(appliedDelta) < 1e-9)
                return;

            Volatile.Write(ref _startOffsetSeconds, Volatile.Read(ref _startOffsetSeconds) + appliedDelta);
            Volatile.Write(ref _driftCorrectionOffsetSeconds, nextCorrection);
        }
    }

    private void ResetDriftCorrectionOffsetUnsafe()
    {
        var currentCorrection = Volatile.Read(ref _driftCorrectionOffsetSeconds);
        if (Math.Abs(currentCorrection) < 1e-9)
            return;

        Volatile.Write(ref _startOffsetSeconds, Volatile.Read(ref _startOffsetSeconds) - currentCorrection);
        Volatile.Write(ref _driftCorrectionOffsetSeconds, 0);
    }

    /// <summary>Number of frames currently held in the decode pre-fetch queue.</summary>
    public int QueueDepth
    {
        get => (int)Math.Max(0, Interlocked.Read(ref _decodeQueueDepth));
    }

    /// <summary>Last decoder source pixel format name (FFmpeg AVPixelFormat string).</summary>
    public string DecoderSourcePixelFormatName =>
        _videoDecoder is FFVideoDecoder ffVideoDecoder
            ? ffVideoDecoder.LastSourcePixelFormatName
            : "unknown";

    /// <summary>Current decoder output pixel format name.</summary>
    public string DecoderOutputPixelFormatName =>
        _videoDecoder is FFVideoDecoder ffVideoDecoder
            ? ffVideoDecoder.LastOutputPixelFormatName
            : StreamInfo.PixelFormat.ToString();


    /// <summary>
    /// Attaches the source to a <see cref="MasterClock"/>. If the clock is already running ahead
    /// of <see cref="StartOffset"/> the source seeks to the current clock position.
    /// </summary>
    public override void AttachToClock(IVideoClock clock)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(clock);

        lock (_syncLock)
        {
            _masterClock = clock;
            IsSynchronized = true;

            var relativeTime = clock.CurrentTimestamp - StartOffset;
            if (relativeTime > 0)
            {
                SeekInternal(relativeTime);
            }
            else
            {
                ClearFrameCache();
                SetPosition(0);
                SetSamplePosition(0);
            }
        }
    }

    /// <summary>Detaches the source from its current <see cref="MasterClock"/>.</summary>
    public override void DetachFromClock()
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            _masterClock = null;
            IsSynchronized = false;
        }
    }

    /// <inheritdoc/>
    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds))
            return false;

        var clamped = ClampToDuration(positionInSeconds);
        lock (_syncLock)
        {
            var priorState = State;
            if (!SeekInternal(clamped))
                return false;

            if (priorState == VideoPlaybackState.EndOfStream)
                SetState(VideoPlaybackState.Paused);
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool SeekToFrame(long frameIndex)
    {
        ThrowIfDisposed();

        if (frameIndex < 0)
            return false;

        var frameCount = StreamInfo.FrameCount;
        if (frameCount.HasValue && frameIndex >= frameCount.Value)
            return false;

        var frameRate = StreamInfo.FrameRate;
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            return false;

        var positionSeconds = frameIndex / frameRate;
        return Seek(positionSeconds);
    }

    /// <inheritdoc/>
    public override bool SeekToStart()
    {
        return Seek(0);
    }

    /// <inheritdoc/>
    public override bool SeekToEnd()
    {
        var frameCount = StreamInfo.FrameCount;
        if (frameCount is > 0)
            return SeekToFrame(frameCount.Value - 1);

        var duration = Duration;
        if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration))
            return false;

        var target = Math.Max(0, duration - Math.Max(_frameDurationSeconds, 0.001));
        return Seek(target);
    }

    /// <summary>
    /// Attempts to return the frame that should be displayed at <paramref name="masterTimestamp"/>.
    /// Promotes a pending frame when its PTS is due and raises frame-ready events on promotion.
    /// </summary>
    /// <param name="masterTimestamp">Absolute master clock position in seconds.</param>
    /// <param name="frame">The current frame on success.</param>
    /// <returns><see langword="true"/> if a frame is available.</returns>
    public override bool TryGetFrameAtTime(double masterTimestamp, out VideoFrame frame)
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            var playbackState = State;
            if (playbackState == VideoPlaybackState.Stopped)
            {
                frame = _hasCurrentFrame ? _currentFrame! : default!;
                return _hasCurrentFrame;
            }

            var relativeTime = ResolveRelativeTime(masterTimestamp, playbackState);

            if (relativeTime < 0)
            {
                frame = default!;
                return false;
            }

            TryRecoverFromStarvation(relativeTime);

            var seekEpoch = Volatile.Read(ref _seekEpoch);

            // Multiple views can request at effectively the same clock time.
            // Avoid consuming extra frames for duplicate pulls within one tick.
            var currentFrame = _currentFrame;
            var masterDelta = masterTimestamp - _lastServedMasterTimestamp;
            if (playbackState == VideoPlaybackState.Playing &&
                _hasCurrentFrame &&
                currentFrame != null &&
                masterDelta >= 0 &&
                masterDelta <= DuplicateRequestWindowSeconds)
            {
                frame = currentFrame;
                UpdateEndOfStreamState(relativeTime);
                return true;
            }

            PromoteReadyFrames(masterTimestamp, relativeTime, seekEpoch);

            if (!_hasCurrentFrame && !EnsurePendingFrame(seekEpoch, relativeTime))
            {
                UpdateEndOfStreamState(relativeTime);
                frame = _hasCurrentFrame ? _currentFrame! : default!;
                return _hasCurrentFrame;
            }

            frame = _hasCurrentFrame ? _currentFrame! : default!;

            if (playbackState == VideoPlaybackState.Playing)
                _lastServedMasterTimestamp = masterTimestamp;

            UpdateEndOfStreamState(relativeTime);
            return _hasCurrentFrame;
        }
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryGetFrameAtTime"/> using the attached
    /// attached <see cref="Clocks.IVideoClock"/>'s current timestamp.
    /// This is the primary method the render loop should call each frame.
    /// </summary>
    /// <param name="frame">The current frame on success.</param>
    /// <returns><see langword="false"/> if no clock is attached or no frame is yet available.</returns>
    public override bool RequestNextFrame(out VideoFrame frame)
    {
        ThrowIfDisposed();
        if (_masterClock == null)
        {
            frame = default!;
            return false;
        }

        return TryGetFrameAtTime(_masterClock.CurrentTimestamp, out frame);
    }

    /// <summary>
    /// Seeks to the position corresponding to <paramref name="samplePosition"/> on the attached
    /// clock. Called by the synchronisation layer when a hard re-sync is required.
    /// </summary>
    public override void ResyncTo(long samplePosition)
    {
        ThrowIfDisposed();

        if (_masterClock == null)
            return;

        var targetSeconds = samplePosition / (double)_masterClock.SampleRate;
        lock (_syncLock)
        {
            SeekInternal(targetSeconds);
        }
    }

    /// <summary>Stops the decode thread, disposes all buffered frames and optionally the underlying decoder.</summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            base.Dispose(disposing);

            _decodeThreadRunning = false;
            _decodeWakeEvent.Set();
            if (_decodeThread is { IsAlive: true })
                _decodeThread.Join(TimeSpan.FromSeconds(1));
            _decodeWakeEvent.Dispose();

            _videoDecoder.StreamInfoChanged -= OnDecoderStreamInfoChanged;

            ClearFrameCache();
            ClearDecodeQueue();
            _masterClock = null;
            IsSynchronized = false;

            if (_ownsDecoder)
            {
                lock (_decoderLock)
                    _videoDecoder.Dispose();
            }
        }

        _disposed = true;
    }

    private void PromotePendingFrame(double masterTimestamp)
    {
        if (!_hasPendingFrame)
            return;

        DisposeFrame(_currentFrame);
        _currentFrame = _pendingFrame;
        _hasCurrentFrame = true;
        _hasPendingFrame = false;
        _pendingFrame = null;

        if (_masterClock != null)
            SetSamplePosition((long)(_currentFrame!.PtsSeconds * _masterClock.SampleRate));

        SetPosition(_currentFrame!.PtsSeconds);

        Interlocked.Exchange(ref _currentFramePtsSeconds, _currentFrame!.PtsSeconds);
        Interlocked.Exchange(ref _lastPromotedMasterTimestamp, masterTimestamp);
        Interlocked.Increment(ref _presentedFrameCount);

        var currentFrame = _currentFrame!;
        SetState(VideoPlaybackState.Playing);
        RaiseFrameReady(currentFrame, masterTimestamp);
    }

    private void DropPendingFrame()
    {
        if (!_hasPendingFrame)
            return;

        DisposeFrame(_pendingFrame);
        _pendingFrame = null;
        _hasPendingFrame = false;
        Interlocked.Increment(ref _droppedFrameCount);
    }

    private static double ResolveFrameDurationSeconds(double frameRate)
    {
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            return 1.0 / 30.0;

        return 1.0 / frameRate;
    }


    private static int ResolveQueueCapacity(IVideoDecoder decoder)
    {
        return decoder is FFVideoDecoder ffVideoDecoder
            ? Math.Max(2, ffVideoDecoder.QueueCapacity)
            : DefaultQueueCapacity;
    }

    private static bool ResolveUseDedicatedDecodeThread(IVideoDecoder decoder)
    {
        return decoder is not FFVideoDecoder ffVideoDecoder || ffVideoDecoder.UseDedicatedDecodeThread;
    }

    private void OnDecoderStreamInfoChanged(VideoStreamInfo streamInfo)
    {
        _frameDurationSeconds = ResolveFrameDurationSeconds(streamInfo.FrameRate);
        RaiseStreamInfoChanged(streamInfo);
    }

    private bool EnsurePendingFrame(long requiredSeekEpoch, double relativeTime)
    {
        if (_hasPendingFrame)
            return true;

        var allowStarvationRecovery = IsQueueStarved();

        if (TryDequeueDecodedFrame(requiredSeekEpoch, relativeTime, allowStarvationRecovery, out var queued))
        {
            _pendingFrame = queued;
            _hasPendingFrame = true;
            return true;
        }

        // After a seek/hard-resync, we can have no current frame while the background
        // decode thread is still refilling the queue. Decode one frame inline so the
        // caller does not sit on a stale image for noticeable time.
        if (_useDedicatedDecodeThread && _hasCurrentFrame && !ShouldForceInlineCatchUpDecode(relativeTime))
            return false;

        lock (_decoderLock)
        {
            if (_videoDecoder.IsEndOfStream)
                return false;

            while (true)
            {
                if (!_videoDecoder.TryDecodeNextFrame(out _pendingFrame, out _))
                {
                    DisposeFrame(_pendingFrame);
                    _pendingFrame = null;
                    _hasPendingFrame = false;
                    return false;
                }

                Interlocked.Increment(ref _decodedFrameCount);

                if (IsFrameUsableForPresentation(_pendingFrame!.PtsSeconds, relativeTime, allowStarvationRecovery))
                {
                    break;
                }

                Interlocked.Increment(ref _droppedFrameCount);
                DisposeFrame(_pendingFrame);
                _pendingFrame = null;

                if (_videoDecoder.IsEndOfStream)
                {
                    _hasPendingFrame = false;
                    return false;
                }
            }
        }

        if (requiredSeekEpoch != Volatile.Read(ref _seekEpoch))
        {
            DisposeFrame(_pendingFrame);
            _pendingFrame = null;
            _hasPendingFrame = false;
            return false;
        }

        _hasPendingFrame = true;

        PrefillQueueAfterInlineRecovery(requiredSeekEpoch);

        return true;
    }

    private bool SeekInternal(double seconds)
    {
        ResetDriftCorrectionOffsetUnsafe();

        var clampedSeconds = Math.Max(0, seconds);
        var target = TimeSpan.FromSeconds(clampedSeconds);
        VideoFrame? primedFrame;
        VideoFrame? preservedFrame = null;

        if (_options.HoldLastFrameOnEndOfStream && _hasCurrentFrame && _currentFrame != null)
            preservedFrame = _currentFrame.AddRef();

        lock (_decoderLock)
        {
            if (!_videoDecoder.TrySeek(target, out _))
                return false;

            Interlocked.Increment(ref _seekEpoch);
            SetSeekPresentationFloor(clampedSeconds);
            primedFrame = PrimeSeekFrameLocked();
        }

        ClearFrameCache();
        lock (_decodeQueue)
            ClearDecodeQueueLocked();

        if (primedFrame != null)
        {
            preservedFrame?.Dispose();
            _currentFrame = primedFrame;
            _hasCurrentFrame = true;

            var primedPts = primedFrame.PtsSeconds;
            SetPlaybackPosition(primedPts);

            Interlocked.Exchange(ref _currentFramePtsSeconds, primedPts);
            Interlocked.Exchange(ref _lastPromotedMasterTimestamp, clampedSeconds + StartOffset);
            Interlocked.Increment(ref _presentedFrameCount);

            RaiseFrameReady(primedFrame, clampedSeconds + StartOffset);
        }
        else if (preservedFrame != null)
        {
            _currentFrame = preservedFrame;
            _hasCurrentFrame = true;

            var preservedPts = preservedFrame.PtsSeconds;
            SetPlaybackPosition(preservedPts);
            Interlocked.Exchange(ref _currentFramePtsSeconds, preservedPts);
            Interlocked.Exchange(ref _lastPromotedMasterTimestamp, clampedSeconds + StartOffset);
        }
        else
        {
            SetPlaybackPosition(clampedSeconds);
        }

        _decodeWakeEvent.Set();

        return true;
    }

    private VideoFrame? PrimeSeekFrameLocked()
    {
        VideoFrame? lastFrameBeforeTarget = null;

        while (!_videoDecoder.IsEndOfStream)
        {
            if (!_videoDecoder.TryDecodeNextFrame(out var frame, out _))
            {
                DisposeFrame(frame);
                break;
            }

            Interlocked.Increment(ref _decodedFrameCount);

            if (IsFrameEligibleForPresentation(frame.PtsSeconds))
            {
                DisposeFrame(lastFrameBeforeTarget);
                return frame;
            }

            Interlocked.Increment(ref _droppedFrameCount);
            DisposeFrame(lastFrameBeforeTarget);
            lastFrameBeforeTarget = frame;
        }

        return lastFrameBeforeTarget;
    }

    private void SetSeekPresentationFloor(double positionSeconds)
    {
        Interlocked.Exchange(ref _seekPresentationFloorSeconds, Math.Max(0, positionSeconds));
    }

    private bool IsFrameEligibleForPresentation(double ptsSeconds)
    {
        var floor = Interlocked.CompareExchange(ref _seekPresentationFloorSeconds, 0, 0);
        if (double.IsNegativeInfinity(floor))
            return true;

        return ptsSeconds + GetSeekFrameToleranceSeconds() >= floor;
    }

    private double GetSeekFrameToleranceSeconds()
    {
        var frameBasedTolerance = _frameDurationSeconds * 0.25;
        return Math.Max(SeekFrameToleranceFloorSeconds, Math.Min(frameBasedTolerance, 0.010));
    }

    private void SetPlaybackPosition(double positionSeconds)
    {
        SetPosition(positionSeconds);

        if (_masterClock != null)
            SetSamplePosition((long)(positionSeconds * _masterClock.SampleRate));
        else
            SetSamplePosition(0);
    }

    private void ClearFrameCache()
    {
        DisposeFrame(_currentFrame);
        DisposeFrame(_pendingFrame);
        _hasCurrentFrame = false;
        _hasPendingFrame = false;
        _currentFrame = null;
        _pendingFrame = null;
        _lastServedMasterTimestamp = double.NegativeInfinity;
        Interlocked.Exchange(ref _currentFramePtsSeconds, double.NaN);
        Interlocked.Exchange(ref _lastPromotedMasterTimestamp, double.NaN);
    }

    private double ResolveRelativeTime(double masterTimestamp, VideoPlaybackState playbackState)
    {
        if (playbackState is VideoPlaybackState.Paused or VideoPlaybackState.EndOfStream)
            return Position;

        return masterTimestamp - StartOffset;
    }

    private void UpdateEndOfStreamState(double relativeTime)
    {
        if (!_videoDecoder.IsEndOfStream || State == VideoPlaybackState.Stopped)
            return;

        if (_hasPendingFrame)
            return;

        if (!_hasCurrentFrame || _currentFrame == null)
        {
            SetState(VideoPlaybackState.EndOfStream);
            return;
        }

        if (relativeTime >= _currentFrame.PtsSeconds + Math.Max(_frameDurationSeconds, 0.001))
        {
            if (_options.HoldLastFrameOnEndOfStream)
            {
                SetPosition(_currentFrame.PtsSeconds);
            }
            else
            {
                ClearFrameCache();
            }

            SetState(VideoPlaybackState.EndOfStream);
        }
    }

    private double ClampToDuration(double positionInSeconds)
    {
        var duration = Duration;
        if (duration > 0 && !double.IsNaN(duration) && !double.IsInfinity(duration))
            return Math.Clamp(positionInSeconds, 0, duration);

        return Math.Max(0, positionInSeconds);
    }

    private void StartDecodeThreadIfNeeded()
    {
        if (!_useDedicatedDecodeThread)
            return;

        _decodeThreadRunning = true;
        _decodeThread = new Thread(DecodeThreadProc)
        {
            IsBackground = true,
            Name = "VideoStreamSource-Decode",
            Priority = ThreadPriority.AboveNormal
        };
        _decodeThread.Start();
    }

    private void DecodeThreadProc()
    {
        while (_decodeThreadRunning)
        {

            if (QueueIsFull())
            {
                _decodeWakeEvent.WaitOne(4);
                continue;
            }

            VideoFrame frame;
            bool ok;
            var decodeEpoch = Volatile.Read(ref _seekEpoch);
            lock (_decoderLock)
            {
                if (_videoDecoder.IsEndOfStream)
                {
                    _decodeWakeEvent.WaitOne(8);
                    continue;
                }

                ok = _videoDecoder.TryDecodeNextFrame(out frame, out _);
            }

            if (!ok)
            {
                DisposeFrame(frame);
                _decodeWakeEvent.WaitOne(8);
                continue;
            }

            if (decodeEpoch != Volatile.Read(ref _seekEpoch))
            {
                DisposeFrame(frame);
                continue;
            }

            // Producer path should only enforce seek-floor validity.
            // Late-frame dropping happens on the presentation path where clock context is accurate.
            if (!IsFrameEligibleForPresentation(frame.PtsSeconds))
            {
                Interlocked.Increment(ref _decodedFrameCount);
                Interlocked.Increment(ref _droppedFrameCount);
                DisposeFrame(frame);
                continue;
            }

            var enqueued = false;
            while (_decodeThreadRunning && !enqueued)
            {
                lock (_decodeQueue)
                {
                    if (decodeEpoch != Volatile.Read(ref _seekEpoch))
                    {
                        break;
                    }

                    if (_decodeQueue.Count < _queueCapacity)
                    {
                        _decodeQueue.Enqueue(new DecodedFrameEntry(frame, decodeEpoch));
                        Interlocked.Increment(ref _decodeQueueDepth);
                        Interlocked.Increment(ref _decodedFrameCount);
                        enqueued = true;
                    }
                }

                if (!enqueued)
                    _decodeWakeEvent.WaitOne(2);
            }

            if (!enqueued)
                DisposeFrame(frame);
        }
    }

    private bool QueueIsFull()
    {
        return Interlocked.Read(ref _decodeQueueDepth) >= _queueCapacity;
    }

    private bool TryDequeueDecodedFrame(long requiredSeekEpoch, double relativeTime, bool allowStarvationRecovery, out VideoFrame frame)
    {
        lock (_decodeQueue)
        {
            while (_decodeQueue.Count > 0)
            {
                var entry = _decodeQueue.Dequeue();
                Interlocked.Decrement(ref _decodeQueueDepth);
                _decodeWakeEvent.Set();

                if (entry.SeekEpoch == requiredSeekEpoch)
                {
                    var preserveLateFrame = allowStarvationRecovery && _decodeQueue.Count == 0;
                    if (!IsFrameUsableForPresentation(entry.Frame.PtsSeconds, relativeTime, preserveLateFrame))
                    {
                        Interlocked.Increment(ref _droppedFrameCount);
                        DisposeFrame(entry.Frame);
                        continue;
                    }

                    frame = entry.Frame;
                    return true;
                }

                DisposeFrame(entry.Frame);
            }

            frame = default!;
            return false;
        }
    }

    private void PromoteReadyFrames(double masterTimestamp, double relativeTime, long requiredSeekEpoch)
    {
        while (true)
        {
            if (!_hasPendingFrame && !EnsurePendingFrame(requiredSeekEpoch, relativeTime))
                return;

            if (!_hasPendingFrame || _pendingFrame == null)
                return;

            if (IsFrameTooLateForPresentation(_pendingFrame.PtsSeconds, relativeTime, allowStarvationRecovery: IsQueueStarved()))
            {
                DropPendingFrame();
                continue;
            }

            if (!_hasCurrentFrame)
            {
                PromotePendingFrame(masterTimestamp);
                continue;
            }

            if (_pendingFrame.PtsSeconds <= relativeTime)
            {
                PromotePendingFrame(masterTimestamp);
                continue;
            }

            return;
        }
    }

    private bool IsFrameUsableForPresentation(double ptsSeconds, double relativeTime, bool allowStarvationRecovery = false)
    {
        return IsFrameEligibleForPresentation(ptsSeconds) &&
               !IsFrameTooLateForPresentation(ptsSeconds, relativeTime, allowStarvationRecovery);
    }

    private bool IsFrameTooLateForPresentation(double ptsSeconds, double relativeTime, bool allowStarvationRecovery = false)
    {
        if (relativeTime < 0)
            return false;

        var toleranceSeconds = GetLateFrameToleranceSeconds();
        if (allowStarvationRecovery)
            toleranceSeconds = Math.Max(toleranceSeconds, GetStarvationRecoveryToleranceSeconds());

        return ptsSeconds < relativeTime - toleranceSeconds;
    }

    private bool ShouldForceInlineCatchUpDecode(double relativeTime)
    {
        if (!_hasCurrentFrame || _currentFrame == null)
            return true;

        if (QueueDepth == 0 && relativeTime >= _currentFrame.PtsSeconds)
            return true;

        if (QueueDepth > ProactiveInlineDecodeLowQueueThreshold)
            return false;

        return IsFrameTooLateForPresentation(_currentFrame.PtsSeconds, relativeTime)
               || IsFrameCloseToRunningLate(_currentFrame.PtsSeconds, relativeTime);
    }

    private double GetLateFrameToleranceSeconds()
    {
        var frameBasedTolerance = _frameDurationSeconds * 0.75;
        return Math.Max(LateFrameToleranceFloorSeconds, Math.Min(frameBasedTolerance, 0.050));
    }

    private double GetStarvationRecoveryToleranceSeconds()
    {
        var frameBasedTolerance = _frameDurationSeconds * 12.0;
        return Math.Max(StarvationRecoveryToleranceFloorSeconds, Math.Min(frameBasedTolerance, 0.250));
    }

    private bool IsFrameCloseToRunningLate(double ptsSeconds, double relativeTime)
    {
        var proactiveLead = GetProactiveInlineDecodeLeadSeconds();
        return relativeTime >= ptsSeconds + proactiveLead;
    }

    private double GetProactiveInlineDecodeLeadSeconds()
    {
        var frameBasedLead = _frameDurationSeconds * 0.35;
        return Math.Max(LateFrameToleranceFloorSeconds, Math.Min(frameBasedLead, 0.012));
    }

    private bool IsQueueStarved()
    {
        return Interlocked.Read(ref _decodeQueueDepth) == 0 && !_hasPendingFrame;
    }

    private void TryRecoverFromStarvation(double relativeTime)
    {
        _ = relativeTime;

        if (!_useDedicatedDecodeThread || _videoDecoder.IsEndOfStream)
            return;

        if (Interlocked.Read(ref _decodeQueueDepth) <= InlineRecoveryPrefetchQueueTarget)
            _decodeWakeEvent.Set();
    }

    private void PrefillQueueAfterInlineRecovery(long requiredSeekEpoch)
    {
        if (!_useDedicatedDecodeThread)
            return;

        var targetQueueDepth = Math.Min(_queueCapacity, InlineRecoveryPrefetchQueueTarget);
        while (QueueDepth < targetQueueDepth)
        {
            VideoFrame? frame;
            lock (_decoderLock)
            {
                if (_videoDecoder.IsEndOfStream || requiredSeekEpoch != Volatile.Read(ref _seekEpoch))
                    break;

                if (!_videoDecoder.TryDecodeNextFrame(out frame, out _))
                {
                    DisposeFrame(frame);
                    break;
                }
            }

            Interlocked.Increment(ref _decodedFrameCount);

            if (requiredSeekEpoch != Volatile.Read(ref _seekEpoch))
            {
                DisposeFrame(frame);
                break;
            }

            // Keep prefill frames unless they violate seek-floor eligibility.
            // Presentation-time late filtering decides what gets shown/dropped.
            if (!IsFrameEligibleForPresentation(frame.PtsSeconds))
            {
                Interlocked.Increment(ref _droppedFrameCount);
                DisposeFrame(frame);
                continue;
            }

            var enqueued = false;
            lock (_decodeQueue)
            {
                if (requiredSeekEpoch == Volatile.Read(ref _seekEpoch) && _decodeQueue.Count < _queueCapacity)
                {
                    _decodeQueue.Enqueue(new DecodedFrameEntry(frame, requiredSeekEpoch));
                    Interlocked.Increment(ref _decodeQueueDepth);
                    enqueued = true;
                }
            }

            if (!enqueued)
            {
                DisposeFrame(frame);
                break;
            }
        }

        _decodeWakeEvent.Set();
    }

    private void ClearDecodeQueue()
    {
        lock (_decodeQueue)
            ClearDecodeQueueLocked();
    }

    private void ClearDecodeQueueLocked()
    {
        while (_decodeQueue.Count > 0)
        {
            DisposeFrame(_decodeQueue.Dequeue().Frame);
            Interlocked.Decrement(ref _decodeQueueDepth);
        }

        Interlocked.Exchange(ref _decodeQueueDepth, 0);
    }

    private static void DisposeFrame(VideoFrame? frame)
    {
        frame?.Dispose();
    }
}

