using NdiLib;
using Ownaudio;
using Ownaudio.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;
using OwnaudioNET.Synchronization;

namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Audio source adapter that pulls from NDI frame-sync and exposes it to OwnAudio mixers.
/// Capture runs on a background thread and feeds a ring buffer so mixer callbacks do not block on network timing.
/// </summary>
public sealed class NDIAudioStreamSource : BaseAudioSource, IMasterClockSource
{
    private const int FloatBitDepth = 32;
    private const int StereoChannels = 2;
    private const int CaptureJoinTimeoutMs = 200;

    private readonly NdiFrameSync _frameSync;
    private readonly Lock? _frameSyncLock;
    private readonly AudioConfig _config;
    private readonly AudioStreamInfo _streamInfo;
    private readonly INDIExternalTimelineClock _timelineClock;
    private readonly NDIAudioStreamSourceOptions _options;

    private readonly Lock _bufferLock = new();
    private readonly Thread _captureThread;

    private float[] _ringBuffer;
    private int _ringReadIndex;
    private int _ringWriteIndex;
    private int _ringCount;

    private float[] _captureScratch = Array.Empty<float>();
    private volatile bool _captureThreadRunning;
    private bool _disposed;

    private MasterClock? _masterClock;
    private double _positionSeconds;
    private long _underrunCount;
    private long _readRequestCount;
    private long _capturedBlockCount;

    public NDIAudioStreamSource(
        NdiFrameSync frameSync,
        AudioConfig config,
        INDIExternalTimelineClock timelineClock,
        Lock? frameSyncLock = null,
        NDIAudioStreamSourceOptions? options = null)
    {
        _frameSync = frameSync ?? throw new ArgumentNullException(nameof(frameSync));
        _frameSyncLock = frameSyncLock;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timelineClock = timelineClock ?? throw new ArgumentNullException(nameof(timelineClock));
        _options = (options ?? new NDIAudioStreamSourceOptions()).CloneNormalized();
        _streamInfo = new AudioStreamInfo(config.Channels, config.SampleRate, TimeSpan.Zero, bitDepth: FloatBitDepth);

        var ringCapacity = Math.Max(1, config.BufferSize * config.Channels * _options.RingCapacityMultiplier);
        _ringBuffer = new float[ringCapacity];

        _captureThreadRunning = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "NDIAudioStreamSource.Capture",
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    public override AudioConfig Config => _config;

    public override AudioStreamInfo StreamInfo => _streamInfo;

    public override double Position => Volatile.Read(ref _positionSeconds);

    public override double Duration => 0;

    public override bool IsEndOfStream => false;

    public double StartOffset { get; set; }

    public bool IsAttachedToClock => _masterClock != null;

    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    public long ReadRequestCount => Interlocked.Read(ref _readRequestCount);

    public long CapturedBlockCount => Interlocked.Read(ref _capturedBlockCount);

    public double RingFillRatio
    {
        get
        {
            lock (_bufferLock)
                return _ringBuffer.Length > 0 ? _ringCount / (double)_ringBuffer.Length : 0;
        }
    }

    public override int ReadSamples(Span<float> buffer, int frameCount)
    {
        ThrowIfDisposed();

        if (frameCount <= 0)
            return 0;

        var requestedSamples = frameCount * _config.Channels;
        if (buffer.Length < requestedSamples)
            throw new ArgumentException("Buffer is too small for requested frame count.", nameof(buffer));

        FillWithSilence(buffer, requestedSamples);
        ReadFromRing(buffer[..requestedSamples]);
        Interlocked.Increment(ref _readRequestCount);
        _timelineClock.OnAudioPlaybackFrames(frameCount, _config.SampleRate);

        UpdateSamplePosition(frameCount);
        Volatile.Write(ref _positionSeconds, Position + (frameCount / (double)_config.SampleRate));

        ApplyVolume(buffer, requestedSamples);
        OnSamplesRead(buffer, requestedSamples);
        return frameCount;
    }

    public bool ReadSamplesAtTime(double masterTimestamp, Span<float> buffer, int frameCount, out ReadResult result)
    {
        ThrowIfDisposed();

        var relativeTimestamp = masterTimestamp - StartOffset;
        if (relativeTimestamp < 0)
        {
            var sampleCount = frameCount * _config.Channels;
            FillWithSilence(buffer, sampleCount);
            result = ReadResult.CreateSuccess(frameCount);
            return true;
        }

        _ = ReadSamples(buffer, frameCount);
        result = ReadResult.CreateSuccess(frameCount);
        return true;
    }

    public override bool Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (double.IsNaN(positionInSeconds) || double.IsInfinity(positionInSeconds) || positionInSeconds < 0)
            return false;

        lock (_bufferLock)
        {
            _ringReadIndex = 0;
            _ringWriteIndex = 0;
            _ringCount = 0;
        }

        Volatile.Write(ref _positionSeconds, positionInSeconds);
        SetSamplePosition(CalculateSamplePosition(positionInSeconds));
        return true;
    }

    public void AttachToClock(MasterClock clock)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(clock);

        _masterClock = clock;
        IsSynchronized = true;

        var relativeTime = clock.CurrentTimestamp - StartOffset;
        _ = Seek(relativeTime > 0 ? relativeTime : 0);
    }

    public void DetachFromClock()
    {
        ThrowIfDisposed();
        _masterClock = null;
        IsSynchronized = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _captureThreadRunning = false;
            if (_captureThread.IsAlive)
                _captureThread.Join(CaptureJoinTimeoutMs);

            if (_masterClock != null)
                DetachFromClock();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private void CaptureLoop()
    {
        var captureFrames = Math.Max(_options.MinimumCaptureFrames, _config.BufferSize / _options.CaptureFrameTargetDivisor);

        while (_captureThreadRunning)
        {
            if (RingFillRatio > _options.CaptureHighWatermarkRatio)
            {
                Thread.Sleep(_options.CaptureSleepMilliseconds);
                continue;
            }

            if (_frameSyncLock != null)
            {
                lock (_frameSyncLock)
                {
                    CaptureAudioOnce(captureFrames);
                }
            }
            else
            {
                CaptureAudioOnce(captureFrames);
            }
        }
    }

    private void CaptureAudioOnce(int captureFrames)
    {
        _frameSync.CaptureAudio(out var audio, _config.SampleRate, _config.Channels, captureFrames);
        try
        {
            if (audio.PData == nint.Zero || audio.NoSamples <= 0 || audio.NoChannels <= 0)
                return;

            var sampleCount = audio.NoSamples * _config.Channels;
            EnsureCaptureScratch(sampleCount);
            var target = _captureScratch.AsSpan(0, sampleCount);
            target.Clear();

            unsafe
            {
                var srcBase = (byte*)audio.PData;
                for (var frame = 0; frame < audio.NoSamples; frame++)
                {
                    var dstOffset = frame * _config.Channels;

                    if (_config.Channels == StereoChannels && audio.NoChannels > StereoChannels)
                    {
                        // Downmix multichannel NDI audio to stereo for local monitoring.
                        var left = 0f;
                        var right = 0f;
                        var leftCount = 0;
                        var rightCount = 0;

                        for (var srcChannel = 0; srcChannel < audio.NoChannels; srcChannel++)
                        {
                            var channelPtr = (float*)(srcBase + (srcChannel * audio.ChannelStrideInBytes));
                            var sample = channelPtr[frame];

                            if ((srcChannel & 1) == 0)
                            {
                                left += sample;
                                leftCount++;
                            }
                            else
                            {
                                right += sample;
                                rightCount++;
                            }
                        }

                        target[dstOffset + 0] = leftCount > 0 ? left / leftCount : 0;
                        target[dstOffset + 1] = rightCount > 0 ? right / rightCount : target[dstOffset + 0];
                        continue;
                    }

                    for (var channel = 0; channel < _config.Channels; channel++)
                    {
                        if (channel >= audio.NoChannels)
                            continue;

                        var channelPtr = (float*)(srcBase + (channel * audio.ChannelStrideInBytes));
                        target[dstOffset + channel] = channelPtr[frame];
                    }
                }
            }

            WriteToRing(target);
            Interlocked.Increment(ref _capturedBlockCount);

            _timelineClock.OnAudioFrame(
                audio.Timecode,
                audio.NoSamples,
                audio.SampleRate > 0 ? audio.SampleRate : _config.SampleRate);
        }
        finally
        {
            _frameSync.FreeAudio(audio);
        }
    }

    private void EnsureCaptureScratch(int requiredSamples)
    {
        if (_captureScratch.Length >= requiredSamples)
            return;

        _captureScratch = new float[requiredSamples];
    }

    private void WriteToRing(ReadOnlySpan<float> samples)
    {
        lock (_bufferLock)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                if (_ringCount == _ringBuffer.Length)
                {
                    _ringReadIndex = (_ringReadIndex + 1) % _ringBuffer.Length;
                    _ringCount--;
                }

                _ringBuffer[_ringWriteIndex] = samples[i];
                _ringWriteIndex = (_ringWriteIndex + 1) % _ringBuffer.Length;
                _ringCount++;
            }
        }
    }

    private void ReadFromRing(Span<float> destination)
    {
        lock (_bufferLock)
        {
            var readable = Math.Min(destination.Length, _ringCount);
            for (var i = 0; i < readable; i++)
            {
                destination[i] = _ringBuffer[_ringReadIndex];
                _ringReadIndex = (_ringReadIndex + 1) % _ringBuffer.Length;
            }

            _ringCount -= readable;

            if (readable < destination.Length)
                Interlocked.Increment(ref _underrunCount);
        }
    }
}

