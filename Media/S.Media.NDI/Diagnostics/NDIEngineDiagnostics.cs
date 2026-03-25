namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIEngineDiagnostics(
    NDIAudioDiagnostics Audio,
    NDIVideoDiagnostics Video,
    double ClockDriftMs,
    DateTimeOffset CapturedAtUtc);

