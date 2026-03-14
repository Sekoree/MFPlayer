using OwnaudioNET.Interfaces;
using OwnaudioNET.Synchronization;
using Seko.OwnAudioSharp.Video.Decoders;

namespace Seko.OwnAudioSharp.Video.Sources;

public sealed class VideoFrameReadyEventArgs : EventArgs
{
    public VideoFrameReadyEventArgs(VideoFrame frame, double masterTimestamp)
    {
        Frame = frame;
        MasterTimestamp = masterTimestamp;
    }

    public VideoFrame Frame { get; }
    public double MasterTimestamp { get; }
}

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

    public FFVideoSource(string filePath)
        : this(filePath, new FFVideoSourceOptions())
    {
    }

    public FFVideoSource(string filePath, FFVideoSourceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _options = options;
        _videoDecoder = new FFVideoDecoder(filePath, _options.DecoderOptions);
        _ownsDecoder = true;
        _decodeQueue = new Queue<VideoFrame>(Math.Max(2, _options.QueueCapacity));
        StartDecodeThreadIfNeeded();
    }

    public FFVideoSource(IVideoDecoder videoDecoder, bool ownsDecoder = false)
        : this(videoDecoder, new FFVideoSourceOptions(), ownsDecoder)
    {
    }

    public FFVideoSource(IVideoDecoder videoDecoder, FFVideoSourceOptions options, bool ownsDecoder = false)
    {
        ArgumentNullException.ThrowIfNull(videoDecoder);
        _options = options;
        _videoDecoder = videoDecoder;
        _ownsDecoder = ownsDecoder;
        _decodeQueue = new Queue<VideoFrame>(Math.Max(2, _options.QueueCapacity));
        StartDecodeThreadIfNeeded();
    }

    public event EventHandler<VideoFrameReadyEventArgs>? FrameReady;

    public VideoStreamInfo StreamInfo => _videoDecoder.StreamInfo;
    public bool IsHardwareDecoding => _videoDecoder.IsHardwareDecoding;
    public double StartOffset { get; set; }
    public bool IsAttachedToClock => _masterClock != null;
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrameCount);
    public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);
    public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);
    public double CurrentFramePtsSeconds => Interlocked.CompareExchange(ref _currentFramePtsSeconds, 0, 0);
    public double LastPromotedMasterTimestamp => Interlocked.CompareExchange(ref _lastPromotedMasterTimestamp, 0, 0);
    public double CurrentDriftCorrectionOffsetSeconds => Interlocked.CompareExchange(ref _driftCorrectionOffsetSeconds, 0, 0);
    public int QueueDepth
    {
        get
        {
            lock (_decodeQueue)
                return _decodeQueue.Count;
        }
    }

    public long SamplePosition { get; private set; }
    public string? SyncGroupId { get; set; }
    public bool IsSynchronized { get; set; }

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

    public void DetachFromClock()
    {
        ThrowIfDisposed();

        lock (_syncLock)
        {
            _masterClock = null;
            IsSynchronized = false;
        }
    }

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
            if (_hasCurrentFrame && currentFrame != null && masterTimestamp <= _lastServedMasterTimestamp + _options.DuplicateRequestWindowSeconds)
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

            while (_hasPendingFrame && _pendingFrame != null && _pendingFrame.PtsSeconds <= relativeTime - _options.LateDropThresholdSeconds)
            {
                DropPendingFrame();
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
        FrameReady?.Invoke(this, new VideoFrameReadyEventArgs(_currentFrame!, masterTimestamp));
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

        if (_options.UseDedicatedDecodeThread)
            return false;

        // Fallback sync decode when background thread is disabled.
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
            _decodeQueue.Clear();
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

            lock (_decodeQueue)
            {
                if (_decodeQueue.Count < Math.Max(2, _options.QueueCapacity))
                {
                    _decodeQueue.Enqueue(frame);
                    Interlocked.Increment(ref _decodedFrameCount);
                }
                else
                {
                    DisposeFrame(frame);
                    Interlocked.Increment(ref _droppedFrameCount);
                }
            }
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
        {
            while (_decodeQueue.Count > 0)
                DisposeFrame(_decodeQueue.Dequeue());
        }
    }

    private static void DisposeFrame(VideoFrame? frame)
    {
        frame?.Dispose();
    }
}

