using System.Buffers;
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

            if (!converted)
            {
                Interlocked.Increment(ref _managedFallbacks);
                rented.AsSpan(0, bytes).Clear();
            }
            else
            {
                Interlocked.Increment(ref _libYuvSuccesses);
            }

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

    public void Dispose() => _disposed = true;
}

