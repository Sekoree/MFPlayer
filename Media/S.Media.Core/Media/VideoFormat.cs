namespace S.Media.Core.Media;

/// <summary>Describes the format of a video stream.</summary>
public readonly record struct VideoFormat(
    int         Width,
    int         Height,
    PixelFormat PixelFormat,
    int         FrameRateNumerator,
    int         FrameRateDenominator)
{
    /// <summary>Frame rate as a double (numerator / denominator).</summary>
    public double FrameRate =>
        FrameRateDenominator == 0 ? 0.0 : (double)FrameRateNumerator / FrameRateDenominator;

    public override string ToString() =>
        $"{Width}×{Height} {PixelFormat} @ {FrameRate:F3} fps";
}

