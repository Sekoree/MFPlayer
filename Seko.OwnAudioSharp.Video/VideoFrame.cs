using System.Buffers;

namespace Seko.OwnAudioSharp.Video;

public sealed class VideoFrame : IDisposable
{
    private byte[]? _rgbaData;
    private readonly bool _pooled;
    private int _referenceCount;

    internal VideoFrame(byte[] rgbaData, int dataLength, int width, int height, int stride, double ptsSeconds, bool pooled)
    {
        _rgbaData = rgbaData;
        DataLength = dataLength;
        Width = width;
        Height = height;
        Stride = stride;
        PtsSeconds = ptsSeconds;
        _pooled = pooled;
        _referenceCount = 1;
    }

    public byte[] RgbaData => _rgbaData ?? Array.Empty<byte>();
    public int DataLength { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public double PtsSeconds { get; }

    internal static VideoFrame CreatePooled(int dataLength, int width, int height, int stride, double ptsSeconds)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(dataLength);
        return new VideoFrame(buffer, dataLength, width, height, stride, ptsSeconds, pooled: true);
    }

    public VideoFrame AddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(VideoFrame));

            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                return this;
        }
    }

    public void Dispose()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
                return;

            if (Interlocked.CompareExchange(ref _referenceCount, current - 1, current) != current)
                continue;

            if (current == 1)
            {
                var buffer = Interlocked.Exchange(ref _rgbaData, null);
                if (_pooled && buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }

            return;
        }
    }
}
