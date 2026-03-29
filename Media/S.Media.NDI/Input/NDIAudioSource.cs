using System.Diagnostics;
using System.Buffers;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Media;

namespace S.Media.NDI.Input;

public sealed class NDIAudioSource : IAudioSource
{
    private const uint CaptureTimeoutMs = 8;

    private readonly Lock _gate = new();
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly NDICaptureCoordinator? _captureCoordinator;
    private float[] _audioRing;
    private int _ringReadIndex;
    private int _ringWriteIndex;
    private int _ringSampleCount;
    private int _readInProgress;
    private bool _disposed;
    private long _framesCaptured;
    private long _framesDropped;
    private double _lastReadMs;

    public NDIAudioSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions)
        : this(mediaItem, sourceOptions, captureCoordinator: null)
    {
    }

    internal NDIAudioSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions, NDICaptureCoordinator? captureCoordinator)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);
        Id = Guid.NewGuid();
        SourceOptions = sourceOptions;
        _captureCoordinator = captureCoordinator ?? (mediaItem.Receiver is null ? null : new NDICaptureCoordinator(mediaItem.Receiver));

        var stream = mediaItem.AudioStreams.FirstOrDefault();
        _sampleRate = stream.SampleRate ?? 48_000;
        _channelCount = Math.Max(1, stream.ChannelCount ?? 2);
        var audioJitterBufferMs = Math.Max(1, sourceOptions.AudioJitterBufferMs);
        var targetFrames = Math.Max(64, (int)Math.Round(_sampleRate * (audioJitterBufferMs / 1000.0)));
        var capacityFrames = Math.Max(targetFrames * 4, _sampleRate * 2);
        _audioRing = new float[capacityFrames * _channelCount];
    }

    public Guid Id { get; }

    public NDISourceOptions SourceOptions { get; }

    public AudioSourceState State { get; private set; }

    /// <inheritdoc/>
    public float Volume { get; set; } = 1.0f;

    /// <inheritdoc/>
    public long? TotalSampleCount => null; // live source — no known duration

    public AudioStreamInfo StreamInfo => new()
    {
        SampleRate = _sampleRate,
        ChannelCount = _channelCount,
    };

    public double PositionSeconds { get; private set; }

    public double DurationSeconds => double.NaN;

    public NDIAudioDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new NDIAudioDiagnostics(_framesCaptured, _framesDropped, _lastReadMs);
            }
        }
    }

    public int Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.NDISourceStartFailed;
            }

            State = AudioSourceState.Running;
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            State = AudioSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead)
    {
        framesRead = 0;

        if (State != AudioSourceState.Running)
        {
            lock (_gate)
            {
                _framesDropped++;
            }

            // §5.4: source is stopped — not a concurrent-read violation.
            return (int)MediaErrorCode.MediaSourceNotRunning;
        }

        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0)
        {
            lock (_gate)
            {
                _framesDropped++;
            }

            // Genuine concurrent-read attempt.
            return (int)MediaErrorCode.NDIAudioReadRejected;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();

            if (requestedFrameCount <= 0)
            {
                lock (_gate)
                {
                    _lastReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                return MediaResult.Success;
            }

            var framesWritable = destination.Length / _channelCount;
            var framesToWrite = Math.Max(0, Math.Min(requestedFrameCount, framesWritable));
            if (_captureCoordinator is not null && _captureCoordinator.TryReadAudio(CaptureTimeoutMs, out var capture))
            {
                try
                {
                    EnqueueCapturedAudio(capture);
                }
                finally
                {
                    if (capture.IsPooled)
                    {
                        ArrayPool<float>.Shared.Return(capture.InterleavedSamples);
                    }
                }
            }

            var samplesRequested = framesToWrite * _channelCount;
            var samplesCopied = DequeueSamples(destination[..samplesRequested]);
            if (samplesCopied < samplesRequested)
            {
                destination.Slice(samplesCopied, samplesRequested - samplesCopied).Clear();
            }

            framesRead = samplesCopied / _channelCount;

            lock (_gate)
            {
                if (framesRead > 0)
                {
                    _framesCaptured += framesRead;
                    PositionSeconds += framesRead / (double)_sampleRate;
                }
                else
                {
                    _framesDropped++;
                }

                _lastReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            return MediaResult.Success;
        }
        finally
        {
            _ = Interlocked.Exchange(ref _readInProgress, 0);
        }
    }

    public int Seek(double positionSeconds)
    {
        return (int)MediaErrorCode.MediaSourceNonSeekable;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            State = AudioSourceState.Stopped;
        }
    }

    private void EnqueueCapturedAudio(in CapturedAudioBlock frame)
    {
        var sourceChannels = Math.Max(1, frame.Channels);
        var frameCount = Math.Max(0, frame.Frames);
        if (frameCount == 0 || frame.SampleCount <= 0)
        {
            return;
        }

        if (_channelCount == sourceChannels)
        {
            lock (_gate)
            {
                EnsureRingCapacityLocked(frame.SampleCount);
                WriteToRingLocked(frame.InterleavedSamples.AsSpan(0, frame.SampleCount));
            }

            return;
        }

        var convertedSampleCount = frameCount * _channelCount;
        var converted = ArrayPool<float>.Shared.Rent(convertedSampleCount);
        try
        {
            var source = frame.InterleavedSamples.AsSpan(0, frame.SampleCount);
            var destination = converted.AsSpan(0, convertedSampleCount);
            for (var sample = 0; sample < frameCount; sample++)
            {
                for (var channel = 0; channel < _channelCount; channel++)
                {
                    destination[(sample * _channelCount) + channel] = channel < sourceChannels
                        ? source[(sample * sourceChannels) + channel]
                        : 0f;
                }
            }

            lock (_gate)
            {
                EnsureRingCapacityLocked(convertedSampleCount);
                WriteToRingLocked(destination);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(converted);
        }
    }

    private int DequeueSamples(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            var toCopy = Math.Min(destination.Length, _ringSampleCount);
            if (toCopy == 0)
            {
                return 0;
            }

            var firstChunk = Math.Min(toCopy, _audioRing.Length - _ringReadIndex);
            _audioRing.AsSpan(_ringReadIndex, firstChunk).CopyTo(destination[..firstChunk]);
            var copied = firstChunk;
            _ringReadIndex = (_ringReadIndex + firstChunk) % _audioRing.Length;

            var remaining = toCopy - firstChunk;
            if (remaining > 0)
            {
                _audioRing.AsSpan(_ringReadIndex, remaining).CopyTo(destination.Slice(copied, remaining));
                _ringReadIndex = (_ringReadIndex + remaining) % _audioRing.Length;
                copied += remaining;
            }

            _ringSampleCount -= copied;
            return copied;
        }
    }

    private void EnsureRingCapacityLocked(int incomingSamples)
    {
        if (incomingSamples <= 0)
        {
            return;
        }

        if (incomingSamples <= _audioRing.Length)
        {
            return;
        }

        var newCapacity = _audioRing.Length;
        while (newCapacity < incomingSamples)
        {
            newCapacity *= 2;
        }

        var replacement = new float[newCapacity];
        if (_ringSampleCount > 0)
        {
            var firstChunk = Math.Min(_ringSampleCount, _audioRing.Length - _ringReadIndex);
            _audioRing.AsSpan(_ringReadIndex, firstChunk).CopyTo(replacement);
            var remaining = _ringSampleCount - firstChunk;
            if (remaining > 0)
            {
                _audioRing.AsSpan(0, remaining).CopyTo(replacement.AsSpan(firstChunk));
            }
        }

        _audioRing = replacement;
        _ringReadIndex = 0;
        _ringWriteIndex = _ringSampleCount;
    }

    private void WriteToRingLocked(ReadOnlySpan<float> interleaved)
    {
        var needed = interleaved.Length;
        if (needed == 0)
        {
            return;
        }

        while (_ringSampleCount + needed > _audioRing.Length)
        {
            var drop = Math.Min(_channelCount, _ringSampleCount);
            _ringReadIndex = (_ringReadIndex + drop) % _audioRing.Length;
            _ringSampleCount -= drop;
            if (drop >= _channelCount)
            {
                _framesDropped++;
            }
        }

        var firstChunk = Math.Min(needed, _audioRing.Length - _ringWriteIndex);
        interleaved[..firstChunk].CopyTo(_audioRing.AsSpan(_ringWriteIndex, firstChunk));
        _ringWriteIndex = (_ringWriteIndex + firstChunk) % _audioRing.Length;
        var remaining = needed - firstChunk;
        if (remaining > 0)
        {
            interleaved[firstChunk..].CopyTo(_audioRing.AsSpan(_ringWriteIndex, remaining));
            _ringWriteIndex = (_ringWriteIndex + remaining) % _audioRing.Length;
        }

        _ringSampleCount += needed;
    }
}
