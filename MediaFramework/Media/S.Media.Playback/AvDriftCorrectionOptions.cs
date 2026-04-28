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
    /// Maximum magnitude of one correction step (ms). Default 25 ms.
    /// <para>
    /// §heavy-media-fixes phase 6 — was 8 ms, raised to 25 ms so a real
    /// multi-hundred-millisecond drift (e.g. heavy 4K60 below realtime
    /// playback that has accumulated lag) can actually converge in a
    /// reasonable number of loop iterations. 25 ms is still well under
    /// any audio frame size large enough to be perceptible as a glitch —
    /// the previous 8 ms default was under one 48 kHz audio buffer but
    /// effectively meant the corrector could not catch a 250 ms drift in
    /// less than ten loop intervals.
    /// </para>
    /// </summary>
    public double MaxStepMs { get; init; } = 25;

    /// <summary>
    /// Absolute cap on accumulated offset (ms). Default 2000 ms.
    /// <para>
    /// §heavy-media-fixes phase 6 — was 250 ms, raised to 2000 ms so the
    /// loop can absorb the larger drifts that surface on heavy media when
    /// the decoder spends extended periods below realtime. Independent of
    /// step-size: this is the running total clamp, not the per-step clamp.
    /// </para>
    /// </summary>
    public double MaxAbsOffsetMs { get; init; } = 2000;
}
