using System.Threading;
using System.Diagnostics;
using NdiLib;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Media;

namespace S.Media.NDI.Input;

public sealed class NDIVideoSource : IVideoSource
{
    private readonly Lock _gate = new();
    private readonly NdiReceiver? _receiver;
    private int _readInProgress;
    private bool _disposed;
    private long _framesCaptured;
    private long _framesDropped;
    private long _repeatedTimestampFramesPresented;
    private long _lastTimestamp100ns;
    private double _lastReadMs;

    public NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);
        SourceId = Guid.NewGuid();
        SourceOptions = sourceOptions;
        _receiver = mediaItem.Receiver;
    }

    public Guid SourceId { get; }

    public NDISourceOptions SourceOptions { get; }

    public VideoSourceState State { get; private set; }

    public double PositionSeconds { get; private set; }

    public double DurationSeconds => double.NaN;

    public long CurrentFrameIndex { get; private set; }

    public long? CurrentDecodeFrameIndex => null;

    public long? TotalFrameCount => null;

    public bool IsSeekable => false;

    public NDIVideoDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new NDIVideoDiagnostics(
                    _framesCaptured,
                    _framesDropped,
                    _repeatedTimestampFramesPresented,
                    _lastReadMs,
                    VideoPushSuccesses: 0,
                    VideoPushFailures: 0,
                    AudioPushSuccesses: 0,
                    AudioPushFailures: 0,
                    LastPushMs: 0);
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

            State = VideoSourceState.Running;
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

            State = VideoSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    public int ReadFrame(out VideoFrame frame)
    {
        frame = null!;

        if (State != VideoSourceState.Running)
        {
            lock (_gate)
            {
                _framesDropped++;
            }

            return (int)MediaErrorCode.NDIVideoReadRejected;
        }

        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0)
        {
            lock (_gate)
            {
                _framesDropped++;
            }

            return (int)MediaErrorCode.NDIVideoReadRejected;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();

            var width = 2;
            var height = 2;

            if (_receiver is not null)
            {
                try
                {
                    using var capture = _receiver.CaptureScoped(timeoutMs: 0);
                    if (capture.FrameType == NdiFrameType.Video)
                    {
                        width = Math.Max(1, capture.Video.Xres);
                        height = Math.Max(1, capture.Video.Yres);

                        if (_lastTimestamp100ns != 0 && capture.Video.Timestamp == _lastTimestamp100ns)
                        {
                            lock (_gate)
                            {
                                _repeatedTimestampFramesPresented++;
                            }
                        }

                        _lastTimestamp100ns = capture.Video.Timestamp;
                    }
                }
                catch
                {
                    // Receiver capture is best-effort in this contract-first phase.
                }
            }

            var rgba = new byte[width * height * 4];
            frame = new VideoFrame(
                width: width,
                height: height,
                pixelFormat: VideoPixelFormat.Rgba32,
                pixelFormatData: new Rgba32PixelFormatData(),
                presentationTime: TimeSpan.FromSeconds(PositionSeconds),
                isKeyFrame: true,
                plane0: rgba,
                plane0Stride: width * 4);

            lock (_gate)
            {
                _framesCaptured++;
                CurrentFrameIndex++;
                PositionSeconds = CurrentFrameIndex / 60.0;
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

    public int SeekToFrame(long frameIndex)
    {
        return (int)MediaErrorCode.MediaSourceNonSeekable;
    }

    public int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount)
    {
        currentFrameIndex = CurrentFrameIndex;
        totalFrameCount = TotalFrameCount;
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
            State = VideoSourceState.Stopped;
        }
    }
}

