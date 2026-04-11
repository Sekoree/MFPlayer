using System.Buffers;
using System.Buffers.Binary;
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
    private static long _libYuvAttempts;
    private static long _libYuvSuccesses;
    private static long _managedFallbacks;

    public static bool LibYuvEnabled
    {
        get => LibYuvRuntime.Enabled;
        set => LibYuvRuntime.Enabled = value;
    }

    public static DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        LibYuvAvailable: LibYuvRuntime.IsAvailable,
        LibYuvAttempts: Interlocked.Read(ref _libYuvAttempts),
        LibYuvSuccesses: Interlocked.Read(ref _libYuvSuccesses),
        ManagedFallbacks: Interlocked.Read(ref _managedFallbacks));

    public VideoFrame Convert(VideoFrame source, PixelFormat dstFormat)
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
                var src = source.Data.Span;
                var dst = rented.AsSpan(0, bytes);
                int n = Math.Min(src.Length, bytes);
                for (int i = 0; i + 3 < n; i += 4)
                {
                    dst[i] = src[i + 2];
                    dst[i + 1] = src[i + 1];
                    dst[i + 2] = src[i];
                    dst[i + 3] = src[i + 3];
                }
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
                && TryConvertI210Managed(source.Data.Span, rented.AsSpan(0, bytes), source.Width, source.Height, dstRgba);

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
            int bytes = source.Width * source.Height * 4;
            var rented = ArrayPool<byte>.Shared.Rent(bytes);
            var owner = new ArrayPoolOwner<byte>(rented);
            rented.AsSpan(0, bytes).Clear();
            return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
        }

        throw new NotSupportedException($"BasicPixelFormatConverter does not support {source.PixelFormat} -> {dstFormat}.");
    }

    private static bool TryConvertI210Managed(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, bool dstRgba)
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

                float yf = y10 / 1023f;
                float uf = (u10 - 512f) / 512f;
                float vf = (v10 - 512f) / 512f;

                int r = ClampToByte((yf + (1.5748f * vf)) * 255f);
                int g = ClampToByte((yf - (0.1873f * uf) - (0.4681f * vf)) * 255f);
                int b = ClampToByte((yf + (1.8556f * uf)) * 255f);

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

    public void Dispose() => _disposed = true;
}
