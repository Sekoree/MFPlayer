namespace S.Media.NDI.Config;

public enum NDIVideoFallbackMode
{
    NoFrame = 0,
    PresentLastFrameOnRepeatedTimestamp = 1,
    PresentLastFrameUntilTimeout = 2,
}

