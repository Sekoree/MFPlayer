namespace S.Media.NDI.Diagnostics;

public readonly record struct NDIEngineDiagnostics(
    NDIAudioDiagnostics Audio,
    NDIVideoSourceDebugInfo VideoSource,
    NDIVideoOutputDebugInfo VideoOutput,
    /// <summary>
    /// Config-derived budget in milliseconds: <c>DiagnosticsTickInterval / MaxPendingVideoFrames</c>.
    /// This is a static hint derived from options, <b>not</b> a live clock-drift measurement.
    /// </summary>
    double DiagnosticsIntervalBudgetMs,
    DateTimeOffset CapturedAtUtc);
