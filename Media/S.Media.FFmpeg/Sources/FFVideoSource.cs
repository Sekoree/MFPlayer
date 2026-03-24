using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.FFmpeg.Decoders.Internal;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;

namespace S.Media.FFmpeg.Sources;

public sealed class FFVideoSource : IVideoSource
{
    private readonly Lock _gate = new();
    private readonly FFSharedDemuxSession? _sharedDemuxSession;
    private bool _disposed;
    private double _positionSeconds;
    private double? _observedNativeFrameRate;
    private long _currentFrameIndex;

    public FFVideoSource(double durationSeconds = double.NaN, bool isSeekable = true, long? totalFrameCount = null)
        : this(new VideoStreamInfo { Duration = CreateDuration(durationSeconds) }, durationSeconds, isSeekable, totalFrameCount)
    {
    }

    public FFVideoSource(FFMediaItem mediaItem)
        : this(
            mediaItem.VideoStreams.FirstOrDefault(),
            durationSeconds: double.NaN,
            isSeekable: true,
            totalFrameCount: null,
            sharedDemuxSession: mediaItem.SharedDemuxSession)
    {
    }

    public FFVideoSource(VideoStreamInfo streamInfo, double durationSeconds = double.NaN, bool isSeekable = true, long? totalFrameCount = null)
        : this(streamInfo, durationSeconds, isSeekable, totalFrameCount, sharedDemuxSession: null)
    {
    }

    internal FFVideoSource(
        VideoStreamInfo streamInfo,
        double durationSeconds,
        bool isSeekable,
        long? totalFrameCount,
        FFSharedDemuxSession? sharedDemuxSession)
    {
        StreamInfo = streamInfo;
        DurationSeconds = durationSeconds;
        IsSeekable = isSeekable;
        TotalFrameCount = totalFrameCount;
        _sharedDemuxSession = sharedDemuxSession;
        SourceId = Guid.NewGuid();
    }

    public Guid SourceId { get; }

    public VideoSourceState State { get; private set; } = VideoSourceState.Stopped;

    public VideoStreamInfo StreamInfo { get; }

    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                return _positionSeconds;
            }
        }
    }

    public double DurationSeconds { get; }

    public long CurrentFrameIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentFrameIndex;
            }
        }
    }

    public long? CurrentDecodeFrameIndex => CurrentFrameIndex;

    public long? TotalFrameCount { get; }

    public bool IsSeekable { get; }

    public int Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
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
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            State = VideoSourceState.Stopped;
            return MediaResult.Success;
        }
    }

    public int ReadFrame(out VideoFrame frame)
    {
        if (_sharedDemuxSession is not null)
        {
            var code = _sharedDemuxSession.ReadVideoFrame(out var sessionFrame);
            if (code != MediaResult.Success)
            {
                frame = null!;
                return code;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    frame = null!;
                    return (int)MediaErrorCode.MediaInvalidArgument;
                }

                var mappedFormat = sessionFrame.PixelFormat == VideoPixelFormat.Unknown
                    ? FFNativeFormatMapper.MapPixelFormat(sessionFrame.NativePixelFormat)
                    : sessionFrame.PixelFormat;
                if (mappedFormat == VideoPixelFormat.Unknown)
                {
                    mappedFormat = VideoPixelFormat.Rgba32;
                }

                var nativeFrameRate = sessionFrame.TryGetNativeFrameRate();
                if (nativeFrameRate.HasValue && double.IsFinite(nativeFrameRate.Value) && nativeFrameRate.Value > 0)
                {
                    _observedNativeFrameRate = nativeFrameRate.Value;
                }

                _ = sessionFrame.HasNativeTimingMetadata;
                _ = sessionFrame.HasNativePixelMetadata;

                var pixelFormatData = CreatePixelFormatData(mappedFormat);
                var plane0Stride = ComputePlane0Stride(mappedFormat, Math.Max(1, sessionFrame.Width));

                frame = new VideoFrame(
                    width: Math.Max(1, sessionFrame.Width),
                    height: Math.Max(1, sessionFrame.Height),
                    pixelFormat: mappedFormat,
                    pixelFormatData: pixelFormatData,
                    presentationTime: sessionFrame.PresentationTime,
                    isKeyFrame: sessionFrame.IsKeyFrame,
                    plane0: new byte[Math.Max(1, plane0Stride * Math.Max(1, sessionFrame.Height))],
                    plane0Stride: plane0Stride);

                _positionSeconds = sessionFrame.PresentationTime.TotalSeconds;
                _currentFrameIndex = sessionFrame.FrameIndex + 1;
                return MediaResult.Success;
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                frame = null!;
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            frame = new VideoFrame(
                width: 2,
                height: 2,
                pixelFormat: VideoPixelFormat.Rgba32,
                pixelFormatData: new Rgba32PixelFormatData(),
                presentationTime: TimeSpan.FromSeconds(_positionSeconds),
                isKeyFrame: true,
                plane0: new byte[16],
                plane0Stride: 8);

            _currentFrameIndex++;
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (!IsSeekable)
        {
            return (int)MediaErrorCode.MediaSourceNonSeekable;
        }

        if (_sharedDemuxSession is not null)
        {
            var seekCode = _sharedDemuxSession.Seek(positionSeconds);
            if (seekCode != MediaResult.Success)
            {
                return seekCode;
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _positionSeconds = positionSeconds;
            return MediaResult.Success;
        }
    }

    public int SeekToFrame(long frameIndex)
    {
        if (frameIndex < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (!IsSeekable)
        {
            return (int)MediaErrorCode.MediaSourceNonSeekable;
        }

        if (_sharedDemuxSession is not null)
        {
            var fps = StreamInfo.FrameRate.GetValueOrDefault(_observedNativeFrameRate.GetValueOrDefault(30d));
            if (!double.IsFinite(fps) || fps <= 0)
            {
                fps = 30d;
            }

            var targetSeconds = frameIndex / fps;
            var seekCode = _sharedDemuxSession.Seek(targetSeconds);
            if (seekCode != MediaResult.Success)
            {
                return seekCode;
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            _currentFrameIndex = frameIndex;
            return MediaResult.Success;
        }
    }

    public int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount)
    {
        var result = SeekToFrame(frameIndex);

        lock (_gate)
        {
            currentFrameIndex = _currentFrameIndex;
            totalFrameCount = TotalFrameCount;
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            State = VideoSourceState.Stopped;
        }
    }

    private static TimeSpan? CreateDuration(double seconds)
    {
        return double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static IPixelFormatData CreatePixelFormatData(VideoPixelFormat format)
    {
        return format switch
        {
            VideoPixelFormat.Rgba32 => new Rgba32PixelFormatData(),
            VideoPixelFormat.Bgra32 => new Bgra32PixelFormatData(),
            VideoPixelFormat.Yuv420P => new Yuv420PPixelFormatData(),
            VideoPixelFormat.Nv12 => new Nv12PixelFormatData(),
            VideoPixelFormat.Yuv422P => new Yuv422PPixelFormatData(),
            VideoPixelFormat.Yuv422P10Le => new Yuv422P10LePixelFormatData(),
            VideoPixelFormat.P010Le => new P010LePixelFormatData(),
            VideoPixelFormat.Yuv420P10Le => new Yuv420P10LePixelFormatData(),
            VideoPixelFormat.Yuv444P => new Yuv444PPixelFormatData(),
            VideoPixelFormat.Yuv444P10Le => new Yuv444P10LePixelFormatData(),
            _ => new Rgba32PixelFormatData(),
        };
    }

    private static int ComputePlane0Stride(VideoPixelFormat format, int width)
    {
        var safeWidth = Math.Max(1, width);

        return format switch
        {
            VideoPixelFormat.Rgba32 => safeWidth * 4,
            VideoPixelFormat.Bgra32 => safeWidth * 4,
            VideoPixelFormat.Yuv420P => safeWidth,
            VideoPixelFormat.Nv12 => safeWidth,
            VideoPixelFormat.Yuv422P => safeWidth,
            VideoPixelFormat.Yuv422P10Le => safeWidth * 2,
            VideoPixelFormat.P010Le => safeWidth * 2,
            VideoPixelFormat.Yuv420P10Le => safeWidth * 2,
            VideoPixelFormat.Yuv444P => safeWidth,
            VideoPixelFormat.Yuv444P10Le => safeWidth * 2,
            _ => safeWidth * 4,
        };
    }
}

