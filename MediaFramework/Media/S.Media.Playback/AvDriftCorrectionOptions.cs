namespace S.Media.Playback;

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
    /// Single-sample threshold above which the drift loop *suspects* a
    /// reconnect / decoder hiccup / freeze rather than real A/V error.
    /// A sample over this cap is gated by <see cref="OutlierConsecutiveSamples"/>
    /// before being honored: if N consecutive samples all exceed the cap the
    /// drift is treated as real and corrected with a clamped step.
    /// Default 250 ms (matches the historical hard cap; the new persistence
    /// gate is what prevents a single outlier from being applied as a step).
    /// </summary>
    public double IgnoreOutlierDriftMs { get; init; } = 250;

    /// <summary>
    /// Number of consecutive samples that must remain above
    /// <see cref="IgnoreOutlierDriftMs"/> before the loop concludes the drift
    /// is real and applies a clamped correction. Setting this to 1 reproduces
    /// the previous "always honor" behaviour; larger values trade in latency
    /// for resilience against a single decoder hiccup or paused-source pulse.
    /// Default 3.
    /// </summary>
    public int OutlierConsecutiveSamples { get; init; } = 3;

    /// <summary>
    /// Gain applied to the measured drift when computing a correction step.
    /// Default 0.20 (one-fifth convergence) — keeps individual nudges small
    /// enough that a sink (e.g. NDI receiver) does not perceive an audible
    /// step. Tune up for faster lock-in if your sink tolerates larger nudges.
    /// </summary>
    public double CorrectionGain { get; init; } = 0.20;

    /// <summary>
    /// Maximum magnitude of one correction step (ms). Default 8 ms — slightly
    /// under the audio frame size at 48 kHz/240-sample windows so a single
    /// step never exceeds one buffer of audio.
    /// </summary>
    public double MaxStepMs { get; init; } = 8;

    /// <summary>
    /// Absolute cap on accumulated offset (ms). Default 250 ms.
    /// </summary>
    public double MaxAbsOffsetMs { get; init; } = 250;
}
