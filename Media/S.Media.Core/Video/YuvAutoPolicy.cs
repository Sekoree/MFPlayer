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
            _ => YuvColorRange.Full
        };
    }

    public static YuvColorMatrix ResolveMatrix(YuvColorMatrix requested, int width, int height)
    {
        return requested switch
        {
            YuvColorMatrix.Bt601 => YuvColorMatrix.Bt601,
            YuvColorMatrix.Bt709 => YuvColorMatrix.Bt709,
            _ => width >= 1280 || height > 576 ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601
        };
    }
}

