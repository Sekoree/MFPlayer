namespace S.Media.FFmpeg;

/// <summary>
/// §5.9 — Configuration for the automatic A/V drift correction loop started
/// by <see cref="MediaPlayerBuilder.WithAutoAvDriftCorrection"/>.
/// </summary>
public sealed record AvDriftCorrectionOptions
{
    /// <summary>
    /// Initial settling delay before the first correction pass.
    /// Default 30 s (matches NDIAutoPlayer's warmup behaviour).
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to run correction once active. Default 30 s.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum absolute drift (ms) before a correction step is applied. Default 20 ms.
    /// </summary>
    public double MinDriftMs { get; init; } = 20;

    /// <summary>
    /// Ignore drifts above this threshold (treated as reconnect/outlier). Default 250 ms.
    /// </summary>
    public double IgnoreOutlierDriftMs { get; init; } = 250;

    /// <summary>
    /// Gain applied to the measured drift when computing a correction step.
    /// Default 0.50 (half-step convergence).
    /// </summary>
    public double CorrectionGain { get; init; } = 0.50;

    /// <summary>
    /// Maximum magnitude of one correction step (ms). Default 40 ms.
    /// </summary>
    public double MaxStepMs { get; init; } = 40;

    /// <summary>
    /// Absolute cap on accumulated offset (ms). Default 250 ms.
    /// </summary>
    public double MaxAbsOffsetMs { get; init; } = 250;
}
