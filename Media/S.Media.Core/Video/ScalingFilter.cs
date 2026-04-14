namespace S.Media.Core.Video;

/// <summary>
/// Controls the upscaling algorithm used by GPU renderers (SDL3 and Avalonia) when the video
/// is displayed at a resolution different from its native pixel dimensions.
/// </summary>
public enum ScalingFilter
{
    /// <summary>
    /// Standard bilinear interpolation via <c>GL_LINEAR</c> texture filtering.
    /// Fast and suitable for moderate scaling ratios.
    /// </summary>
    Bilinear = 0,

    /// <summary>
    /// Catmull-Rom bicubic interpolation using a 4×4 <c>texelFetch</c> kernel rendered into an
    /// intermediate FBO at native video resolution, then blitted to the window.
    /// Significantly sharper than bilinear at the cost of 16 texture reads per output pixel.
    /// Recommended for broadcast monitoring where edge sharpness is critical.
    /// </summary>
    Bicubic = 1,

    /// <summary>
    /// Nearest-neighbour (no interpolation). Use for pixel-art content or when
    /// a 1:1 pixel-exact match is required.
    /// </summary>
    Nearest = 2,
}

