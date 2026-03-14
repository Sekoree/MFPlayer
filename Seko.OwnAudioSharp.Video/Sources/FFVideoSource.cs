using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioSharp.Video.Decoders;
using Seko.OwnAudioSharp.Video.Events;

namespace Seko.OwnAudioSharp.Video.Sources;


/// <summary>
/// Clock-driven video source that decodes frames via an <see cref="IVideoDecoder"/> and presents
/// them in sync with an OwnAudio <see cref="MasterClock"/>.
/// <para>
/// A dedicated background thread pre-fills a bounded queue of decoded <see cref="VideoFrame"/>
/// objects. On each call to <see cref="RequestNextFrame"/> (or <see cref="TryGetFrameAtTime"/>) the
/// source promotes the next due frame, fires <see cref="FrameReady"/> / <see cref="FrameReadyFast"/>
/// and returns the current frame to the caller.
/// </para>
/// </summary>
public sealed class FFVideoSource : IDisposable, ISynchronizable
{
    private readonly Lock _syncLock = new();
    private readonly Lock _decoderLock = new();
    private readonly IVideoDecoder _videoDecoder;
    private readonly bool _ownsDecoder;
    private readonly FFVideoSourceOptions _options;

    private readonly Queue<VideoFrame> _decodeQueue;
    private readonly AutoResetEvent _decodeWakeEvent = new(false);
    private Thread? _decodeThread;
    private volatile bool _decodeThreadRunning;
    private volatile bool _seekRequested;

    private MasterClock? _masterClock;
    private VideoFrame? _currentFrame;
    private bool _hasCurrentFrame;
    private VideoFrame? _pendingFrame;
    private bool _hasPendingFrame;
    private double _lastServedMasterTimestamp = double.NegativeInfinity;
    private bool _disposed;
    private long _decodedFrameCount;
    private long _presentedFrameCount;
    private long _droppedFrameCount;
    private double _currentFramePtsSeconds = double.NaN;
    private double _lastPromotedMasterTimestamp = double.NaN;
    private double _driftCorrectionOffsetSeconds;
    private double _frameDurationSeconds;

    /// <summary>Initializes a new instance for the file at <paramref name="filePath"/> with default options.</summary>
    public FFVideoSource(string filePath)
        : this(filePath, new FFVideoSourceOptions())
    {
    }

    /// <summary>Initializes a new instance for the file at <paramref name="filePath"/> with the given <see cref="FFVideoSourceOptions"/>.</summary>
    public FFVideoSource(string filePath, FFVideoSourceOptions options, int? streamIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _options = options;
        _videoDecoder = new FFVideoDecoder(filePath, ResolveDecoderOptions(options.DecoderOptions, streamIndex));
        _ownsDecoder = true;
        _frameDurationSeconds = ResolveFrameDurationSeconds(_videoDecoder.StreamInfo.FrameRate);
        _videoDecoder.StreamInfoChanged += OnDecoderStreamInfoChanged;
        _decodeQueue = new Queue<VideoFrame>(Math.Max(2, _options.QueueCapacity));
        StartDecodeThreadIfNeeded();
    }

    /// <summary>Initializes a new instance for the given decoder with default options.</summary>
    public FFVideoSource(IVideoDecoder videoDecoder, bool ownsDecoder = false)
        : this(videoDecoder, new FFVideoSourceOptions(), ownsDecoder)
    {
    }

    /// <summary>Initializes a new instance for the given decoder with the specified options.</summary>
    public FFVideoSource(IVideoDecoder videoDecoder, FFVideoSourceOptions options, bool ownsDecoder = false)
    {
        ArgumentNullException.ThrowIfNull(videoDecoder);
        _options = options;
        _videoDecoder = videoDecoder;
        _ownsDecoder = ownsDecoder;
        _frameDurationSeconds = ResolveFrameDurationSeconds(_videoDecoder.StreamInfo.FrameRate);
        _videoDecoder.StreamInfoChanged += OnDecoderStreamInfoChanged;
        _decodeQueue = new Queue<VideoFrame>(Math.Max(2, _options.QueueCapacity));
        StartDecodeThreadIfNeeded();
    }

    /// <summary>
    /// Fired on the calling thread each time a new frame is promoted.
    /// Subscribing allocates a <see cref="VideoFrameReadyEventArgs"/> per frame; prefer
    /// <see cref="FrameReadyFast"/> for zero-allocation consumers.
    /// </summary>
    public event EventHandler<VideoFrameReadyEventArgs>? FrameReady;

    /// <summary>
    /// Zero-allocation alternative to <see cref="FrameReady"/>.
    /// The <see cref="VideoFrame"/> argument is the promoted frame (same object returned by
    /// <see cref="RequestNextFrame"/>); the <see langword="double"/> is the master clock timestamp.
    /// Do not hold a reference without calling <see cref="VideoFrame.AddRef"/>.
    /// </summary>
    public event Action<VideoFrame, double>? FrameReadyFast;

    /// <summary>Raised when decoder stream metadata changes at runtime (for example, resolution changes).</summary>
    public event EventHandler<VideoStreamInfoChangedEventArgs>? StreamInfoChanged;

    /// <summary>Metadata describing the underlying video stream.</summary>
    public VideoStreamInfo StreamInfo => _videoDecoder.StreamInfo;

    /// <summary><see langword="true"/> when a hardware-accelerated decode context is active.</summary>
    public bool IsHardwareDecoding => _videoDecoder.IsHardwareDecoding;

    /// <summary><see langword="true"/> once the decoder has consumed and flushed all frames.</summary>
    public bool IsEndOfStream => _videoDecoder.IsEndOfStream;

    /// <summary>
    /// Master clock offset in seconds. The source treats <c>masterTimestamp - StartOffset</c> as
    /// the stream-relative playback position.
    /// </summary>
    public double StartOffset { get; set; }

    /// <summary><see langword="true"/> when a <see cref="MasterClock"/> is attached.</summary>
    public bool IsAttachedToClock => _masterClock != null;

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

    /// <summary>Current drift-correction offset applied to the target time (seconds).</summary>
    public double CurrentDriftCorrectionOffsetSeconds => Interlocked.CompareExchange(ref _driftCorrectionOffsetSeconds, 0, 0);

    /// <summary>Number of frames currently held in the decode pre-fetch queue.</summary>
    public int QueueDepth
    {
        get
        {
            lock (_decodeQueue)
                return _decodeQueue.Count;
        }
    }

    // ISynchronizable
    /// <inheritdoc/>
    public long SamplePosition { get; private set; }
    /// <inheritdoc/>
    public string? SyncGroupId { get; set; }
    /// <inheritdoc/>
    public bool IsSynchronized { get; set; }

    /// <summary>
    /// Attaches the source to a <see cref="MasterClock"/>. If the clock is already running ahead
    /// of <see cref="StartOffset"/> the source seeks to the current clock position.
    /// </summary>
    public void AttachToClock(MasterClock clock)
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
            }
        }
    }

    /// <summary>Detaches the source from its current <see cref="MasterClock"/>.</summary>
    public void DetachFromClock()
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            _masterClock = null;
            IsSynchronized = false;
        }
    }

    /// <summary>
    /// Attempts to return the frame that should be displayed at <paramref name="masterTimestamp"/>.
    /// Promotes a pending frame when its PTS is due, applies late-drop and drift-correction policy,
    /// and fires <see cref="FrameReady"/> / <see cref="FrameReadyFast"/> on promotion.
    /// </summary>
    /// <param name="masterTimestamp">Absolute master clock position in seconds.</param>
    /// <param name="frame">The current frame on success.</param>
    /// <returns><see langword="true"/> if a frame is available.</returns>
    public bool TryGetFrameAtTime(double masterTimestamp, out VideoFrame frame)
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            var relativeTime = masterTimestamp - StartOffset;
            if (_options.EnableDriftCorrection)
                relativeTime += _driftCorrectionOffsetSeconds;

            if (relativeTime < 0)
            {
                frame = default!;
                return false;
            }

            // Multiple views can request at effectively the same clock time.
            // Avoid consuming extra frames for duplicate pulls within one tick.
            var currentFrame = _currentFrame;
            var masterDelta = masterTimestamp - _lastServedMasterTimestamp;
            if (_hasCurrentFrame && currentFrame != null && masterDelta >= 0 && masterDelta <= _options.DuplicateRequestWindowSeconds)
            {
                frame = currentFrame;
                return true;
            }

            if (_hasCurrentFrame && currentFrame != null)
            {
                var signedDrift = currentFrame.PtsSeconds - relativeTime;
                var drift = Math.Abs(signedDrift);

                if (_options.EnableDriftCorrection)
                    ApplyDriftCorrection(signedDrift);

                if (drift > _options.HardSeekThresholdSeconds)
                {
                    if (!SeekInternal(relativeTime))
                    {
                        frame = default!;
                        return false;
                    }
                }
            }

            if (!EnsurePendingFrame())
            {
                frame = _hasCurrentFrame ? _currentFrame! : default!;
                return _hasCurrentFrame;
            }

            var maxDropsThisRequest = Math.Max(0, _options.MaxDropsPerRequest);
            var droppedThisRequest = 0;
            while (droppedThisRequest < maxDropsThisRequest && ShouldDropPendingFrame(relativeTime))
            {
                DropPendingFrame();
                droppedThisRequest++;
                if (!EnsurePendingFrame())
                    break;
            }

            if (!_hasCurrentFrame && _hasPendingFrame)
            {
                PromotePendingFrame(masterTimestamp);
            }
            else if (_hasPendingFrame && _pendingFrame != null && _pendingFrame.PtsSeconds <= relativeTime)
            {
                PromotePendingFrame(masterTimestamp);
            }

            frame = _hasCurrentFrame ? _currentFrame! : default!;
            _lastServedMasterTimestamp = masterTimestamp;
            return _hasCurrentFrame;
        }
    }

    /// <summary>
    /// Convenience wrapper over <see cref="TryGetFrameAtTime"/> using the attached
    /// <see cref="MasterClock"/>'s current timestamp.
    /// This is the primary method the render loop should call each frame.
    /// </summary>
    /// <param name="frame">The current frame on success.</param>
    /// <returns><see langword="false"/> if no clock is attached or no frame is yet available.</returns>
    public bool RequestNextFrame(out VideoFrame frame)
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
    public void ResyncTo(long samplePosition)
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
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _decodeThreadRunning = false;
        _decodeWakeEvent.Set();
        if (_decodeThread is { IsAlive: true })
            _decodeThread.Join(TimeSpan.FromSeconds(1));
        _decodeWakeEvent.Dispose();

        _videoDecoder.StreamInfoChanged -= OnDecoderStreamInfoChanged;

        ClearFrameCache();
        ClearDecodeQueue();

        if (_ownsDecoder)
        {
            lock (_decoderLock)
                _videoDecoder.Dispose();
        }
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
            SamplePosition = (long)(_currentFrame!.PtsSeconds * _masterClock.SampleRate);

        Interlocked.Exchange(ref _currentFramePtsSeconds, _currentFrame!.PtsSeconds);
        Interlocked.Exchange(ref _lastPromotedMasterTimestamp, masterTimestamp);
        Interlocked.Increment(ref _presentedFrameCount);

        var currentFrame = _currentFrame!;
        FrameReadyFast?.Invoke(currentFrame, masterTimestamp);
        FrameReady?.Invoke(this, new VideoFrameReadyEventArgs(currentFrame, masterTimestamp));
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

    private bool ShouldDropPendingFrame(double relativeTime)
    {
        if (!_hasPendingFrame || _pendingFrame == null)
            return false;

        var effectiveLateThreshold = GetEffectiveLateDropThresholdSeconds();
        if (_pendingFrame.PtsSeconds > relativeTime - effectiveLateThreshold)
            return false;

        if (!_options.UseDedicatedDecodeThread)
            return true;

        // Keep the only available pending frame when decode is starved.
        // This favors continuity over aggressive catch-up dropping.
        lock (_decodeQueue)
            return _decodeQueue.Count > 0;
    }

    private double GetEffectiveLateDropThresholdSeconds()
    {
        var frameBased = _frameDurationSeconds * Math.Max(0.0, _options.LateDropFrameMultiplier);
        return Math.Max(_options.LateDropThresholdSeconds, frameBased);
    }

    private static double ResolveFrameDurationSeconds(double frameRate)
    {
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            return 1.0 / 30.0;

        return 1.0 / frameRate;
    }

    private static FFVideoDecoderOptions ResolveDecoderOptions(FFVideoDecoderOptions baseOptions, int? streamIndex)
    {
        if (!streamIndex.HasValue)
            return baseOptions;

        return new FFVideoDecoderOptions
        {
            PreferredStreamIndex = streamIndex,
            EnableHardwareDecoding = baseOptions.EnableHardwareDecoding,
            PreferredHardwareDevice = baseOptions.PreferredHardwareDevice,
            ThreadCount = baseOptions.ThreadCount
        };
    }

    private void OnDecoderStreamInfoChanged(VideoStreamInfo streamInfo)
    {
        _frameDurationSeconds = ResolveFrameDurationSeconds(streamInfo.FrameRate);
        StreamInfoChanged?.Invoke(this, new VideoStreamInfoChangedEventArgs(streamInfo));
    }

    private bool EnsurePendingFrame()
    {
        if (_hasPendingFrame)
            return true;

        if (TryDequeueDecodedFrame(out var queued))
        {
            _pendingFrame = queued;
            _hasPendingFrame = true;
            return true;
        }

        // After a seek/hard-resync, we can have no current frame while the background
        // decode thread is still refilling the queue. Decode one frame inline so the
        // caller does not sit on a stale image for noticeable time.
        if (_options.UseDedicatedDecodeThread && _hasCurrentFrame)
            return false;

        lock (_decoderLock)
        {
            if (_videoDecoder.IsEndOfStream)
                return false;

            if (!_videoDecoder.TryDecodeNextFrame(out _pendingFrame, out _))
            {
                DisposeFrame(_pendingFrame);
                _pendingFrame = null;
                _hasPendingFrame = false;
                return false;
            }
        }

        _hasPendingFrame = true;
        Interlocked.Increment(ref _decodedFrameCount);
        return true;
    }

    private bool SeekInternal(double seconds)
    {
        var target = TimeSpan.FromSeconds(Math.Max(0, seconds));

        lock (_decoderLock)
        {
            if (!_videoDecoder.TrySeek(target, out _))
                return false;
        }

        ClearFrameCache();
        lock (_decodeQueue)
            ClearDecodeQueueLocked();
        _seekRequested = true;
        _decodeWakeEvent.Set();

        if (_masterClock != null)
            SamplePosition = (long)(seconds * _masterClock.SampleRate);
        return true;
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
        Interlocked.Exchange(ref _driftCorrectionOffsetSeconds, 0.0);
        Interlocked.Exchange(ref _currentFramePtsSeconds, double.NaN);
        Interlocked.Exchange(ref _lastPromotedMasterTimestamp, double.NaN);
    }

    private void ApplyDriftCorrection(double signedDriftSeconds)
    {
        var absDrift = Math.Abs(signedDriftSeconds);
        if (absDrift <= _options.DriftCorrectionDeadZoneSeconds)
            return;

        // signedDrift = currentVideoPts - targetPts.
        // If video lags (negative), push target forward (positive offset) to catch up.
        var correction = -signedDriftSeconds * _options.DriftCorrectionRate;
        correction = Math.Clamp(correction, -_options.MaxCorrectionStepSeconds, _options.MaxCorrectionStepSeconds);

        _driftCorrectionOffsetSeconds += correction;
        // Avoid unbounded offset growth when stream stalls.
        _driftCorrectionOffsetSeconds = Math.Clamp(_driftCorrectionOffsetSeconds, -0.100, 0.100);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFVideoSource));
    }

    private void StartDecodeThreadIfNeeded()
    {
        if (!_options.UseDedicatedDecodeThread)
            return;

        _decodeThreadRunning = true;
        _decodeThread = new Thread(DecodeThreadProc)
        {
            IsBackground = true,
            Name = "FFVideoSource-Decode"
        };
        _decodeThread.Start();
    }

    private void DecodeThreadProc()
    {
        while (_decodeThreadRunning)
        {
            if (_seekRequested)
            {
                _seekRequested = false;
                // Let next iteration refill queue after seek.
            }

            if (QueueIsFull())
            {
                _decodeWakeEvent.WaitOne(4);
                continue;
            }

            VideoFrame frame;
            bool ok;
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

            var enqueued = false;
            while (_decodeThreadRunning && !enqueued)
            {
                lock (_decodeQueue)
                {
                    if (_decodeQueue.Count < Math.Max(2, _options.QueueCapacity))
                    {
                        _decodeQueue.Enqueue(frame);
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
        lock (_decodeQueue)
            return _decodeQueue.Count >= Math.Max(2, _options.QueueCapacity);
    }

    private bool TryDequeueDecodedFrame(out VideoFrame frame)
    {
        lock (_decodeQueue)
        {
            if (_decodeQueue.Count == 0)
            {
                frame = default!;
                return false;
            }

            frame = _decodeQueue.Dequeue();
            _decodeWakeEvent.Set();
            return true;
        }
    }

    private void ClearDecodeQueue()
    {
        lock (_decodeQueue)
            ClearDecodeQueueLocked();
    }

    private void ClearDecodeQueueLocked()
    {
        while (_decodeQueue.Count > 0)
            DisposeFrame(_decodeQueue.Dequeue());
    }

    private static void DisposeFrame(VideoFrame? frame)
    {
        frame?.Dispose();
    }

    public bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds))
            return false;

        var durationSeconds = StreamInfo.Duration.TotalSeconds;
        var clamped = durationSeconds > 0
            ? Math.Clamp(positionInSeconds, 0.0, durationSeconds)
            : Math.Max(0.0, positionInSeconds);

        lock (_syncLock)
        {
            return SeekInternal(clamped);
        }
    }
}
