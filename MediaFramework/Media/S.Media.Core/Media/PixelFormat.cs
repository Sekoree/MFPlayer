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
    /// <summary>10-bit semi-planar YUV 4:2:0 (p010le). Direct output of D3D11VA / VAAPI hardware decode on 10-bit HEVC/AV1.</summary>
    P010,
    /// <summary>10-bit planar YUV 4:2:0 (yuv420p10le / I010). FFmpeg software decode output for 10-bit HEVC/VP9.</summary>
    Yuv420p10,
    /// <summary>8-bit planar YUV 4:4:4. Used by JPEG 2000, lossless profiles, and some professional capture cards.</summary>
    Yuv444p,
    /// <summary>24-bit packed RGB (no alpha). Used by screenshots, some industrial cameras, and PNG-sourced frames.</summary>
    Rgb24,
    /// <summary>24-bit packed BGR (no alpha). Variant of Rgb24 with swapped R/B channels.</summary>
    Bgr24,
    /// <summary>Single-channel 8-bit luma (greyscale). Used by monochrome cameras, IR sensors, and mask/keying sources.</summary>
    Gray8,
    /// <summary>
    /// Sentinel meaning "format unknown / follow source". Endpoints that
    /// support a passthrough mode (e.g. <c>NDIAVEndpoint</c> in Auto mode)
    /// recognise this value as a request to send each frame in whatever
    /// pixel format it arrives — no conversion, no FourCC override. Other
    /// endpoints should reject it (or treat it as a configuration error).
    /// Added at the end of the enum so existing positional values are unchanged.
    /// </summary>
    Unknown,
}

