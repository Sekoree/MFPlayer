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

    /// <summary>
    /// Creates a <see cref="VideoFormat"/> from a frame rate expressed as a simple <c>double</c>.
    /// Internally encodes the rate as <c>round(fps * 1000) / 1000</c> to preserve sub-integer
    /// rates (e.g. 29.97) while keeping denominator fixed.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="pixelFormat">Pixel format.</param>
    /// <param name="fps">Frames per second (e.g. 30.0, 59.94).</param>
    public static VideoFormat Create(int width, int height, PixelFormat pixelFormat, double fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "Frame rate must be positive.");
        return new VideoFormat(width, height, pixelFormat,
            (int)Math.Round(fps * 1000), 1000);
    }

    public override string ToString() =>
        $"{Width}×{Height} {PixelFormat} @ {FrameRate:F3} fps";
}

