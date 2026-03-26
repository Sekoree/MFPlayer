namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIEngineDiagnostics(
    NDIAudioDiagnostics Audio,
    NDIVideoSourceDebugInfo VideoSource,
    NDIVideoOutputDebugInfo VideoOutput,
    double ClockDriftMs,
    DateTimeOffset CapturedAtUtc);

