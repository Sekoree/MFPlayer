namespace S.Media.Core.Media;

/// <summary>Pixel format for video frames travelling through the pipeline.</summary>
public enum PixelFormat
{
    Bgra32,
    Rgba32,
    Nv12,
    Yuv420p,
    Uyvy422,
    /// <summary>10-bit YUV 4:2:2 planar (yuv422p10le in FFmpeg). Required for high-bit-depth formats such as Apple ProRes 4444 and certain camera codecs.</summary>
    Yuv422p10,
}

