namespace S.Media.Core.Video;

/// <summary>
/// Shared policy resolver for YUV shader auto/default behavior.
/// </summary>
public static class YuvAutoPolicy
{
    public static YuvColorRange ResolveRange(YuvColorRange requested)
    {
        return requested switch
        {
            YuvColorRange.Full => YuvColorRange.Full,
            YuvColorRange.Limited => YuvColorRange.Limited,
            // Auto: virtually all H.264/H.265/VP9 video uses limited (studio) range
            // even when the container/codec reports UNSPECIFIED. Defaulting to Full
            // causes washed-out blacks/contrast because Y 16–235 is not expanded.
            _ => YuvColorRange.Limited
        };
    }

    public static YuvColorMatrix ResolveMatrix(YuvColorMatrix requested, int width, int height)
    {
        return requested switch
        {
            YuvColorMatrix.Bt601  => YuvColorMatrix.Bt601,
            YuvColorMatrix.Bt709  => YuvColorMatrix.Bt709,
            YuvColorMatrix.Bt2020 => YuvColorMatrix.Bt2020,
            // Auto: HD/UHD → BT.709, SD → BT.601 (BT.2020 is never auto-selected)
            _ => width >= 1280 || height > 576 ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601
        };
    }

    /// <summary>Converts a resolved <see cref="YuvColorMatrix"/> to the shader uniform integer value.</summary>
    public static int ToShaderValue(YuvColorMatrix matrix) => matrix switch
    {
        YuvColorMatrix.Bt2020 => 2,
        YuvColorMatrix.Bt709  => 1,
        _                     => 0,  // Bt601 / Auto → 0
    };
}

