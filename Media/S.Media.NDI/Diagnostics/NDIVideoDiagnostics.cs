namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIVideoDiagnostics(
    long FramesCaptured,
    long FramesDropped,
    long RepeatedTimestampFramesPresented,
    long FallbackFramesPresented,
    double LastReadMs,
    int JitterBufferFrames,
    int QueueDepth,
    string IncomingPixelFormat,
    string OutputPixelFormat,
    string ConversionPath,
    long VideoPushSuccesses,
    long VideoPushFailures,
    long AudioPushSuccesses,
    long AudioPushFailures,
    double LastPushMs);

