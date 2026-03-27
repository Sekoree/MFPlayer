namespace S.Media.Core.Errors;

public static class ErrorCodeRanges
{
    public static bool IsValid(MediaErrorCode code)
    {
        var value = (int)code;
        return IsValid(value);
    }

    public static bool IsSuccess(int code) => code == MediaResult.Success;

    public static bool IsFailure(int code) => code != MediaResult.Success;

    public static MediaErrorArea ResolveArea(MediaErrorCode code)
    {
        var value = (int)code;
        return ResolveArea(value);
    }

    public static int ResolveSharedSemantic(int code)
    {
        return code switch
        {
            (int)MediaErrorCode.FFmpegConcurrentReadViolation => (int)MediaErrorCode.MediaConcurrentOperationViolation,
            (int)MediaErrorCode.MIDIConcurrentOperationRejected => (int)MediaErrorCode.MediaConcurrentOperationViolation,
            (int)MediaErrorCode.NDIAudioReadRejected => (int)MediaErrorCode.MediaConcurrentOperationViolation,
            (int)MediaErrorCode.NDIVideoReadRejected => (int)MediaErrorCode.MediaConcurrentOperationViolation,
            _ => code,
        };
    }

    public static bool IsGenericAudioCode(int code) => code is >= 4200 and <= 4299;

    public static bool IsPortAudioCode(int code) => MediaErrorAllocations.PortAudioActive.Contains(code);

    private static bool IsValid(int code)
    {
        return code switch
        {
            >= 0 and <= 999 => true,
            >= 1000 and <= 1999 => true,
            >= 2000 and <= 2999 => true,
            >= 3000 and <= 3999 => true,
            >= 4000 and <= 4999 => true,
            >= 5000 and <= 5199 => true,
            _ => false,
        };
    }

    private static MediaErrorArea ResolveArea(int code)
    {
        if (code >= 0 && code <= 999)
        {
            return MediaErrorArea.GenericCommon;
        }

        if (code >= 1000 && code <= 1999)
        {
            return MediaErrorArea.Playback;
        }

        if (code >= 2000 && code <= 2999)
        {
            return MediaErrorArea.Decoding;
        }

        if (code >= 3000 && code <= 3999)
        {
            return MediaErrorArea.Mixing;
        }

        if (code >= 4000 && code <= 4999)
        {
            return MediaErrorArea.OutputRender;
        }

        if (code >= 5000 && code <= 5199)
        {
            return MediaErrorArea.NDI;
        }

        return MediaErrorArea.Unknown;
    }
}
