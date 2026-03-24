using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Runtime;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFPixelConverter : IDisposable
{
    private bool _disposed;
    private bool _initialized;
    private bool _nativeConvertEnabled = true;
    private FFNativePixelConverterBackend? _nativeBackend;

    internal bool IsNativeConvertEnabled => _nativeConvertEnabled;

    public int Initialize()
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegPixelConversionFailed;
        }

        _nativeConvertEnabled = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
        _initialized = true;
        return MediaResult.Success;
    }

    public int Convert() => _disposed || !_initialized ? (int)MediaErrorCode.FFmpegPixelConversionFailed : MediaResult.Success;

    public int Convert(FFVideoDecodeResult decoded, out FFVideoConvertResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegPixelConversionFailed;
        }

        if (_nativeConvertEnabled && TryNativeConvert(decoded, out var nativeResult))
        {
            result = nativeResult;
            return MediaResult.Success;
        }

        // Placeholder phase keeps geometry/timing unchanged while preserving deterministic metadata.
        result = new FFVideoConvertResult(
            decoded.Generation,
            decoded.FrameIndex,
            decoded.PresentationTime,
            decoded.IsKeyFrame,
            decoded.Width,
            decoded.Height,
            decoded.NativeTimeBaseNumerator,
            decoded.NativeTimeBaseDenominator,
            decoded.NativeFrameRateNumerator,
            decoded.NativeFrameRateDenominator,
            decoded.NativePixelFormat,
            FFNativeFormatMapper.MapPixelFormat(decoded.NativePixelFormat));
        return MediaResult.Success;
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
    }

    private bool TryNativeConvert(FFVideoDecodeResult decoded, out FFVideoConvertResult result)
    {
        result = default;

        if (decoded.NativePixelFormat is null)
        {
            return false;
        }

        try
        {
            _nativeBackend ??= new FFNativePixelConverterBackend();
            if (!_nativeBackend.TryEnsureInitialized(
                    decoded.Width,
                    decoded.Height,
                    decoded.NativePixelFormat.Value,
                    (int)AVPixelFormat.AV_PIX_FMT_RGBA))
            {
                _nativeConvertEnabled = false;
                return false;
            }

            if (!_nativeBackend.TryExecuteScale())
            {
                _nativeConvertEnabled = false;
                return false;
            }

            result = new FFVideoConvertResult(
                decoded.Generation,
                decoded.FrameIndex,
                decoded.PresentationTime,
                decoded.IsKeyFrame,
                decoded.Width,
                decoded.Height,
                decoded.NativeTimeBaseNumerator,
                decoded.NativeTimeBaseDenominator,
                decoded.NativeFrameRateNumerator,
                decoded.NativeFrameRateDenominator,
                decoded.NativePixelFormat,
                VideoPixelFormat.Rgba32);
            return true;
        }
        catch (DllNotFoundException)
        {
            _nativeConvertEnabled = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _nativeConvertEnabled = false;
            return false;
        }
        catch (TypeInitializationException)
        {
            _nativeConvertEnabled = false;
            return false;
        }
        catch (NotSupportedException)
        {
            _nativeConvertEnabled = false;
            return false;
        }
    }
}

internal unsafe sealed class FFNativePixelConverterBackend : IDisposable
{
    private SwsContext* _context;
    private int _width;
    private int _height;
    private int _sourcePixelFormat;
    private int _targetPixelFormat;
    private bool _disposed;

    public bool TryEnsureInitialized(int width, int height, int sourcePixelFormat, int targetPixelFormat)
    {
        if (_disposed)
        {
            return false;
        }

        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        if (_context is not null &&
            _width == safeWidth &&
            _height == safeHeight &&
            _sourcePixelFormat == sourcePixelFormat &&
            _targetPixelFormat == targetPixelFormat)
        {
            return true;
        }

        DisposeContext();

        _context = ffmpeg.sws_getContext(
            safeWidth,
            safeHeight,
            (AVPixelFormat)sourcePixelFormat,
            safeWidth,
            safeHeight,
            (AVPixelFormat)targetPixelFormat,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);

        if (_context is null)
        {
            return false;
        }

        _width = safeWidth;
        _height = safeHeight;
        _sourcePixelFormat = sourcePixelFormat;
        _targetPixelFormat = targetPixelFormat;
        return true;
    }

    public bool TryExecuteScale()
    {
        if (_disposed || _context is null)
        {
            return false;
        }

        var sourceLinesize = new int[4];
        var targetLinesize = new int[4];
        sourceLinesize[0] = _width * 4;
        targetLinesize[0] = _width * 4;

        var sourceBuffer = new byte[Math.Max(1, sourceLinesize[0] * _height)];
        var targetBuffer = new byte[Math.Max(1, targetLinesize[0] * _height)];

        fixed (byte* srcPtr = sourceBuffer)
        fixed (byte* dstPtr = targetBuffer)
        {
            var sourceData = new byte*[4];
            var targetData = new byte*[4];
            sourceData[0] = srcPtr;
            sourceData[1] = null;
            sourceData[2] = null;
            sourceData[3] = null;
            targetData[0] = dstPtr;
            targetData[1] = null;
            targetData[2] = null;
            targetData[3] = null;

            var result = ffmpeg.sws_scale(_context, sourceData, sourceLinesize, 0, _height, targetData, targetLinesize);
            return result >= 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeContext();
    }

    private void DisposeContext()
    {
        if (_context is null)
        {
            return;
        }

        ffmpeg.sws_freeContext(_context);
        _context = null;
    }
}

internal readonly struct FFVideoConvertResult
{
    public FFVideoConvertResult(
        long generation,
        long frameIndex,
        TimeSpan presentationTime,
        bool isKeyFrame,
        int width,
        int height,
        int? nativeTimeBaseNumerator = null,
        int? nativeTimeBaseDenominator = null,
        int? nativeFrameRateNumerator = null,
        int? nativeFrameRateDenominator = null,
        int? nativePixelFormat = null,
        VideoPixelFormat mappedPixelFormat = VideoPixelFormat.Unknown)
    {
        Generation = generation;
        FrameIndex = frameIndex;
        PresentationTime = presentationTime;
        IsKeyFrame = isKeyFrame;
        Width = width;
        Height = height;
        NativeTimeBaseNumerator = nativeTimeBaseNumerator;
        NativeTimeBaseDenominator = nativeTimeBaseDenominator;
        NativeFrameRateNumerator = nativeFrameRateNumerator;
        NativeFrameRateDenominator = nativeFrameRateDenominator;
        NativePixelFormat = nativePixelFormat;
        MappedPixelFormat = mappedPixelFormat;
    }

    public long Generation { get; }

    public long FrameIndex { get; }

    public TimeSpan PresentationTime { get; }

    public bool IsKeyFrame { get; }

    public int Width { get; }

    public int Height { get; }

    public int? NativeTimeBaseNumerator { get; }

    public int? NativeTimeBaseDenominator { get; }

    public int? NativeFrameRateNumerator { get; }

    public int? NativeFrameRateDenominator { get; }

    public int? NativePixelFormat { get; }

    public VideoPixelFormat MappedPixelFormat { get; }

    public bool HasNativeTimingMetadata =>
        NativeTimeBaseNumerator.HasValue || NativeTimeBaseDenominator.HasValue ||
        NativeFrameRateNumerator.HasValue || NativeFrameRateDenominator.HasValue;

    public bool HasNativePixelMetadata => NativePixelFormat.HasValue;
}

