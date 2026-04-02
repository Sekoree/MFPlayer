using S.Media.Core.Video;

namespace S.Media.OpenGL.Conversion;

/// <summary>
/// Classifies a <see cref="VideoFrame"/>'s pixel format into an upload strategy that
/// <see cref="Upload.GlTextureUploader"/> can execute. This keeps pixel-format knowledge
/// out of backend-specific code (SDL3, Avalonia, etc.).
/// </summary>
internal static class FrameFormatRouter
{
    /// <summary>
    /// Returns the <see cref="UploadStrategy"/> for the given pixel format.
    /// <see cref="UploadStrategy.Unsupported"/> is returned for formats that have no
    /// known GPU upload path.
    /// </summary>
    public static UploadStrategy Classify(VideoPixelFormat format)
    {
        return format switch
        {
            VideoPixelFormat.Rgba32      => UploadStrategy.PackedRgba,
            VideoPixelFormat.Bgra32      => UploadStrategy.PackedRgba,
            VideoPixelFormat.Nv12        => UploadStrategy.SemiPlanarYuv,
            VideoPixelFormat.P010Le      => UploadStrategy.SemiPlanarYuv,
            VideoPixelFormat.Yuv420P     => UploadStrategy.PlanarYuv,
            VideoPixelFormat.Yuv420P10Le => UploadStrategy.PlanarYuv,
            VideoPixelFormat.Yuv422P     => UploadStrategy.PlanarYuv,
            VideoPixelFormat.Yuv422P10Le => UploadStrategy.PlanarYuv,
            VideoPixelFormat.Yuv444P     => UploadStrategy.PlanarYuv,
            VideoPixelFormat.Yuv444P10Le => UploadStrategy.PlanarYuv,
            _                            => UploadStrategy.Unsupported,
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> if the format uses a multi-texture YUV shader
    /// path (either semi-planar or fully planar).
    /// </summary>
    public static bool IsYuv(VideoPixelFormat format)
    {
        var strategy = Classify(format);
        return strategy is UploadStrategy.PlanarYuv or UploadStrategy.SemiPlanarYuv;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the format is a packed single-texture RGBA/BGRA format.
    /// </summary>
    public static bool IsPackedRgba(VideoPixelFormat format)
        => Classify(format) == UploadStrategy.PackedRgba;
}

/// <summary>
/// Describes the GPU upload path for a video frame.
/// </summary>
internal enum UploadStrategy
{
    /// <summary>No known upload path for this format.</summary>
    Unsupported = 0,

    /// <summary>Single RGBA/BGRA texture (1 plane, 4 bytes per pixel).</summary>
    PackedRgba = 1,

    /// <summary>Semi-planar YUV (NV12, P010LE): Y texture + interleaved UV texture.</summary>
    SemiPlanarYuv = 2,

    /// <summary>Fully planar YUV (YUV420P, etc.): Y + U + V textures.</summary>
    PlanarYuv = 3,
}

