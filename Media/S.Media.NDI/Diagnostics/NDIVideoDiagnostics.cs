namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIVideoDiagnostics(
    long FramesCaptured,
    long FramesDropped,
    long RepeatedTimestampFramesPresented,
    double LastReadMs,
    long VideoPushSuccesses,
    long VideoPushFailures,
    long AudioPushSuccesses,
    long AudioPushFailures,
    double LastPushMs);

