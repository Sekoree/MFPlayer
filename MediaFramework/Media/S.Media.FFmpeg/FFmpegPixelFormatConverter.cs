using System.Buffers;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// <see cref="IPixelFormatConverter"/> backed by FFmpeg's <c>libswscale</c>.
/// Replaces the libyuv-based fast path that <see cref="BasicPixelFormatConverter"/>
/// previously used: in measured profiles (4K60 yuv422p10le → RGBA, NV12 → BGRA,
/// I420 → RGBA, etc.) FFmpeg's SIMD swscale is consistently faster than the
/// libyuv shims, and using a single converter family removes the soft dependency
/// on a separately-shipped <c>libyuv</c> binary.
/// <para>
/// One <c>SwsContext*</c> is cached per converter instance.  When the requested
/// (srcW, srcH, srcFmt, dstW, dstH, dstFmt) tuple changes, FFmpeg's
/// <c>sws_getCachedContext</c> reuses or rebuilds the context internally — we
/// don't have to.  All <c>sws_scale</c> calls are serialised under
/// <see cref="_lock"/> because <c>SwsContext</c> is not thread-safe; the typical
/// caller (NDI's <c>VideoWriteLoop</c>) is single-threaded so the lock is
/// uncontended.
/// </para>
/// <para>
/// Falls back to <see cref="BasicPixelFormatConverter"/>'s managed scalar paths
/// for any pair libswscale doesn't recognise (e.g. when one side maps to
/// <c>AV_PIX_FMT_NONE</c>) so existing call sites keep working.
/// </para>
/// </summary>
public sealed unsafe class FFmpegPixelFormatConverter : IPixelFormatConverter
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegPixelFormatConverter));

    private readonly Lock _lock = new();
    private readonly BasicPixelFormatConverter _fallback = new();
    private readonly byte*[] _srcDataPtrs = new byte*[4];
    private readonly int[] _srcStrides = new int[4];
    private readonly byte*[] _dstDataPtrs = new byte*[4];
    private readonly int[] _dstStrides = new int[4];

    private SwsContext* _sws;
    private bool _disposed;
    private bool _ffmpegAvailable;
    private bool _ffmpegProbed;

    public VideoFrame Convert(VideoFrame source, PixelFormat dstFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.PixelFormat == dstFormat)
            return source;

        var srcAv = MapToAvFormat(source.PixelFormat);
        var dstAv = MapToAvFormat(dstFormat);

        // Either side unmapped → managed scalar fallback (handles Yuv444p, P010,
        // gray, packed 24-bit etc. in their reference scalar paths).
        if (srcAv == AVPixelFormat.AV_PIX_FMT_NONE || dstAv == AVPixelFormat.AV_PIX_FMT_NONE)
            return _fallback.Convert(source, dstFormat);

        if (!EnsureFfmpegLoaded())
            return _fallback.Convert(source, dstFormat);

        int width  = source.Width;
        int height = source.Height;
        if (width <= 0 || height <= 0)
            return _fallback.Convert(source, dstFormat);

        int srcBytes = ComputeFormatByteSize(source.PixelFormat, width, height);
        int dstBytes = ComputeFormatByteSize(dstFormat,         width, height);
        if (srcBytes <= 0 || dstBytes <= 0 || source.Data.Length < srcBytes)
            return _fallback.Convert(source, dstFormat);

        var rented = ArrayPool<byte>.Shared.Rent(dstBytes);
        var owner  = new ArrayPoolOwner<byte>(rented);

        bool ok;
        lock (_lock)
        {
            ok = TrySwsScale(source.Data.Span, source.PixelFormat, srcAv,
                             rented.AsSpan(0, dstBytes), dstFormat, dstAv,
                             width, height);
        }

        if (!ok)
        {
            // Return rental and fall back so we don't leak a half-converted buffer.
            owner.Dispose();
            return _fallback.Convert(source, dstFormat);
        }

        return new VideoFrame(width, height, dstFormat, rented.AsMemory(0, dstBytes), source.Pts, owner);
    }

    private bool TrySwsScale(
        ReadOnlySpan<byte> src, PixelFormat srcPf, AVPixelFormat srcAv,
        Span<byte> dst, PixelFormat dstPf, AVPixelFormat dstAv,
        int width, int height)
    {
        // §perf-ffmpeg-converter / SWS-flag — bilinear (=2) matches libswscale's
        // default for non-scaling format conversion and is a good balance of
        // SIMD-friendliness vs accuracy for 8/10-bit YUV → RGB.  We never resize
        // here (src and dst dimensions match) so the scaling kernel is a no-op
        // and only the colour-space + chroma upsample work runs.  FFmpeg.AutoGen
        // does not surface the SWS_* flags as constants on the static <c>ffmpeg</c>
        // wrapper, so the literal value (=2) is used directly — same as the
        // existing call site in <see cref="NDIAVEndpoint.TryConvertI210ToRgbaFfmpeg"/>.
        const int swsFlags = 2;

        _sws = ffmpeg.sws_getCachedContext(_sws,
            width, height, srcAv,
            width, height, dstAv,
            swsFlags, null, null, null);

        if (_sws == null)
        {
            Log.LogWarning("sws_getCachedContext returned null for {SrcPf}({SrcAv}) -> {DstPf}({DstAv}) at {W}x{H}",
                srcPf, srcAv, dstPf, dstAv, width, height);
            return false;
        }

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            // §heavy-media-fixes phase 7 — `FillPlanes` already zeroes the
            // byte*[] / int[] state at entry, so we don't need to clear
            // again after `sws_scale`. The previous post-scale clear was
            // pure defense-in-depth (the `byte*` becomes stale once the
            // fixed scope exits), but no caller ever reads these private
            // fields outside this method, so the writes are dead.
            FillPlanes(srcPf, pSrc, width, height, _srcDataPtrs, _srcStrides);
            FillPlanes(dstPf, pDst, width, height, _dstDataPtrs, _dstStrides);

            int converted = ffmpeg.sws_scale(_sws,
                _srcDataPtrs, _srcStrides, 0, height,
                _dstDataPtrs, _dstStrides);

            return converted == height;
        }
    }

    /// <summary>
    /// Populates <paramref name="planes"/> + <paramref name="strides"/> with the
    /// per-plane base pointers that libswscale expects for <paramref name="format"/>.
    /// Uses the same byte layout the rest of the framework already allocates with
    /// (<see cref="ComputeFormatByteSize"/>): planar formats are tightly packed,
    /// 10-bit samples are 16-bit little-endian, semi-planar UV is interleaved.
    /// Indices 1..3 are zeroed out for packed (single-plane) formats.
    /// </summary>
    private static void FillPlanes(PixelFormat format, byte* basePtr, int width, int height,
                                   byte*[] planes, int[] strides)
    {
        for (int i = 0; i < 4; i++) { planes[i] = null; strides[i] = 0; }

        switch (format)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                planes[0]  = basePtr;
                strides[0] = width * 4;
                break;

            case PixelFormat.Rgb24:
            case PixelFormat.Bgr24:
                planes[0]  = basePtr;
                strides[0] = width * 3;
                break;

            case PixelFormat.Gray8:
                planes[0]  = basePtr;
                strides[0] = width;
                break;

            case PixelFormat.Uyvy422:
                planes[0]  = basePtr;
                strides[0] = width * 2;
                break;

            case PixelFormat.Nv12:
            {
                int ySize = width * height;
                planes[0]  = basePtr;
                planes[1]  = basePtr + ySize;
                strides[0] = width;
                strides[1] = width;     // UV interleaved at chroma height
                break;
            }

            case PixelFormat.P010:
            {
                int ySize = width * 2 * height;
                planes[0]  = basePtr;
                planes[1]  = basePtr + ySize;
                strides[0] = width * 2;
                strides[1] = width * 2; // UV interleaved, 16-bit each
                break;
            }

            case PixelFormat.Yuv420p:
            {
                int ySize = width * height;
                int uvSize = ySize / 4;
                planes[0]  = basePtr;
                planes[1]  = basePtr + ySize;
                planes[2]  = basePtr + ySize + uvSize;
                strides[0] = width;
                strides[1] = width / 2;
                strides[2] = width / 2;
                break;
            }

            case PixelFormat.Yuv420p10:
            {
                int ySize = width * 2 * height;
                int uvSize = (width * height) / 2; // (w/2 * h/2) * 2 bytes
                planes[0]  = basePtr;
                planes[1]  = basePtr + ySize;
                planes[2]  = basePtr + ySize + uvSize;
                strides[0] = width * 2;
                strides[1] = width;     // (w/2) samples * 2 bytes
                strides[2] = width;
                break;
            }

            case PixelFormat.Yuv422p10:
            {
                int ySize = width * 2 * height;
                int uvSize = width * height; // (w/2) samples * 2 bytes * h
                planes[0]  = basePtr;
                planes[1]  = basePtr + ySize;
                planes[2]  = basePtr + ySize + uvSize;
                strides[0] = width * 2;
                strides[1] = width;
                strides[2] = width;
                break;
            }

            case PixelFormat.Yuv444p:
            {
                int planeSize = width * height;
                planes[0]  = basePtr;
                planes[1]  = basePtr + planeSize;
                planes[2]  = basePtr + planeSize * 2;
                strides[0] = width;
                strides[1] = width;
                strides[2] = width;
                break;
            }

            default:
                planes[0]  = basePtr;
                strides[0] = width * 4;
                break;
        }
    }

    /// <summary>
    /// Total byte count for one frame in the given <paramref name="format"/> at
    /// the given dimensions.  Assumes the framework's standard tightly-packed
    /// layout (no per-row alignment padding), matching every existing
    /// allocation + capture path in the codebase.
    /// </summary>
    public static int ComputeFormatByteSize(PixelFormat format, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        return format switch
        {
            PixelFormat.Bgra32    => width * height * 4,
            PixelFormat.Rgba32    => width * height * 4,
            PixelFormat.Rgb24     => width * height * 3,
            PixelFormat.Bgr24     => width * height * 3,
            PixelFormat.Gray8     => width * height,
            PixelFormat.Uyvy422   => width * height * 2,
            PixelFormat.Nv12      => width * height * 3 / 2,
            PixelFormat.Yuv420p   => width * height * 3 / 2,
            PixelFormat.P010      => width * height * 3,           // Y(2) + UV(2) at half-h
            PixelFormat.Yuv420p10 => width * height * 3,           // Y(2) + 2 × UV(1) at quarter-area
            PixelFormat.Yuv422p10 => width * height * 4,           // Y(2) + 2 × UV(1) at half-area
            PixelFormat.Yuv444p   => width * height * 3,
            _                     => width * height * 4
        };
    }

    private static AVPixelFormat MapToAvFormat(PixelFormat pf) => pf switch
    {
        PixelFormat.Bgra32    => AVPixelFormat.AV_PIX_FMT_BGRA,
        PixelFormat.Rgba32    => AVPixelFormat.AV_PIX_FMT_RGBA,
        PixelFormat.Rgb24     => AVPixelFormat.AV_PIX_FMT_RGB24,
        PixelFormat.Bgr24     => AVPixelFormat.AV_PIX_FMT_BGR24,
        PixelFormat.Gray8     => AVPixelFormat.AV_PIX_FMT_GRAY8,
        PixelFormat.Uyvy422   => AVPixelFormat.AV_PIX_FMT_UYVY422,
        PixelFormat.Nv12      => AVPixelFormat.AV_PIX_FMT_NV12,
        PixelFormat.Yuv420p   => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Yuv420p10 => AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
        PixelFormat.Yuv422p10 => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        PixelFormat.P010      => AVPixelFormat.AV_PIX_FMT_P010LE,
        PixelFormat.Yuv444p   => AVPixelFormat.AV_PIX_FMT_YUV444P,
        _                     => AVPixelFormat.AV_PIX_FMT_NONE
    };

    private bool EnsureFfmpegLoaded()
    {
        if (_ffmpegProbed) return _ffmpegAvailable;
        try
        {
            FFmpegLoader.EnsureLoaded();
            _ffmpegAvailable = true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "FFmpeg load failed; falling back to managed scalar conversion paths");
            _ffmpegAvailable = false;
        }
        _ffmpegProbed = true;
        return _ffmpegAvailable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
        }
        _fallback.Dispose();
    }
}
