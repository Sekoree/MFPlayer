namespace S.Media.Core.Video;

/// <summary>
/// Immutable YUV shader configuration for renderer color-space handling.
/// Consolidates range and matrix selection that previously required four
/// separate properties on each output.
/// </summary>
public readonly record struct YuvShaderConfig(
    YuvColorRange Range = YuvColorRange.Auto,
    YuvColorMatrix Matrix = YuvColorMatrix.Auto)
{
    public static YuvShaderConfig Default => new(YuvColorRange.Auto, YuvColorMatrix.Auto);
}

