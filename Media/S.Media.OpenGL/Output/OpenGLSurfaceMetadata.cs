using S.Media.Core.Video;

namespace S.Media.OpenGL.Output;

public readonly record struct OpenGLSurfaceMetadata(
    int SurfaceWidth,
    int SurfaceHeight,
    VideoPixelFormat PixelFormat,
    int PlaneCount,
    IReadOnlyList<int> PlaneStrides,
    long LastPresentedFrameGeneration)
{
    public static OpenGLSurfaceMetadata Empty { get; } = new(
        SurfaceWidth: 0,
        SurfaceHeight: 0,
        PixelFormat: VideoPixelFormat.Unknown,
        PlaneCount: 0,
        PlaneStrides: Array.Empty<int>(),
        LastPresentedFrameGeneration: 0);
}
