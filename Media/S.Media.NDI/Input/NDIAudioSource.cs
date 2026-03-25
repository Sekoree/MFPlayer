using System.Threading;
using System.Diagnostics;
using NdiLib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Media;

namespace S.Media.NDI.Input;

public sealed class NDIAudioSource : IAudioSource
{
    private readonly Lock _gate = new();
    private readonly int _sampleRate;
    private readonly int _channelCount;
    private readonly NdiReceiver? _receiver;
    private int _readInProgress;
    private bool _disposed;
    private long _framesCaptured;
    private long _framesDropped;
    private double _lastReadMs;

    public NDIAudioSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);
        SourceId = Guid.NewGuid();
        SourceOptions = sourceOptions;
        _receiver = mediaItem.Receiver;

        var stream = mediaItem.AudioStreams.FirstOrDefault();
        _sampleRate = stream.SampleRate ?? 48_000;
        _channelCount = Math.Max(1, stream.ChannelCount ?? 2);
    }

    public Guid SourceId { get; }

    public NDISourceOptions SourceOptions { get; }

    public AudioSourceState State { get; private set; }

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

            return (int)MediaErrorCode.NDIAudioReadRejected;
        }

        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0)
        {
            lock (_gate)
            {
                _framesDropped++;
            }

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
            var effectiveRequest = requestedFrameCount;
            if (_receiver is not null)
            {
                try
                {
                    using var capture = _receiver.CaptureScoped(timeoutMs: 0);
                    if (capture.FrameType == NdiFrameType.Audio && capture.Audio.NoSamples > 0)
                    {
                        effectiveRequest = Math.Min(effectiveRequest, capture.Audio.NoSamples);
                    }
                }
                catch
                {
                    // Receiver capture is best-effort in this contract-first phase.
                }
            }

            framesRead = Math.Max(0, Math.Min(effectiveRequest, framesWritable));
            destination[..(framesRead * _channelCount)].Clear();

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
}

