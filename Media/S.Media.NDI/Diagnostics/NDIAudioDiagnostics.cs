namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIAudioDiagnostics(
    long FramesCaptured,
    long FramesDropped,
    double LastReadMs);
