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
    private sealed class ArrayPoolByteOwner : IDisposable
    {
        private byte[]? _buffer;

        public ArrayPoolByteOwner(byte[] buffer) => _buffer = buffer;

        public void Dispose()
        {
            var buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private bool _disposed;

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
            var owner = new ArrayPoolByteOwner(rented);

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

            return new VideoFrame(source.Width, source.Height, dstFormat, rented.AsMemory(0, bytes), source.Pts, owner);
        }

        // Fallback for unsupported source formats: return black RGBA frame.
        // This keeps timing/pacing behavior deterministic until full converters are added.
        if (dstFormat == PixelFormat.Rgba32)
        {
            int bytes = source.Width * source.Height * 4;
            var rented = ArrayPool<byte>.Shared.Rent(bytes);
            var owner = new ArrayPoolByteOwner(rented);
            rented.AsSpan(0, bytes).Clear();
            return new VideoFrame(source.Width, source.Height, PixelFormat.Rgba32, rented.AsMemory(0, bytes), source.Pts, owner);
        }

        throw new NotSupportedException($"BasicPixelFormatConverter does not support {source.PixelFormat} -> {dstFormat}.");
    }

    public void Dispose() => _disposed = true;
}

