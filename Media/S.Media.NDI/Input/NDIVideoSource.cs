using System.Diagnostics;
using System.Buffers;
using NDILib;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Media;

namespace S.Media.NDI.Input;

public sealed class NDIVideoSource : IVideoSource
{
    private const uint CaptureTimeoutMs = 16;
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly NDICaptureCoordinator? _captureCoordinator;
    private int _readInProgress;
    private bool _disposed;
    private long _framesCaptured;
    private long _framesDropped;
    private long _repeatedTimestampFramesPresented;
    private long _lastTimestamp100ns;
    private long _firstTimestamp100ns;
    private double _lastReadMs;
    private readonly NDIVideoFallbackMode _videoFallbackMode;
    private readonly NDIQueueOverflowPolicy _queueOverflowPolicy;
    private readonly int _videoJitterBufferFrames;
    private readonly Queue<BufferedVideoFrame> _videoJitterQueue = new();
    private bool _videoJitterPrimed;
    private byte[]? _lastFrameRgba;
    private int _lastFrameValidLength;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private DateTime _lastFrameCapturedUtc;
    private string _lastFrameIncomingPixelFormat = "none";
    private VideoPixelFormat _lastFrameOutputPixelFormat = VideoPixelFormat.Rgba32;
    private string _lastFrameConversionPath = "none";
    private long _fallbackFramesPresented;
    private string _incomingPixelFormat = "none";
    private string _outputPixelFormat = "none";
    private string _conversionPath = "none";

    public NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions)
        : this(mediaItem, sourceOptions, captureCoordinator: null)
    {
    }

    internal NDIVideoSource(NDIMediaItem mediaItem, NDISourceOptions sourceOptions, NDICaptureCoordinator? captureCoordinator)
    {
        ArgumentNullException.ThrowIfNull(mediaItem);
        Id = Guid.NewGuid();
        SourceOptions = sourceOptions;
        _captureCoordinator = captureCoordinator ?? (mediaItem.Receiver is null ? null : new NDICaptureCoordinator(mediaItem.Receiver));
        _videoFallbackMode = sourceOptions.VideoFallbackMode;
        _queueOverflowPolicy = sourceOptions.QueueOverflowPolicy;
        _videoJitterBufferFrames = Math.Max(1, sourceOptions.VideoJitterBufferFrames);
    }

    public Guid Id { get; }

    public NDISourceOptions SourceOptions { get; }

    public VideoSourceState State { get; private set; }

    /// <inheritdoc/>
    public VideoStreamInfo StreamInfo
    {
        get
        {
            lock (_gate)
            {
                return new VideoStreamInfo
                {
                    Width  = _lastFrameWidth  > 0 ? _lastFrameWidth  : null,
                    Height = _lastFrameHeight > 0 ? _lastFrameHeight : null,
                };
            }
        }
    }

    public double PositionSeconds { get; private set; }

    public double DurationSeconds => double.NaN;

    public long CurrentFrameIndex { get; private set; }

    public long? CurrentDecodeFrameIndex => null;

    public long? TotalFrameCount => null;

    public bool IsSeekable => false;

    public NDIVideoSourceDebugInfo Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new NDIVideoSourceDebugInfo(
                    _framesCaptured,
                    _framesDropped,
                    _repeatedTimestampFramesPresented,
                    _fallbackFramesPresented,
                    _lastReadMs,
                    _videoJitterBufferFrames,
                    _videoJitterQueue.Count,
                    _incomingPixelFormat,
                    _outputPixelFormat,
                    _conversionPath);
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

            _firstTimestamp100ns = 0;
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

            _firstTimestamp100ns = 0;
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
            return (int)MediaErrorCode.NDIVideoReadRejected;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();

            var width = 2;
            var height = 2;
            var capturedFrame = false;
            var hasReceiver = _captureCoordinator is not null;
            var repeatedTimestamp = false;
            var presentedFromBuffer = false;
            var presentedFromFallback = false;
            var incomingPixelFormat = "none";
            var outputPixelFormat = VideoPixelFormat.Rgba32;
            var conversionPath = "none";
            var frameDataLength = width * height * 4;
            var framePresentationSeconds = PositionSeconds;
            Action<VideoFrame>? releaseAction = null;
            byte[]? rgba = null;

            if (_captureCoordinator is not null && _captureCoordinator.TryReadVideo(CaptureTimeoutMs, out var capture))
            {
                width = capture.Width;
                height = capture.Height;
                incomingPixelFormat = capture.IncomingPixelFormat;
                outputPixelFormat = capture.OutputPixelFormat;
                conversionPath = capture.ConversionPath;

                EnqueueCapturedFrame(
                    capture.Rgba,
                    capture.ValidLength,
                    width,
                    height,
                    capture.Timestamp100Ns,
                    incomingPixelFormat,
                    outputPixelFormat,
                    conversionPath,
                    DateTime.UtcNow);

                if (_lastTimestamp100ns != 0 && capture.Timestamp100Ns == _lastTimestamp100ns)
                {
                    repeatedTimestamp = true;
                    lock (_gate)
                    {
                        _repeatedTimestampFramesPresented++;
                    }
                }

                _lastTimestamp100ns = capture.Timestamp100Ns;
                capturedFrame = false;
            }

            if (hasReceiver && TryDequeueBufferedFrame(out var bufferedFrame))
            {
                rgba = bufferedFrame.Rgba;
                width = bufferedFrame.Width;
                height = bufferedFrame.Height;
                incomingPixelFormat = bufferedFrame.IncomingPixelFormat;
                outputPixelFormat = bufferedFrame.OutputPixelFormat;
                conversionPath = bufferedFrame.ConversionPath;
                frameDataLength = bufferedFrame.ValidLength;
                releaseAction = bufferedFrame.IsPooled
                    ? _ => ArrayPool<byte>.Shared.Return(bufferedFrame.Rgba)
                    : null;
                capturedFrame = true;
                presentedFromBuffer = true;
                if (bufferedFrame.Timestamp100Ns > 0)
                {
                    if (_firstTimestamp100ns == 0)
                    {
                        _firstTimestamp100ns = bufferedFrame.Timestamp100Ns;
                    }

                    var relativeTimestamp = bufferedFrame.Timestamp100Ns - _firstTimestamp100ns;
                    framePresentationSeconds = Math.Max(0, relativeTimestamp / 10_000_000d);
                }
            }

            if (hasReceiver && !capturedFrame && repeatedTimestamp && _videoFallbackMode == NDIVideoFallbackMode.PresentLastFrameOnRepeatedTimestamp)
            {
                if (TryGetFallbackFrame(
                        DateTime.UtcNow,
                        out var repeatedFrame,
                        out var repeatedWidth,
                        out var repeatedHeight,
                        out var repeatedValidLength,
                        out var repeatedIncomingFormat,
                        out var repeatedOutputFormat,
                        out var repeatedConversionPath))
                {
                    rgba = repeatedFrame;
                    width = repeatedWidth;
                    height = repeatedHeight;
                    frameDataLength = repeatedValidLength;
                    incomingPixelFormat = repeatedIncomingFormat;
                    outputPixelFormat = repeatedOutputFormat;
                    conversionPath = repeatedConversionPath;
                    capturedFrame = true;
                    presentedFromFallback = true;
                }
            }

            if (hasReceiver && !capturedFrame)
            {
                if (_videoFallbackMode == NDIVideoFallbackMode.PresentLastFrameUntilTimeout
                    && TryGetFallbackFrame(
                        DateTime.UtcNow,
                        out var fallbackFrame,
                        out var fallbackWidth,
                        out var fallbackHeight,
                        out var fallbackValidLength,
                        out var fallbackIncomingFormat,
                        out var fallbackOutputFormat,
                        out var fallbackConversionPath))
                {
                    rgba = fallbackFrame;
                    width = fallbackWidth;
                    height = fallbackHeight;
                    frameDataLength = fallbackValidLength;
                    incomingPixelFormat = fallbackIncomingFormat;
                    outputPixelFormat = fallbackOutputFormat;
                    conversionPath = fallbackConversionPath;
                    capturedFrame = true;
                    presentedFromFallback = true;
                }
            }

            if (hasReceiver && !capturedFrame)
            {
                lock (_gate)
                {
                    _framesDropped++;
                    _lastReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                return (int)MediaErrorCode.NDIVideoFallbackUnavailable;
            }

            rgba ??= new byte[width * height * 4];
            if (!capturedFrame)
            {
                // No active receiver path keeps deterministic synthetic output for tests.
                Array.Clear(rgba, 0, rgba.Length);
                frameDataLength = width * height * 4;
                incomingPixelFormat = "synthetic";
                outputPixelFormat = VideoPixelFormat.Rgba32;
                conversionPath = "synthetic";
            }
            else if (hasReceiver && presentedFromBuffer)
            {
                CacheFallbackFrame(rgba, frameDataLength, width, height, incomingPixelFormat, outputPixelFormat, conversionPath, DateTime.UtcNow);
            }

            frame = new VideoFrame(
                width: width,
                height: height,
                pixelFormat: outputPixelFormat,
                pixelFormatData: outputPixelFormat == VideoPixelFormat.Bgra32
                    ? new Bgra32PixelFormatData()
                    : new Rgba32PixelFormatData(),
                presentationTime: TimeSpan.FromSeconds(Math.Max(0, framePresentationSeconds)),
                isKeyFrame: true,
                plane0: new ReadOnlyMemory<byte>(rgba, 0, frameDataLength),
                plane0Stride: width * 4,
                releaseAction: releaseAction);

            lock (_gate)
            {
                if (presentedFromFallback)
                {
                    _fallbackFramesPresented++;
                }

                _framesCaptured++;
                CurrentFrameIndex++;
                if (capturedFrame)
                {
                    PositionSeconds = Math.Max(PositionSeconds, framePresentationSeconds);
                }
                else
                {
                    PositionSeconds = CurrentFrameIndex / 60.0;
                }
                _lastReadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _incomingPixelFormat = incomingPixelFormat;
                _outputPixelFormat = outputPixelFormat.ToString();
                _conversionPath = conversionPath;
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
            while (_videoJitterQueue.Count > 0)
            {
                var dropped = _videoJitterQueue.Dequeue();
                if (dropped.IsPooled)
                {
                    ArrayPool<byte>.Shared.Return(dropped.Rgba);
                }
            }
        }
    }

    private void EnqueueCapturedFrame(byte[] rgba, int validLength, int width, int height, long timestamp100Ns, string incomingPixelFormat, VideoPixelFormat outputPixelFormat, string conversionPath, DateTime capturedUtc)
    {
        lock (_gate)
        {
            var maxQueueDepth = Math.Max(_videoJitterBufferFrames * 3, _videoJitterBufferFrames + 2);
            if (_videoJitterQueue.Count >= maxQueueDepth)
            {
                if (_queueOverflowPolicy == NDIQueueOverflowPolicy.RejectIncoming)
                {
                    ArrayPool<byte>.Shared.Return(rgba);
                    _framesDropped++;
                    return;
                }

                if (_queueOverflowPolicy == NDIQueueOverflowPolicy.DropNewest)
                {
                    ArrayPool<byte>.Shared.Return(rgba);
                    _framesDropped++;
                    return;
                }

                var dropped = _videoJitterQueue.Dequeue();
                if (dropped.IsPooled)
                {
                    ArrayPool<byte>.Shared.Return(dropped.Rgba);
                }

                _framesDropped++;
            }

            _videoJitterQueue.Enqueue(new BufferedVideoFrame(rgba, validLength, true, width, height, timestamp100Ns, capturedUtc, incomingPixelFormat, outputPixelFormat, conversionPath));
        }
    }

    private bool TryDequeueBufferedFrame(out BufferedVideoFrame frame)
    {
        lock (_gate)
        {
            if (_videoJitterQueue.Count == 0)
            {
                frame = default;
                return false;
            }

            if (!_videoJitterPrimed)
            {
                if (_videoJitterQueue.Count < _videoJitterBufferFrames)
                {
                    frame = default;
                    return false;
                }

                _videoJitterPrimed = true;
            }

            frame = _videoJitterQueue.Dequeue();
            return true;
        }
    }

    private static string ResolveConversionPath(NdiFourCCVideoType sourceFormat)
    {
        return sourceFormat switch
        {
            NdiFourCCVideoType.Rgba => "passthrough-rgba",
            NdiFourCCVideoType.Rgbx => "passthrough-rgbx",
            NdiFourCCVideoType.Bgra => "passthrough-bgra",
            NdiFourCCVideoType.Bgrx => "passthrough-bgrx",
            _ => "unsupported-source-format",
        };
    }

    private static unsafe bool CopyPacked32(
        nint sourcePtr,
        int sourceStride,
        NdiFourCCVideoType sourceFormat,
        int width,
        int height,
        byte[] destination,
        int destinationLength,
        out VideoPixelFormat outputFormat,
        out string conversionPath)
    {
        switch (sourceFormat)
        {
            case NdiFourCCVideoType.Rgba:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgba";
                break;
            case NdiFourCCVideoType.Rgbx:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgbx";
                break;
            case NdiFourCCVideoType.Bgra:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgra";
                break;
            case NdiFourCCVideoType.Bgrx:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgrx";
                break;
            default:
                outputFormat = VideoPixelFormat.Unknown;
                conversionPath = "unsupported-source-format";
                return false;
        }

        var destinationStride = width * 4;
        var pixelsPerRow = Math.Min(width, Math.Max(0, sourceStride / 4));
        var copyBytesPerRow = pixelsPerRow * 4;
        if (destinationLength < destinationStride * height)
        {
            outputFormat = VideoPixelFormat.Unknown;
            conversionPath = "destination-too-small";
            return false;
        }

        if (copyBytesPerRow == destinationStride)
        {
            fixed (byte* destinationBase = destination)
            {
                Buffer.MemoryCopy((void*)sourcePtr, destinationBase, destinationLength, destinationStride * height);
            }

            return true;
        }

        fixed (byte* destinationBase = destination)
        {
            for (var y = 0; y < height; y++)
            {
                var sourceRow = (byte*)sourcePtr + (y * sourceStride);
                var destinationRow = destinationBase + (y * destinationStride);
                if (copyBytesPerRow < destinationStride)
                {
                    new Span<byte>(destinationRow, destinationStride).Clear();
                }

                if (copyBytesPerRow > 0)
                {
                    Buffer.MemoryCopy(sourceRow, destinationRow, destinationStride, copyBytesPerRow);
                }
            }
        }

        return true;
    }


    private void CacheFallbackFrame(
        byte[] rgba,
        int validLength,
        int width,
        int height,
        string incomingPixelFormat,
        VideoPixelFormat outputPixelFormat,
        string conversionPath,
        DateTime capturedUtc)
    {
        lock (_gate)
        {
            if (_lastFrameRgba is null || _lastFrameRgba.Length < validLength)
            {
                _lastFrameRgba = new byte[validLength];
            }

            rgba.AsSpan(0, validLength).CopyTo(_lastFrameRgba);
            _lastFrameValidLength = validLength;
            _lastFrameWidth = width;
            _lastFrameHeight = height;
            _lastFrameCapturedUtc = capturedUtc;
            _lastFrameIncomingPixelFormat = incomingPixelFormat;
            _lastFrameOutputPixelFormat = outputPixelFormat;
            _lastFrameConversionPath = conversionPath;
        }
    }

    private bool TryGetFallbackFrame(
        DateTime nowUtc,
        out byte[] rgba,
        out int width,
        out int height,
        out int validLength,
        out string incomingPixelFormat,
        out VideoPixelFormat outputPixelFormat,
        out string conversionPath)
    {
        lock (_gate)
        {
            if (_lastFrameRgba is null || _lastFrameWidth <= 0 || _lastFrameHeight <= 0 || _lastFrameValidLength <= 0)
            {
                rgba = Array.Empty<byte>();
                width = 0;
                height = 0;
                validLength = 0;
                incomingPixelFormat = "none";
                outputPixelFormat = VideoPixelFormat.Rgba32;
                conversionPath = "none";
                return false;
            }

            if (_videoFallbackMode == NDIVideoFallbackMode.PresentLastFrameUntilTimeout && nowUtc - _lastFrameCapturedUtc > FallbackTimeout)
            {
                rgba = Array.Empty<byte>();
                width = 0;
                height = 0;
                validLength = 0;
                incomingPixelFormat = "none";
                outputPixelFormat = VideoPixelFormat.Rgba32;
                conversionPath = "none";
                return false;
            }

            rgba = _lastFrameRgba;
            width = _lastFrameWidth;
            height = _lastFrameHeight;
            validLength = _lastFrameValidLength;
            incomingPixelFormat = _lastFrameIncomingPixelFormat;
            outputPixelFormat = _lastFrameOutputPixelFormat;
            conversionPath = _lastFrameConversionPath;
            return true;
        }
    }

    private readonly record struct BufferedVideoFrame(
        byte[] Rgba,
        int ValidLength,
        bool IsPooled,
        int Width,
        int Height,
        long Timestamp100Ns,
        DateTime CapturedAtUtc,
        string IncomingPixelFormat,
        VideoPixelFormat OutputPixelFormat,
        string ConversionPath);
}
