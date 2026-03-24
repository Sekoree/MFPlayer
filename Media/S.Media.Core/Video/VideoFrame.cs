using System.Threading;
using S.Media.Core.Errors;

namespace S.Media.Core.Video;

public sealed class VideoFrame : IDisposable
{
    private readonly Action<VideoFrame>? _releaseAction;
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
        Action<VideoFrame>? releaseAction = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

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
        _releaseAction = releaseAction;
    }

    public int Width { get; }

    public int Height { get; }

    public VideoPixelFormat PixelFormat { get; }

    public IPixelFormatData PixelFormatData { get; }

    public TimeSpan PresentationTime { get; }

    public bool IsKeyFrame { get; }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaseAction?.Invoke(this);
    }
}

