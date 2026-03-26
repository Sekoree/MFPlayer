namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIVideoSourceDebugInfo(
    long FramesCaptured,
    long FramesDropped,
    long RepeatedTimestampFramesPresented,
    long FallbackFramesPresented,
    double LastReadMs,
    int JitterBufferFrames,
    int QueueDepth,
    string IncomingPixelFormat,
    string OutputPixelFormat,
    string ConversionPath);

public readonly record struct NDIVideoOutputDebugInfo(
    long VideoPushSuccesses,
    long VideoPushFailures,
    long AudioPushSuccesses,
    long AudioPushFailures,
    double LastPushMs);

