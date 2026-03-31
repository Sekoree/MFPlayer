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
    // N9: configurable target packed format (defaults to Rgba32).
    private VideoPixelFormat _preferredOutputPixelFormat = VideoPixelFormat.Rgba32;

    internal bool IsNativeConvertEnabled => _nativeConvertEnabled;

    /// <param name="preferredOutputPixelFormat">
    /// Preferred output format for the sws_scale fallback path. Only packed single-plane formats
    /// (<see cref="VideoPixelFormat.Rgba32"/>, <see cref="VideoPixelFormat.Bgra32"/>) are
    /// supported here; multi-plane formats are already handled by the native pass-through path
    /// and are unaffected by this setting.
    /// </param>
    public int Initialize(VideoPixelFormat? preferredOutputPixelFormat = null)
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegPixelConversionFailed;
        }

        _nativeConvertEnabled = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
        // N9: only honour the preference if it is a packed single-plane format we can scale into.
        _preferredOutputPixelFormat = preferredOutputPixelFormat.HasValue && IsPackedOutputFormat(preferredOutputPixelFormat.Value)
            ? preferredOutputPixelFormat.Value
            : VideoPixelFormat.Rgba32;
        _initialized = true;
        return MediaResult.Success;
    }

    // N3: removed no-arg Convert() overload — it was a no-op.

    public int Convert(FFVideoDecodeResult decoded, out FFVideoConvertResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegPixelConversionFailed;
        }

        var mappedFormat = FFNativeFormatMapper.ResolvePreferredPixelFormat(
            decoded.NativePixelFormat,
            decoded.Width,
            decoded.Height,
            decoded.Plane0,
            decoded.Plane0Stride,
            decoded.Plane1,
            decoded.Plane1Stride,
            decoded.Plane2,
            decoded.Plane2Stride);
        if (ShouldPreserveNativeMultiPlane(decoded, mappedFormat))
        {
            result = new FFVideoConvertResult(
                decoded.Generation,
                decoded.FrameIndex,
                decoded.PresentationTime,
                decoded.IsKeyFrame,
                decoded.Width,
                decoded.Height,
                decoded.Plane0,
                decoded.Plane0Stride,
                decoded.Plane1,
                decoded.Plane1Stride,
                decoded.Plane2,
                decoded.Plane2Stride,
                decoded.NativeTimeBaseNumerator,
                decoded.NativeTimeBaseDenominator,
                decoded.NativeFrameRateNumerator,
                decoded.NativeFrameRateDenominator,
                decoded.NativePixelFormat,
                mappedFormat);
            return MediaResult.Success;
        }

        if (_nativeConvertEnabled && TryNativeConvert(decoded, out var nativeResult))
        {
            result = nativeResult;
            return MediaResult.Success;
        }

        return (int)MediaErrorCode.FFmpegPixelConversionFailed;
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
            // N9: use the configured target format instead of hardcoded AV_PIX_FMT_RGBA.
            var targetAvFormat = FFNativeFormatMapper.MapToNativePixelFormat(_preferredOutputPixelFormat);

            _nativeBackend ??= new FFNativePixelConverterBackend();
            if (!_nativeBackend.TryEnsureInitialized(
                    decoded.Width,
                    decoded.Height,
                    decoded.NativePixelFormat.Value,
                    targetAvFormat))
            {
                _nativeConvertEnabled = false;
                return false;
            }

            if (!_nativeBackend.TryExecuteScale(
                    decoded.Plane0,
                    decoded.Plane0Stride,
                    decoded.Plane1,
                    decoded.Plane1Stride,
                    decoded.Plane2,
                    decoded.Plane2Stride,
                    out var plane0,
                    out var plane0Stride))
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
                plane0,
                plane0Stride,
                default,
                0,
                default,
                0,
                decoded.NativeTimeBaseNumerator,
                decoded.NativeTimeBaseDenominator,
                decoded.NativeFrameRateNumerator,
                decoded.NativeFrameRateDenominator,
                decoded.NativePixelFormat,
                _preferredOutputPixelFormat);
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

    private static bool ShouldPreserveNativeMultiPlane(FFVideoDecodeResult decoded, VideoPixelFormat mappedFormat)
    {
        if (!IsMultiPlaneMappedFormat(mappedFormat))
        {
            return false;
        }

        return HasRequiredPlanes(decoded, mappedFormat);
    }

    private static bool HasRequiredPlanes(FFVideoDecodeResult decoded, VideoPixelFormat mappedFormat)
    {
        if (decoded.Plane0.IsEmpty || decoded.Plane0Stride <= 0)
        {
            return false;
        }

        if (mappedFormat == VideoPixelFormat.Nv12 || mappedFormat == VideoPixelFormat.P010Le)
        {
            return !decoded.Plane1.IsEmpty && decoded.Plane1Stride > 0;
        }

        return !decoded.Plane1.IsEmpty && decoded.Plane1Stride > 0 &&
            !decoded.Plane2.IsEmpty && decoded.Plane2Stride > 0;
    }

    private static bool IsMultiPlaneMappedFormat(VideoPixelFormat format)
    {
        return format is
            VideoPixelFormat.Yuv420P or
            VideoPixelFormat.Nv12 or
            VideoPixelFormat.Yuv422P or
            VideoPixelFormat.Yuv422P10Le or
            VideoPixelFormat.P010Le or
            VideoPixelFormat.Yuv420P10Le or
            VideoPixelFormat.Yuv444P or
            VideoPixelFormat.Yuv444P10Le;
    }

    /// <summary>Returns <see langword="true"/> for packed single-plane formats safe to use as sws_scale targets.</summary>
    private static bool IsPackedOutputFormat(VideoPixelFormat format)
    {
        return format is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32;
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

    public bool TryExecuteScale(
        ReadOnlyMemory<byte> sourcePlane0,
        int sourcePlane0Stride,
        ReadOnlyMemory<byte> sourcePlane1,
        int sourcePlane1Stride,
        ReadOnlyMemory<byte> sourcePlane2,
        int sourcePlane2Stride,
        out ReadOnlyMemory<byte> plane0,
        out int plane0Stride)
    {
        plane0 = default;
        plane0Stride = 0;

        if (_disposed || _context is null)
        {
            return false;
        }

        // N9: derive the target line size from the actual pixel format via av_image_get_linesize
        // instead of assuming 4 bytes/pixel (the old RGBA-only hardcode).
        var targetLs = ffmpeg.av_image_get_linesize((AVPixelFormat)_targetPixelFormat, _width, 0);
        var targetLinesize0 = targetLs > 0 ? targetLs : _width * 4;

        var sourceLinesize = new int[4];
        var targetLinesize = new int[4];
        sourceLinesize[0] = sourcePlane0Stride > 0 ? sourcePlane0Stride : _width * 4;
        sourceLinesize[1] = sourcePlane1Stride > 0 ? sourcePlane1Stride : 0;
        sourceLinesize[2] = sourcePlane2Stride > 0 ? sourcePlane2Stride : 0;
        targetLinesize[0] = targetLinesize0;
        plane0Stride = targetLinesize0;

        var sourceBuffer0 = sourcePlane0.IsEmpty ? Array.Empty<byte>() : sourcePlane0.ToArray();
        var sourceBuffer1 = sourcePlane1.IsEmpty ? Array.Empty<byte>() : sourcePlane1.ToArray();
        var sourceBuffer2 = sourcePlane2.IsEmpty ? Array.Empty<byte>() : sourcePlane2.ToArray();
        var targetBuffer = new byte[Math.Max(1, targetLinesize0 * _height)];

        fixed (byte* srcPtr0 = sourceBuffer0)
        fixed (byte* srcPtr1 = sourceBuffer1)
        fixed (byte* srcPtr2 = sourceBuffer2)
        fixed (byte* dstPtr = targetBuffer)
        {
            var sourceData = new byte*[4];
            var targetData = new byte*[4];
            sourceData[0] = sourceBuffer0.Length > 0 ? srcPtr0 : null;
            sourceData[1] = sourceBuffer1.Length > 0 ? srcPtr1 : null;
            sourceData[2] = sourceBuffer2.Length > 0 ? srcPtr2 : null;
            sourceData[3] = null;
            targetData[0] = dstPtr;
            targetData[1] = null;
            targetData[2] = null;
            targetData[3] = null;

            var result = ffmpeg.sws_scale(_context, sourceData, sourceLinesize, 0, _height, targetData, targetLinesize);
            if (result < 0)
            {
                return false;
            }

            plane0 = targetBuffer;
            return true;
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
        ReadOnlyMemory<byte> plane0 = default,
        int plane0Stride = 0,
        ReadOnlyMemory<byte> plane1 = default,
        int plane1Stride = 0,
        ReadOnlyMemory<byte> plane2 = default,
        int plane2Stride = 0,
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
        Plane0 = plane0;
        Plane0Stride = plane0Stride;
        Plane1 = plane1;
        Plane1Stride = plane1Stride;
        Plane2 = plane2;
        Plane2Stride = plane2Stride;
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

    public ReadOnlyMemory<byte> Plane0 { get; }

    public int Plane0Stride { get; }

    public ReadOnlyMemory<byte> Plane1 { get; }

    public int Plane1Stride { get; }

    public ReadOnlyMemory<byte> Plane2 { get; }

    public int Plane2Stride { get; }

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
