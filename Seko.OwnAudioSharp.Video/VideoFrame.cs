using System.Buffers;
using System.Collections.Concurrent;

namespace Seko.OwnAudioSharp.Video;

/// <summary>
/// A reference-counted, pooled RGBA video frame produced by <see cref="Decoders.IVideoDecoder"/>.
/// <para>
/// Each <see cref="VideoFrame"/> starts with a reference count of 1. Call <see cref="AddRef"/> to share
/// ownership across consumers and <see cref="Dispose"/> to release each reference. The underlying pixel
/// buffer is returned to <see cref="ArrayPool{T}.Shared"/> and the wrapper object is recycled internally
/// once the last reference is dropped.
/// </para>
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private static readonly ConcurrentBag<VideoFrame> _objectPool = new();
    private const int MaxObjectPoolSize = 48;

    private byte[]? _rgbaData;
    private readonly bool _pooled;
    private int _referenceCount;

    // Field-backed so Reinitialize can reset without a new allocation.
    private int _dataLength;
    private int _width;
    private int _height;
    private int _stride;
    private double _ptsSeconds;

    internal VideoFrame(byte[] rgbaData, int dataLength, int width, int height, int stride, double ptsSeconds, bool pooled)
    {
        _rgbaData = rgbaData;
        _dataLength = dataLength;
        _width = width;
        _height = height;
        _stride = stride;
        _ptsSeconds = ptsSeconds;
        _pooled = pooled;
        _referenceCount = 1;
    }

    /// <summary>Raw RGBA pixel data. Valid only while the frame has at least one live reference.</summary>
    public byte[] RgbaData => _rgbaData ?? Array.Empty<byte>();

    /// <summary>Number of valid bytes in <see cref="RgbaData"/> (may be less than <c>RgbaData.Length</c> due to pool renting).</summary>
    public int DataLength => _dataLength;

    /// <summary>Frame width in pixels.</summary>
    public int Width => _width;

    /// <summary>Frame height in pixels.</summary>
    public int Height => _height;

    /// <summary>Row stride in bytes (bytes per scanline, including any padding).</summary>
    public int Stride => _stride;

    /// <summary>Presentation timestamp in seconds relative to stream start.</summary>
    public double PtsSeconds => _ptsSeconds;

    internal static VideoFrame CreatePooled(int dataLength, int width, int height, int stride, double ptsSeconds)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(dataLength);

        if (_objectPool.TryTake(out var recycled))
        {
            recycled.Reinitialize(buffer, dataLength, width, height, stride, ptsSeconds);
            return recycled;
        }

        return new VideoFrame(buffer, dataLength, width, height, stride, ptsSeconds, pooled: true);
    }

    private void Reinitialize(byte[] rgbaData, int dataLength, int width, int height, int stride, double ptsSeconds)
    {
        _rgbaData = rgbaData;
        _dataLength = dataLength;
        _width = width;
        _height = height;
        _stride = stride;
        _ptsSeconds = ptsSeconds;
        Volatile.Write(ref _referenceCount, 1);
    }

    /// <summary>
    /// Increments the reference count and returns <see langword="this"/> for chaining.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the frame has already been fully disposed.</exception>
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

    /// <summary>
    /// Releases one reference. When the reference count reaches zero the pixel buffer is returned to
    /// <see cref="ArrayPool{T}.Shared"/> and the wrapper object is recycled for future frames.
    /// </summary>
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

                // Return the wrapper object to the pool for the next frame.
                if (_pooled && _objectPool.Count < MaxObjectPoolSize)
                    _objectPool.Add(this);
            }

            return;
        }
    }
}
