using System.Buffers;
using System.Collections.Concurrent;

namespace Seko.OwnAudioNET.Video;

/// <summary>
/// A reference-counted, pooled multi-plane video frame produced by <see cref="Decoders.IVideoDecoder"/>.
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
    private static int _pooledObjectCount;
    [ThreadStatic] private static VideoFrame? _threadLocalFrame;

    private byte[]? _plane0;
    private byte[]? _plane1;
    private byte[]? _plane2;
    private readonly bool _pooled;
    private int _referenceCount;

    // Field-backed so Reinitialize can reset without a new allocation.
    private VideoPixelFormat _pixelFormat;
    private int _planeCount;
    private int _plane0Length;
    private int _plane1Length;
    private int _plane2Length;
    private int _width;
    private int _height;
    private int _plane0Stride;
    private int _plane1Stride;
    private int _plane2Stride;
    private double _ptsSeconds;

    internal VideoFrame(bool pooled)
    {
        _pooled = pooled;
        Reinitialize(
            pixelFormat: VideoPixelFormat.Rgba32,
            plane0: null,
            plane0Length: 0,
            plane0Stride: 0,
            plane1: null,
            plane1Length: 0,
            plane1Stride: 0,
            plane2: null,
            plane2Length: 0,
            plane2Stride: 0,
            width: 0,
            height: 0,
            ptsSeconds: 0);
    }

    /// <summary>Frame pixel format.</summary>
    public VideoPixelFormat PixelFormat => _pixelFormat;

    /// <summary>Number of planes in this frame.</summary>
    public int PlaneCount => _planeCount;

    /// <summary>Frame width in pixels.</summary>
    public int Width => _width;

    /// <summary>Frame height in pixels.</summary>
    public int Height => _height;

    /// <summary>Presentation timestamp in seconds relative to stream start.</summary>
    public double PtsSeconds => _ptsSeconds;

    internal static VideoFrame CreatePooledRgba32(int dataLength, int width, int height, int stride, double ptsSeconds)
    {
        var plane0 = ArrayPool<byte>.Shared.Rent(Math.Max(1, dataLength));

        var frame = RentFrame();
        frame.Reinitialize(
            pixelFormat: VideoPixelFormat.Rgba32,
            plane0: plane0,
            plane0Length: dataLength,
            plane0Stride: stride,
            plane1: null,
            plane1Length: 0,
            plane1Stride: 0,
            plane2: null,
            plane2Length: 0,
            plane2Stride: 0,
            width: width,
            height: height,
            ptsSeconds: ptsSeconds);
        return frame;
    }

    internal static VideoFrame CreatePooledNv12(int yLength, int uvLength, int width, int height, int yStride, int uvStride, double ptsSeconds)
    {
        var plane0 = ArrayPool<byte>.Shared.Rent(Math.Max(1, yLength));
        var plane1 = ArrayPool<byte>.Shared.Rent(Math.Max(1, uvLength));

        var frame = RentFrame();
        frame.Reinitialize(
            pixelFormat: VideoPixelFormat.Nv12,
            plane0: plane0,
            plane0Length: yLength,
            plane0Stride: yStride,
            plane1: plane1,
            plane1Length: uvLength,
            plane1Stride: uvStride,
            plane2: null,
            plane2Length: 0,
            plane2Stride: 0,
            width: width,
            height: height,
            ptsSeconds: ptsSeconds);
        return frame;
    }

    /// <summary>Generic factory for any 3-plane format. Callers supply pre-computed byte lengths and strides.</summary>
    internal static VideoFrame CreatePooled3Plane(
        VideoPixelFormat pixelFormat,
        int plane0Length, int plane1Length, int plane2Length,
        int width, int height,
        int plane0Stride, int plane1Stride, int plane2Stride,
        double ptsSeconds)
    {
        var plane0 = ArrayPool<byte>.Shared.Rent(Math.Max(1, plane0Length));
        var plane1 = ArrayPool<byte>.Shared.Rent(Math.Max(1, plane1Length));
        var plane2 = ArrayPool<byte>.Shared.Rent(Math.Max(1, plane2Length));

        var frame = RentFrame();
        frame.Reinitialize(
            pixelFormat: pixelFormat,
            plane0: plane0, plane0Length: plane0Length, plane0Stride: plane0Stride,
            plane1: plane1, plane1Length: plane1Length, plane1Stride: plane1Stride,
            plane2: plane2, plane2Length: plane2Length, plane2Stride: plane2Stride,
            width: width, height: height, ptsSeconds: ptsSeconds);
        return frame;
    }

    /// <summary>Generic factory for any 2-plane (semi-planar) format.</summary>
    internal static VideoFrame CreatePooled2Plane(
        VideoPixelFormat pixelFormat,
        int plane0Length, int plane1Length,
        int width, int height,
        int plane0Stride, int plane1Stride,
        double ptsSeconds)
    {
        var plane0 = ArrayPool<byte>.Shared.Rent(Math.Max(1, plane0Length));
        var plane1 = ArrayPool<byte>.Shared.Rent(Math.Max(1, plane1Length));

        var frame = RentFrame();
        frame.Reinitialize(
            pixelFormat: pixelFormat,
            plane0: plane0, plane0Length: plane0Length, plane0Stride: plane0Stride,
            plane1: plane1, plane1Length: plane1Length, plane1Stride: plane1Stride,
            plane2: null, plane2Length: 0, plane2Stride: 0,
            width: width, height: height, ptsSeconds: ptsSeconds);
        return frame;
    }

    // ── 4:2:2 8-bit ──────────────────────────────────────────────────────────
    internal static VideoFrame CreatePooledYuv422p(
        int yLength, int uLength, int vLength,
        int width, int height,
        int yStride, int uStride, int vStride,
        double ptsSeconds) =>
        CreatePooled3Plane(VideoPixelFormat.Yuv422p,
            yLength, uLength, vLength, width, height, yStride, uStride, vStride, ptsSeconds);

    // ── 4:2:2 10-bit ─────────────────────────────────────────────────────────
    internal static VideoFrame CreatePooledYuv422p10le(
        int yLength, int uLength, int vLength,
        int width, int height,
        int yStride, int uStride, int vStride,
        double ptsSeconds) =>
        CreatePooled3Plane(VideoPixelFormat.Yuv422p10le,
            yLength, uLength, vLength, width, height, yStride, uStride, vStride, ptsSeconds);

    // ── 4:2:0 10-bit semi-planar (P010LE) ────────────────────────────────────
    internal static VideoFrame CreatePooledP010le(
        int yLength, int uvLength,
        int width, int height,
        int yStride, int uvStride,
        double ptsSeconds) =>
        CreatePooled2Plane(VideoPixelFormat.P010le,
            yLength, uvLength, width, height, yStride, uvStride, ptsSeconds);

    // ── 4:2:0 10-bit planar ───────────────────────────────────────────────────
    internal static VideoFrame CreatePooledYuv420p10le(
        int yLength, int uLength, int vLength,
        int width, int height,
        int yStride, int uStride, int vStride,
        double ptsSeconds) =>
        CreatePooled3Plane(VideoPixelFormat.Yuv420p10le,
            yLength, uLength, vLength, width, height, yStride, uStride, vStride, ptsSeconds);

    // ── 4:4:4 8-bit ───────────────────────────────────────────────────────────
    internal static VideoFrame CreatePooledYuv444p(
        int yLength, int uLength, int vLength,
        int width, int height,
        int yStride, int uStride, int vStride,
        double ptsSeconds) =>
        CreatePooled3Plane(VideoPixelFormat.Yuv444p,
            yLength, uLength, vLength, width, height, yStride, uStride, vStride, ptsSeconds);

    // ── 4:4:4 10-bit ──────────────────────────────────────────────────────────
    internal static VideoFrame CreatePooledYuv444p10le(
        int yLength, int uLength, int vLength,
        int width, int height,
        int yStride, int uStride, int vStride,
        double ptsSeconds) =>
        CreatePooled3Plane(VideoPixelFormat.Yuv444p10le,
            yLength, uLength, vLength, width, height, yStride, uStride, vStride, ptsSeconds);

    internal static VideoFrame CreatePooledYuv420p(int yLength, int uLength, int vLength, int width, int height, int yStride, int uStride, int vStride, double ptsSeconds)
    {
        var plane0 = ArrayPool<byte>.Shared.Rent(Math.Max(1, yLength));
        var plane1 = ArrayPool<byte>.Shared.Rent(Math.Max(1, uLength));
        var plane2 = ArrayPool<byte>.Shared.Rent(Math.Max(1, vLength));

        var frame = RentFrame();
        frame.Reinitialize(
            pixelFormat: VideoPixelFormat.Yuv420p,
            plane0: plane0,
            plane0Length: yLength,
            plane0Stride: yStride,
            plane1: plane1,
            plane1Length: uLength,
            plane1Stride: uStride,
            plane2: plane2,
            plane2Length: vLength,
            plane2Stride: vStride,
            width: width,
            height: height,
            ptsSeconds: ptsSeconds);
        return frame;
    }

    /// <summary>Gets the backing byte[] for a plane (0-based).</summary>
    public byte[] GetPlaneData(int planeIndex)
    {
        return planeIndex switch
        {
            0 => _plane0 ?? Array.Empty<byte>(),
            1 => _plane1 ?? Array.Empty<byte>(),
            2 => _plane2 ?? Array.Empty<byte>(),
            _ => throw new ArgumentOutOfRangeException(nameof(planeIndex))
        };
    }

    /// <summary>Gets the valid byte length for a plane (0-based).</summary>
    public int GetPlaneLength(int planeIndex)
    {
        return planeIndex switch
        {
            0 => _plane0Length,
            1 => _plane1Length,
            2 => _plane2Length,
            _ => throw new ArgumentOutOfRangeException(nameof(planeIndex))
        };
    }

    /// <summary>Gets the row stride in bytes for a plane (0-based).</summary>
    public int GetPlaneStride(int planeIndex)
    {
        return planeIndex switch
        {
            0 => _plane0Stride,
            1 => _plane1Stride,
            2 => _plane2Stride,
            _ => throw new ArgumentOutOfRangeException(nameof(planeIndex))
        };
    }

    private static VideoFrame RentFrame()
    {
        var cached = _threadLocalFrame;
        if (cached != null)
        {
            _threadLocalFrame = null;
            return cached;
        }

        if (_objectPool.TryTake(out var recycled))
        {
            Interlocked.Decrement(ref _pooledObjectCount);
            return recycled;
        }

        return new VideoFrame(pooled: true);
    }

    private void Reinitialize(
        VideoPixelFormat pixelFormat,
        byte[]? plane0,
        int plane0Length,
        int plane0Stride,
        byte[]? plane1,
        int plane1Length,
        int plane1Stride,
        byte[]? plane2,
        int plane2Length,
        int plane2Stride,
        int width,
        int height,
        double ptsSeconds)
    {
        _pixelFormat = pixelFormat;
        _plane0 = plane0;
        _plane1 = plane1;
        _plane2 = plane2;
        _plane0Length = Math.Max(0, plane0Length);
        _plane1Length = Math.Max(0, plane1Length);
        _plane2Length = Math.Max(0, plane2Length);
        _plane0Stride = Math.Max(0, plane0Stride);
        _plane1Stride = Math.Max(0, plane1Stride);
        _plane2Stride = Math.Max(0, plane2Stride);
        _planeCount = plane2 != null ? 3 : (plane1 != null ? 2 : (plane0 != null ? 1 : 0));
        _width = width;
        _height = height;
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
                var plane0 = Interlocked.Exchange(ref _plane0, null);
                var plane1 = Interlocked.Exchange(ref _plane1, null);
                var plane2 = Interlocked.Exchange(ref _plane2, null);
                if (_pooled)
                {
                    if (plane0 != null)
                        ArrayPool<byte>.Shared.Return(plane0, clearArray: false);
                    if (plane1 != null)
                        ArrayPool<byte>.Shared.Return(plane1, clearArray: false);
                    if (plane2 != null)
                        ArrayPool<byte>.Shared.Return(plane2, clearArray: false);
                }

                _plane0Length = 0;
                _plane1Length = 0;
                _plane2Length = 0;
                _plane0Stride = 0;
                _plane1Stride = 0;
                _plane2Stride = 0;
                _planeCount = 0;
                _width = 0;
                _height = 0;
                _ptsSeconds = 0;

                // Return the wrapper object to the pool for the next frame.
                if (_pooled)
                {
                    if (_threadLocalFrame == null)
                    {
                        _threadLocalFrame = this;
                        return;
                    }

                    var pooledCount = Interlocked.Increment(ref _pooledObjectCount);
                    if (pooledCount <= MaxObjectPoolSize)
                    {
                        _objectPool.Add(this);
                    }
                    else
                    {
                        Interlocked.Decrement(ref _pooledObjectCount);
                    }
                }
            }

            return;
        }
    }
}
