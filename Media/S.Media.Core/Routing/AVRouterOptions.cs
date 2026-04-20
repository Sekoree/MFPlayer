namespace S.Media.Core.Routing;

/// <summary>
/// Configuration options for an <see cref="IAVRouter"/> instance.
/// </summary>
public record AVRouterOptions
{
    /// <summary>
    /// Default frames-per-buffer hint applied to all endpoints that don't
    /// specify their own. 0 = let each endpoint decide. Default: 0.
    /// </summary>
    public int DefaultFramesPerBuffer { get; init; } = 0;

    /// <summary>
    /// Internal clock tick cadence when no override clock is set.
    /// Controls push-endpoint delivery rate and channel drain rate.
    /// Default: 10 ms (~100 Hz).
    /// </summary>
    public TimeSpan InternalTickCadence { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Default <see cref="ClockPriority"/> assigned to clocks auto-discovered from
    /// <see cref="S.Media.Core.Media.Endpoints.IClockCapableEndpoint"/> endpoints.
    /// Default: <see cref="ClockPriority.Hardware"/>.
    /// </summary>
    public ClockPriority DefaultEndpointClockPriority { get; init; } = ClockPriority.Hardware;

    // ── Video sync tuning ───────────────────────────────────────────────

    /// <summary>
    /// How far ahead of the clock a video frame's PTS may be before it is
    /// held back for the next tick. Default: 5 ms.
    /// Smaller values = tighter PTS gating but more "too-early" retries.
    /// </summary>
    public TimeSpan VideoPtsEarlyTolerance { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Maximum number of frames the push video tick may skip in a single
    /// cycle when video is behind the clock. Prevents stalling the push
    /// thread when the decoder falls far behind. Default: 4.
    /// Set to 0 to disable catch-up frame skipping.
    /// </summary>
    public int VideoMaxCatchUpFramesPerTick { get; init; } = 4;

    /// <summary>
    /// Drift correction gain for the pull video endpoint's PTS origin
    /// adjustment. Higher values correct drift more aggressively.
    /// Range: 0.0 (disabled) – 1.0 (instant snap).
    /// Default: 0.02 (2 % per frame — corrects a 33 ms drift in ~50 frames).
    /// </summary>
    public double VideoPullDriftCorrectionGain { get; init; } = 0.02;

    /// <summary>
    /// Drift correction gain for the push video path's PTS origin adjustment.
    /// Applied every time a frame is presented, shifting the PTS↔clock origin
    /// by <c>gain × error</c> so sub-frame drift converges smoothly to zero
    /// instead of oscillating ±½ frame.
    /// Range: 0.0 (disabled) – 1.0 (instant snap).
    /// Default: 0.03 (3 % per frame — corrects an 8 ms drift in ~40 frames at 60 fps).
    /// </summary>
    public double VideoPushDriftCorrectionGain { get; init; } = 0.03;
}

