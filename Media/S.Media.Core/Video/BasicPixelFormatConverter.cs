using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Minimal pixel converter for early pipeline bring-up.
/// Supports Bgra32 to/from Rgba32 byte-swap and falls back to black RGBA output
/// for unsupported source formats to keep playback timing stable.
/// </summary>
public sealed class BasicPixelFormatConverter : IPixelFormatConverter
{
    public readonly record struct DiagnosticsSnapshot(
        bool LibYuvAvailable,
        long LibYuvAttempts,
        long LibYuvSuccesses,
        long ManagedFallbacks);


    private bool _disposed;
    // Instance counters — each converter tracks its own conversions independently.
    private long _libYuvAttempts;
    private long _libYuvSuccesses;
    private long _managedFallbacks;

    public static bool LibYuvEnabled
    {
        get => LibYuvRuntime.Enabled;
        set => LibYuvRuntime.Enabled = value;
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        LibYuvAvailable: LibYuvRuntime.IsAvailable,
        LibYuvAttempts: Interlocked.Read(ref _libYuvAttempts),
        LibYuvSuccesses: Interlocked.Read(ref _libYuvSuccesses),
        ManagedFallbacks: Interlocked.Read(ref _managedFallbacks));

    public VideoFrame Convert(VideoFrame source, PixelFormat dstFormat)
    {
        return ConvertWithHints(source, dstFormat);
    }

    /// <summary>
    /// Converts pixel data with optional YUV colour-space hints.
    /// </summary>
    public VideoFrame ConvertWithHints(VideoFrame source, PixelFormat dstFormat,
        YuvColorRange colorRange = YuvColorRange.Auto,
        YuvColorMatrix colorMatrix = YuvColorMatrix.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.PixelFormat == dstFormat)
            return source;

        if ((source.PixelFormat == PixelFormat.Bgra32 && dstFormat == PixelFormat.Rgba32) ||
            (source.PixelFormat == PixelFormat.Rgba32 && dstFormat == PixelFormat.Bgra32))
        {
            int bytes = source.Width * source.Height * 4;
            var rented = ArrayPool<byte>.Shared.Rent(bytes);
            var owner = new ArrayPoolOwner<byte>(rented);

            bool usedLibYuv = LibYuvRuntime.TrySwapBgraRgba(source.Data, rented, source.Width, source.Height);
            Interlocked.Increment(ref _libYuvAttempts);
            if (!usedLibYuv)
            {
                Interlocked.Increment(ref _managedFallbacks);
                SwapRedBlueChannels(source.Data.Span, rented.AsSpan(0, bytes));
            }
            else
            {
                Interlocked.Increment(ref _libYuvSuccesses);
            }

            return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
        }

        if ((source.PixelFormat == PixelFormat.Nv12 || source.PixelFormat == PixelFormat.Yuv420p ||
             source.PixelFormat == PixelFormat.Uyvy422 || source.PixelFormat == PixelFormat.Yuv422p10) &&
            (dstFormat == PixelFormat.Rgba32 || dstFormat == PixelFormat.Bgra32))
        {
            int bytes = source.Width * source.Height * 4;
            var rented = ArrayPool<byte>.Shared.Rent(bytes);
            var owner = new ArrayPoolOwner<byte>(rented);

            bool dstRgba = dstFormat == PixelFormat.Rgba32;
            bool converted = source.PixelFormat switch
            {
                PixelFormat.Nv12      => LibYuvRuntime.TryConvertNv12(source.Data, rented, source.Width, source.Height, dstRgba),
                PixelFormat.Yuv420p   => LibYuvRuntime.TryConvertI420(source.Data, rented, source.Width, source.Height, dstRgba),
                PixelFormat.Uyvy422   => LibYuvRuntime.TryConvertUyvy(source.Data, rented, source.Width, source.Height, dstRgba),
                PixelFormat.Yuv422p10 => LibYuvRuntime.TryConvertI210(source.Data, rented, source.Width, source.Height, dstRgba),
                _ => false
            };

            Interlocked.Increment(ref _libYuvAttempts);

            if (converted)
            {
                Interlocked.Increment(ref _libYuvSuccesses);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            bool managedConverted = source.PixelFormat == PixelFormat.Yuv422p10
                && TryConvertI210Managed(source.Data.Span, rented.AsSpan(0, bytes), source.Width, source.Height, dstRgba,
                    colorRange, colorMatrix);

            if (managedConverted)
            {
                Interlocked.Increment(ref _managedFallbacks);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            Interlocked.Increment(ref _managedFallbacks);
            rented.AsSpan(0, bytes).Clear();
            return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
        }

        // Fallback for unsupported source formats: return black RGBA frame.
        // This keeps timing/pacing behavior deterministic until full converters are added.
        if (dstFormat == PixelFormat.Rgba32 || dstFormat == PixelFormat.Bgra32)
        {
            bool dstRgba = dstFormat == PixelFormat.Rgba32;

            // ── Rgb24 / Bgr24 → RGBA/BGRA ─────────────────────────────────────
            if (source.PixelFormat == PixelFormat.Rgb24 || source.PixelFormat == PixelFormat.Bgr24)
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                Interlocked.Increment(ref _managedFallbacks);
                bool srcRgb = source.PixelFormat == PixelFormat.Rgb24;
                ExpandPacked24To32(source.Data.Span, rented.AsSpan(0, bytes), source.Width * source.Height, srcRgb, dstRgba);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            // ── Gray8 → RGBA/BGRA ─────────────────────────────────────────────
            if (source.PixelFormat == PixelFormat.Gray8)
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                Interlocked.Increment(ref _managedFallbacks);
                ExpandGray8ToRgba(source.Data.Span, rented.AsSpan(0, bytes), source.Width * source.Height);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            // ── Yuv444p → RGBA/BGRA ───────────────────────────────────────────
            if (source.PixelFormat == PixelFormat.Yuv444p)
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                Interlocked.Increment(ref _managedFallbacks);
                ConvertYuv444pManaged(source.Data.Span, rented.AsSpan(0, bytes), source.Width, source.Height, dstRgba);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            // ── Yuv420p10 → RGBA/BGRA ─────────────────────────────────────────
            if (source.PixelFormat == PixelFormat.Yuv420p10)
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                Interlocked.Increment(ref _managedFallbacks);
                ConvertYuv420p10Managed(source.Data.Span, rented.AsSpan(0, bytes), source.Width, source.Height, dstRgba);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            // ── P010 → RGBA/BGRA ──────────────────────────────────────────────
            if (source.PixelFormat == PixelFormat.P010)
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                Interlocked.Increment(ref _managedFallbacks);
                ConvertP010Managed(source.Data.Span, rented.AsSpan(0, bytes), source.Width, source.Height, dstRgba);
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }

            // Unknown format: black frame
            {
                int bytes = source.Width * source.Height * 4;
                var rented = ArrayPool<byte>.Shared.Rent(bytes);
                var owner = new ArrayPoolOwner<byte>(rented);
                rented.AsSpan(0, bytes).Clear();
                return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
            }
        }

        throw new NotSupportedException($"BasicPixelFormatConverter does not support {source.PixelFormat} -> {dstFormat}.");
    }

    private bool TryConvertI210Managed(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba,
        YuvColorRange colorRange = YuvColorRange.Auto, YuvColorMatrix colorMatrix = YuvColorMatrix.Auto)
    {
        if (width <= 0 || height <= 0)
            return false;

        int yStride = width * 2;
        int uvStride = width;
        int ySize = yStride * height;
        int uvSize = uvStride * height;
        int srcRequired = ySize + (uvSize * 2);
        int dstRequired = width * height * 4;

        if (src.Length < srcRequired || dst.Length < dstRequired)
            return false;

        var yPlane = src[..ySize];
        var uPlane = src.Slice(ySize, uvSize);
        var vPlane = src.Slice(ySize + uvSize, uvSize);

        // Resolve color parameters
        bool limitedRange = YuvAutoPolicy.ResolveRange(colorRange) == YuvColorRange.Limited;
        bool bt709 = YuvAutoPolicy.ResolveMatrix(colorMatrix, width, height) == YuvColorMatrix.Bt709;

        // Coefficient sets
        float kr, kg_u, kg_v, kb;
        if (bt709) { kr = 1.5748f; kg_u = 0.1873f; kg_v = 0.4681f; kb = 1.8556f; }
        else       { kr = 1.4020f; kg_u = 0.3441f; kg_v = 0.7141f; kb = 1.7720f; }

        for (int y = 0; y < height; y++)
        {
            int yRow = y * yStride;
            int uvRow = y * uvStride;
            int dstRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                int yOff = yRow + (x * 2);
                int uvOff = uvRow + ((x >> 1) * 2);

                int y10 = BinaryPrimitives.ReadUInt16LittleEndian(yPlane.Slice(yOff, 2)) & 0x03FF;
                int u10 = BinaryPrimitives.ReadUInt16LittleEndian(uPlane.Slice(uvOff, 2)) & 0x03FF;
                int v10 = BinaryPrimitives.ReadUInt16LittleEndian(vPlane.Slice(uvOff, 2)) & 0x03FF;

                float yf, uf, vf;
                if (limitedRange)
                {
                    yf = Math.Clamp((y10 / 1023f - 64f / 1023f) * (1023f / 876f), 0f, 1f);
                    uf = Math.Clamp((u10 / 1023f - 512f / 1023f) * (1023f / 896f), -0.5f, 0.5f);
                    vf = Math.Clamp((v10 / 1023f - 512f / 1023f) * (1023f / 896f), -0.5f, 0.5f);
                }
                else
                {
                    yf = y10 / 1023f;
                    uf = (u10 - 512f) / 512f;
                    vf = (v10 - 512f) / 512f;
                }

                int r = ClampToByte((yf + kr * vf) * 255f);
                int g = ClampToByte((yf - kg_u * uf - kg_v * vf) * 255f);
                int b = ClampToByte((yf + kb * uf) * 255f);

                int d = dstRow + (x * 4);
                if (dstRgba)
                {
                    dst[d] = (byte)r;
                    dst[d + 1] = (byte)g;
                    dst[d + 2] = (byte)b;
                    dst[d + 3] = 255;
                }
                else
                {
                    dst[d] = (byte)b;
                    dst[d + 1] = (byte)g;
                    dst[d + 2] = (byte)r;
                    dst[d + 3] = 255;
                }
            }
        }

        return true;
    }

    private static int ClampToByte(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 255f) return 255;
        return (int)(v + 0.5f);
    }

    // ── New format helpers ────────────────────────────────────────────────

    /// <summary>Expands 24-bit packed RGB/BGR to 32-bit RGBA/BGRA by inserting A=255.</summary>
    private static void ExpandPacked24To32(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount, bool srcRgb, bool dstRgba)
    {
        for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 3, d += 4)
        {
            byte c0 = src[s], c1 = src[s + 1], c2 = src[s + 2];
            // src: RGB order when srcRgb, BGR when !srcRgb
            // dst: RGBA order when dstRgba, BGRA when !dstRgba
            // In both cases [0]=R or B, [1]=G, [2]=B or R, [3]=A
            bool swapNeeded = srcRgb != dstRgba; // e.g. RGB->BGRA needs swap, RGB->RGBA doesn't
            dst[d]     = swapNeeded ? c2 : c0;
            dst[d + 1] = c1;
            dst[d + 2] = swapNeeded ? c0 : c2;
            dst[d + 3] = 255;
        }
    }

    /// <summary>Expands 8-bit luma to 32-bit RGBA by replicating Y into R, G, B.</summary>
    private static void ExpandGray8ToRgba(ReadOnlySpan<byte> src, Span<byte> dst, int pixelCount)
    {
        for (int i = 0, d = 0; i < pixelCount; i++, d += 4)
        {
            byte y = src[i];
            dst[d] = y; dst[d + 1] = y; dst[d + 2] = y; dst[d + 3] = 255;
        }
    }

    /// <summary>Converts 8-bit planar YUV 4:4:4 to RGBA/BGRA using BT.601 full-range.</summary>
    private static void ConvertYuv444pManaged(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
    {
        int plane = width * height;
        var yp = src[..plane];
        var up = src.Slice(plane, plane);
        var vp = src.Slice(plane * 2, plane);
        for (int i = 0, d = 0; i < plane; i++, d += 4)
        {
            float yf = yp[i] / 255f;
            float uf = (up[i] - 128f) / 128f;
            float vf = (vp[i] - 128f) / 128f;
            int r = ClampToByte((yf + 1.402f * vf) * 255f);
            int g = ClampToByte((yf - 0.344f * uf - 0.714f * vf) * 255f);
            int b = ClampToByte((yf + 1.772f * uf) * 255f);
            if (dstRgba) { dst[d] = (byte)r; dst[d + 1] = (byte)g; dst[d + 2] = (byte)b; }
            else         { dst[d] = (byte)b; dst[d + 1] = (byte)g; dst[d + 2] = (byte)r; }
            dst[d + 3] = 255;
        }
    }

    /// <summary>Converts 10-bit planar YUV 4:2:0 (I010) to RGBA/BGRA using BT.601 full-range.</summary>
    private static void ConvertYuv420p10Managed(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
    {
        int yStride = width * 2;
        int uvW = Math.Max(1, (width + 1) / 2);
        int uvH = Math.Max(1, (height + 1) / 2);
        int uvStride = uvW * 2;
        int yBytes = yStride * height;
        int uvBytes = uvStride * uvH;
        var yp = src[..yBytes];
        var up = src.Slice(yBytes, uvBytes);
        var vp = src.Slice(yBytes + uvBytes, uvBytes);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int y10 = BinaryPrimitives.ReadUInt16LittleEndian(yp.Slice(y * yStride + x * 2, 2)) & 0x3FF;
            int uvX = x / 2, uvY = y / 2;
            int u10 = BinaryPrimitives.ReadUInt16LittleEndian(up.Slice(uvY * uvStride + uvX * 2, 2)) & 0x3FF;
            int v10 = BinaryPrimitives.ReadUInt16LittleEndian(vp.Slice(uvY * uvStride + uvX * 2, 2)) & 0x3FF;
            float yf = y10 / 1023f;
            float uf = (u10 - 512f) / 512f;
            float vf = (v10 - 512f) / 512f;
            int r = ClampToByte((yf + 1.402f * vf) * 255f);
            int g = ClampToByte((yf - 0.344f * uf - 0.714f * vf) * 255f);
            int b = ClampToByte((yf + 1.772f * uf) * 255f);
            int d = (y * width + x) * 4;
            if (dstRgba) { dst[d] = (byte)r; dst[d + 1] = (byte)g; dst[d + 2] = (byte)b; }
            else         { dst[d] = (byte)b; dst[d + 1] = (byte)g; dst[d + 2] = (byte)r; }
            dst[d + 3] = 255;
        }
    }

    /// <summary>Converts 10-bit semi-planar NV12 (P010) to RGBA/BGRA using BT.601 full-range.</summary>
    private static void ConvertP010Managed(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
    {
        int yBytes = width * height * 2;
        var yp  = src[..yBytes];
        var uvp = src[yBytes..];
        int uvW = Math.Max(1, width / 2);
        int uvH = Math.Max(1, height / 2);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int y10 = BinaryPrimitives.ReadUInt16LittleEndian(yp.Slice((y * width + x) * 2, 2)) & 0x3FF;
            int uvOff = (y / 2 * uvW + x / 2) * 4;
            int u10 = BinaryPrimitives.ReadUInt16LittleEndian(uvp.Slice(uvOff,     2)) & 0x3FF;
            int v10 = BinaryPrimitives.ReadUInt16LittleEndian(uvp.Slice(uvOff + 2, 2)) & 0x3FF;
            float yf = y10 / 1023f;
            float uf = (u10 - 512f) / 512f;
            float vf = (v10 - 512f) / 512f;
            int r = ClampToByte((yf + 1.402f * vf) * 255f);
            int g = ClampToByte((yf - 0.344f * uf - 0.714f * vf) * 255f);
            int b = ClampToByte((yf + 1.772f * uf) * 255f);
            int d = (y * width + x) * 4;
            if (dstRgba) { dst[d] = (byte)r; dst[d + 1] = (byte)g; dst[d + 2] = (byte)b; }
            else         { dst[d] = (byte)b; dst[d + 1] = (byte)g; dst[d + 2] = (byte)r; }
            dst[d + 3] = 255;
        }
    }

    /// <summary>
    /// Swaps R and B channels in BGRA/RGBA pixel data using SIMD where available.
    /// Each pixel is 4 bytes: [X][G][Y][A] → [Y][G][X][A].
    /// Works by reinterpreting as uint, masking out R/B, and cross-placing them.
    /// </summary>
    private static void SwapRedBlueChannels(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = Math.Min(src.Length, dst.Length);
        int pixelBytes = n & ~3; // round down to whole pixels

        // Reinterpret as uint spans (1 uint = 1 pixel).
        var srcU = MemoryMarshal.Cast<byte, uint>(src[..pixelBytes]);
        var dstU = MemoryMarshal.Cast<byte, uint>(dst[..pixelBytes]);

        int i = 0;
        if (Vector.IsHardwareAccelerated && srcU.Length >= Vector<uint>.Count)
        {
            // Masks for little-endian layout: byte[0]=R/B, byte[1]=G, byte[2]=B/R, byte[3]=A
            var maskGA = new Vector<uint>(0xFF00FF00u); // green + alpha
            var maskR  = new Vector<uint>(0x000000FFu); // byte 0 (R in RGBA, B in BGRA)
            var maskB  = new Vector<uint>(0x00FF0000u); // byte 2 (B in RGBA, R in BGRA)

            int vecEnd = srcU.Length - (srcU.Length % Vector<uint>.Count);
            for (; i < vecEnd; i += Vector<uint>.Count)
            {
                var v  = new Vector<uint>(srcU[i..]);
                var ga = v & maskGA;          // keep G and A
                var r  = (v & maskR) << 16;   // move byte 0 → byte 2
                var b  = (v & maskB) >> 16;   // move byte 2 → byte 0
                (ga | r | b).CopyTo(dstU[i..]);
            }
        }

        // Scalar tail
        for (; i < srcU.Length; i++)
        {
            uint px = srcU[i];
            uint ga = px & 0xFF00FF00u;
            uint r  = (px & 0x000000FFu) << 16;
            uint b  = (px & 0x00FF0000u) >> 16;
            dstU[i] = ga | r | b;
        }
    }

    public void Dispose() => _disposed = true;
}
