using System.Buffers;
using System.Threading;
using S.Media.Core.Errors;

namespace S.Media.Core.Video;

public sealed class VideoFrame : IDisposable
{
    private readonly Action<VideoFrame>? _releaseAction;
    private int _refCount = 1;
    private int _disposed;

    public VideoFrame(
        int width,
        int height,
        VideoPixelFormat pixelFormat,
        IPixelFormatData pixelFormatData,
        TimeSpan presentationTime,
        bool isKeyFrame,
        ReadOnlyMemory<byte> plane0,
        int plane0Stride,
        ReadOnlyMemory<byte> plane1 = default,
        int plane1Stride = 0,
        ReadOnlyMemory<byte> plane2 = default,
        int plane2Stride = 0,
        ReadOnlyMemory<byte> plane3 = default,
        int plane3Stride = 0,
        Action<VideoFrame>? releaseAction = null,
        bool isFullRange = false)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(pixelFormatData);
        if (pixelFormatData.Format != pixelFormat)
        {
            throw new ArgumentException("Pixel format data does not match frame pixel format.", nameof(pixelFormatData));
        }

        ValidatePlaneShape(
            width,
            height,
            pixelFormat,
            plane0,
            plane0Stride,
            plane1,
            plane1Stride,
            plane2,
            plane2Stride,
            plane3,
            plane3Stride);

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        PixelFormatData = pixelFormatData;
        PresentationTime = presentationTime;
        IsKeyFrame = isKeyFrame;
        Plane0 = plane0;
        Plane1 = plane1;
        Plane2 = plane2;
        Plane3 = plane3;
        Plane0Stride = plane0Stride;
        Plane1Stride = plane1Stride;
        Plane2Stride = plane2Stride;
        Plane3Stride = plane3Stride;
        IsFullRange = isFullRange;
        _releaseAction = releaseAction;
    }

    /// <summary>
    /// Creates a <see cref="VideoFrame"/> whose plane buffers are owned by the supplied
    /// <see cref="IMemoryOwner{T}"/> instances.  When the frame's reference count reaches zero
    /// (i.e. after the matching number of <see cref="Dispose"/> calls), every non-<see langword="null"/>
    /// owner is disposed, returning its backing buffer to its originating pool.
    /// </summary>
    /// <remarks>
    /// <b>Ownership transfer:</b> the caller must NOT dispose the owners after passing them here.
    /// The frame takes sole ownership.  The owners are disposed exactly once when the final
    /// reference is released.
    /// <para>
    /// Typical pattern (e.g. in a decoder that rents from <see cref="System.Buffers.ArrayPool{T}"/>):
    /// <code>
    /// IMemoryOwner&lt;byte&gt; p0 = MemoryPool&lt;byte&gt;.Shared.Rent(requiredBytes);
    /// // fill p0.Memory.Span …
    /// var frame = VideoFrame.FromOwned(width, height, format, fmtData, pts, isKey, p0, stride);
    /// // Do NOT dispose p0 here — the frame owns it now.
    /// </code>
    /// </para>
    /// </remarks>
    public static VideoFrame FromOwned(
        int width,
        int height,
        VideoPixelFormat pixelFormat,
        IPixelFormatData pixelFormatData,
        TimeSpan presentationTime,
        bool isKeyFrame,
        IMemoryOwner<byte> plane0,
        int plane0Stride,
        IMemoryOwner<byte>? plane1 = null,
        int plane1Stride = 0,
        IMemoryOwner<byte>? plane2 = null,
        int plane2Stride = 0,
        IMemoryOwner<byte>? plane3 = null,
        int plane3Stride = 0,
        bool isFullRange = false)
    {
        ArgumentNullException.ThrowIfNull(plane0);
        return new VideoFrame(
            width, height, pixelFormat, pixelFormatData, presentationTime, isKeyFrame,
            plane0.Memory, plane0Stride,
            plane1?.Memory ?? default, plane1Stride,
            plane2?.Memory ?? default, plane2Stride,
            plane3?.Memory ?? default, plane3Stride,
            releaseAction: _ =>
            {
                plane0.Dispose();
                plane1?.Dispose();
                plane2?.Dispose();
                plane3?.Dispose();
            },
            isFullRange: isFullRange);
    }

    // ── P3.3: non-throwing TryCreate factory ──────────────────────────────────

    /// <summary>
    /// Creates a <see cref="VideoFrame"/> using the project's error-code convention.
    /// Returns <see cref="MediaResult.Success"/> and populates <paramref name="frame"/> on success;
    /// returns an error code and sets <paramref name="frame"/> to <see langword="null"/> on invalid arguments.
    /// </summary>
    public static int TryCreate(
        int width,
        int height,
        VideoPixelFormat pixelFormat,
        IPixelFormatData pixelFormatData,
        TimeSpan presentationTime,
        bool isKeyFrame,
        ReadOnlyMemory<byte> plane0,
        int plane0Stride,
        out VideoFrame? frame,
        ReadOnlyMemory<byte> plane1 = default,
        int plane1Stride = 0,
        ReadOnlyMemory<byte> plane2 = default,
        int plane2Stride = 0,
        ReadOnlyMemory<byte> plane3 = default,
        int plane3Stride = 0,
        Action<VideoFrame>? releaseAction = null,
        bool isFullRange = false)
    {
        frame = null;

        try
        {
            frame = new VideoFrame(
                width, height, pixelFormat, pixelFormatData, presentationTime, isKeyFrame,
                plane0, plane0Stride, plane1, plane1Stride, plane2, plane2Stride,
                plane3, plane3Stride, releaseAction, isFullRange);
            return MediaResult.Success;
        }
        catch (ArgumentOutOfRangeException)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }
        catch (ArgumentNullException)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }
        catch (ArgumentException)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }
    }

    private static void ValidatePlaneShape(
        int width,
        int height,
        VideoPixelFormat pixelFormat,
        ReadOnlyMemory<byte> plane0,
        int plane0Stride,
        ReadOnlyMemory<byte> plane1,
        int plane1Stride,
        ReadOnlyMemory<byte> plane2,
        int plane2Stride,
        ReadOnlyMemory<byte> plane3,
        int plane3Stride)
    {
        if (plane0.IsEmpty || plane0Stride <= 0)
        {
            throw new ArgumentException("Plane0 payload and stride are required.");
        }

        ValidatePlane(plane0, plane0Stride, width, height, bytesPerSample: GetLumaBytesPerSample(pixelFormat), "Plane0");

        switch (pixelFormat)
        {
            case VideoPixelFormat.Rgba32:
            case VideoPixelFormat.Bgra32:
                if (plane0Stride < width * 4)
                {
                    throw new ArgumentException("Packed RGBA/BGRA requires plane0 stride >= width * 4.");
                }

                EnsureAbsent(plane1, plane1Stride, "Plane1");
                EnsureAbsent(plane2, plane2Stride, "Plane2");
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;

            case VideoPixelFormat.Nv12:
            case VideoPixelFormat.P010Le:
            {
                var bytesPerSample = pixelFormat == VideoPixelFormat.P010Le ? 2 : 1;
                ValidatePlane(plane1, plane1Stride, width, (height + 1) / 2, bytesPerSample, "Plane1");
                EnsureAbsent(plane2, plane2Stride, "Plane2");
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;
            }

            case VideoPixelFormat.Yuv420P:
            case VideoPixelFormat.Yuv420P10Le:
            {
                var chromaWidth = (width + 1) / 2;
                var chromaHeight = (height + 1) / 2;
                var bytesPerSample = pixelFormat == VideoPixelFormat.Yuv420P10Le ? 2 : 1;
                ValidatePlane(plane1, plane1Stride, chromaWidth, chromaHeight, bytesPerSample, "Plane1");
                ValidatePlane(plane2, plane2Stride, chromaWidth, chromaHeight, bytesPerSample, "Plane2");
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;
            }

            case VideoPixelFormat.Yuv422P:
            case VideoPixelFormat.Yuv422P10Le:
            {
                var chromaWidth = (width + 1) / 2;
                var bytesPerSample = pixelFormat == VideoPixelFormat.Yuv422P10Le ? 2 : 1;
                ValidatePlane(plane1, plane1Stride, chromaWidth, height, bytesPerSample, "Plane1");
                ValidatePlane(plane2, plane2Stride, chromaWidth, height, bytesPerSample, "Plane2");
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;
            }

            case VideoPixelFormat.Yuv444P:
            case VideoPixelFormat.Yuv444P10Le:
            {
                var bytesPerSample = pixelFormat == VideoPixelFormat.Yuv444P10Le ? 2 : 1;
                ValidatePlane(plane1, plane1Stride, width, height, bytesPerSample, "Plane1");
                ValidatePlane(plane2, plane2Stride, width, height, bytesPerSample, "Plane2");
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;
            }

            default:
                EnsureAbsent(plane3, plane3Stride, "Plane3");
                return;
        }
    }

    private static int GetLumaBytesPerSample(VideoPixelFormat pixelFormat)
    {
        return pixelFormat is VideoPixelFormat.Yuv422P10Le or VideoPixelFormat.P010Le or VideoPixelFormat.Yuv420P10Le or VideoPixelFormat.Yuv444P10Le
            ? 2
            : pixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32
                ? 4
                : 1;
    }

    private static void ValidatePlane(ReadOnlyMemory<byte> plane, int stride, int width, int height, int bytesPerSample, string planeName)
    {
        if (plane.IsEmpty || stride <= 0)
        {
            throw new ArgumentException($"{planeName} payload and stride are required.");
        }

        var minStride = Math.Max(1, width * bytesPerSample);
        if (stride < minStride)
        {
            throw new ArgumentException($"{planeName} stride is smaller than required minimum.");
        }

        var minBytes = (long)stride * Math.Max(1, height);
        if (plane.Length < minBytes)
        {
            throw new ArgumentException($"{planeName} payload length is smaller than required minimum.");
        }
    }

    private static void EnsureAbsent(ReadOnlyMemory<byte> plane, int stride, string planeName)
    {
        if (!plane.IsEmpty || stride != 0)
        {
            throw new ArgumentException($"{planeName} must be absent for this pixel format.");
        }
    }

    public int Width { get; }

    public int Height { get; }

    public VideoPixelFormat PixelFormat { get; }

    public IPixelFormatData PixelFormatData { get; }

    public TimeSpan PresentationTime { get; }

    public bool IsKeyFrame { get; }

    /// <summary>
    /// When <see langword="true"/>, YUV sample values use full range (0–255 for 8-bit).
    /// When <see langword="false"/> (the default), values use limited/TV range (16–235 luma,
    /// 16–240 chroma for 8-bit), as produced by most H.264 and H.265 encoders.
    /// Renderers use this to apply the correct range-expansion matrix before colour conversion.
    /// </summary>
    public bool IsFullRange { get; }

    public ReadOnlyMemory<byte> Plane0 { get; }

    public ReadOnlyMemory<byte> Plane1 { get; }

    public ReadOnlyMemory<byte> Plane2 { get; }

    public ReadOnlyMemory<byte> Plane3 { get; }

    public int Plane0Stride { get; }

    public int Plane1Stride { get; }

    public int Plane2Stride { get; }

    public int Plane3Stride { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Shared guard used by output paths to enforce deterministic disposed-frame behavior.
    public int ValidateForPush()
    {
        return IsDisposed ? (int)MediaErrorCode.VideoFrameDisposed : MediaResult.Success;
    }

    /// <summary>
    /// Increments the reference count, keeping this frame alive past the original owner's
    /// <see cref="Dispose"/> call. The returned instance is the same object; the caller must
    /// call <see cref="Dispose"/> when done.
    /// </summary>
    /// <returns>The same <see cref="VideoFrame"/> instance with an incremented reference count.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the frame has already been fully released.</exception>
    /// <remarks>
    /// <b>Ownership rule:</b> every code path that stores or enqueues a <see cref="VideoFrame"/>
    /// across a scope boundary <em>must</em> call <c>AddRef()</c> before storing, and <em>must</em>
    /// call <c>Dispose()</c> when done. Failing to call <c>AddRef()</c> before enqueue is a
    /// silent use-after-free bug — the backing buffer may be returned to the pool while the
    /// enqueued reference is still live.
    /// <para>
    /// Pattern:
    /// <code>
    /// frame.AddRef();           // keep alive
    /// _queue.Enqueue(frame);    // safe — ref-count now ≥ 2
    /// // … later, in consumer:
    /// var f = _queue.Dequeue();
    /// try { Process(f); }
    /// finally { f.Dispose(); } // drops the AddRef
    /// </code>
    /// </para>
    /// </remarks>
    public VideoFrame AddRef()
    {
        // CAS loop: safely increment only if the ref-count is still positive.
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }
        }
        while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);

        return this;
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _releaseAction?.Invoke(this);
            }
        }
    }
}
