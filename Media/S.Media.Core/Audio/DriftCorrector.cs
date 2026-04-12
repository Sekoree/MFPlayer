using Microsoft.Extensions.Logging;

namespace S.Media.Core.Audio;

/// <summary>
/// PI-controller–based drift corrector for audio sinks.
/// Monitors a pending-buffer queue depth and computes a per-buffer frame-count
/// correction that keeps the queue stable around a target midpoint.
///
/// <para>
/// When a sink's hardware clock runs slightly faster or slower than the leader's
/// clock, the pending queue gradually drains or grows. This corrector adjusts
/// the output frame count by ±1 frame occasionally (using fractional accumulation)
/// to compensate, keeping the queue depth near the target.
/// </para>
/// </summary>
/// <remarks>
/// Typical corrections are on the order of ±0.01–0.1 %, completely inaudible.
/// The corrected frame count should be used to size the resampler's output buffer
/// (cross-rate) or directly adjust the copy length (same-rate).
/// <para>
/// All public methods are safe to call from the RT thread — no allocations, no locks.
/// </para>
/// </remarks>
public sealed class DriftCorrector
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(DriftCorrector));

    private readonly int    _targetDepth;
    private readonly double _kp;
    private readonly double _ki;
    private readonly double _maxCorrection;
    private readonly string _ownerName;

    private double _integral;
    private double _fractionalCarry;
    private double _ratio = 1.0;
    private long   _calls;

    /// <summary>
    /// Creates a drift corrector.
    /// </summary>
    /// <param name="targetDepth">
    /// Target pending-queue depth (e.g. half the pool size).
    /// The controller steers the queue toward this value.
    /// </param>
    /// <param name="ownerName">
    /// Display name of the owning sink (used in log messages).
    /// </param>
    /// <param name="kp">
    /// Proportional gain. Controls how aggressively the controller responds to
    /// instantaneous queue deviations. Default 2 × 10⁻³.
    /// </param>
    /// <param name="ki">
    /// Integral gain. Eliminates residual steady-state error over time. Default 1 × 10⁻⁵.
    /// </param>
    /// <param name="maxCorrection">
    /// Maximum ratio deviation from 1.0 (e.g. 0.005 = ±0.5 %). Default 0.005.
    /// </param>
    public DriftCorrector(
        int     targetDepth,
        string? ownerName     = null,
        double  kp            = 2e-3,
        double  ki            = 1e-5,
        double  maxCorrection = 0.005)
    {
        _targetDepth   = Math.Max(0, targetDepth);
        _ownerName     = ownerName ?? "unknown";
        _kp            = kp;
        _ki            = ki;
        _maxCorrection = maxCorrection;

        Log.LogDebug("DriftCorrector created for '{Owner}': target={Target}, Kp={Kp}, Ki={Ki}, max={Max}",
            _ownerName, _targetDepth, _kp, _ki, _maxCorrection);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────

    /// <summary>Current correction ratio (1.0 = no correction).</summary>
    public double CorrectionRatio => Volatile.Read(ref _ratio);

    /// <summary>Total number of <see cref="CorrectFrameCount"/> calls.</summary>
    public long TotalCalls => Volatile.Read(ref _calls);

    /// <summary>Target queue depth this controller steers toward.</summary>
    public int TargetDepth => _targetDepth;

    // ── Hot-path — called once per ReceiveBuffer ─────────────────────────

    /// <summary>
    /// Computes the drift-corrected output frame count for this buffer.
    /// Call exactly once per <see cref="IAudioSink.ReceiveBuffer"/> invocation.
    /// </summary>
    /// <param name="nominalFrames">
    /// The uncorrected output frame count (from the nominal rate ratio).
    /// </param>
    /// <param name="currentQueueDepth">
    /// Current pending-buffer queue depth.
    /// </param>
    /// <returns>
    /// The adjusted frame count. Typically equals <paramref name="nominalFrames"/>;
    /// differs by ±1 occasionally to compensate for clock drift.
    /// </returns>
    public int CorrectFrameCount(int nominalFrames, int currentQueueDepth)
    {
        // Error: positive when queue is below target (need more frames),
        //        negative when above target (need fewer frames).
        double error = _targetDepth - currentQueueDepth;

        // PI controller.
        _integral += error;

        // Anti-windup: clamp integral to prevent overshoot after prolonged saturation.
        double maxIntegral = _ki > 0 ? _maxCorrection / _ki : 1e12;
        _integral = Math.Clamp(_integral, -maxIntegral, maxIntegral);

        double correction = 1.0 + _kp * error + _ki * _integral;
        correction = Math.Clamp(correction, 1.0 - _maxCorrection, 1.0 + _maxCorrection);
        _ratio = correction;

        // Fractional accumulation: distributes the ±1 frame adjustments evenly over time
        // so that the long-term average frame rate exactly matches the corrected ratio.
        double desired = nominalFrames * correction + _fractionalCarry;
        int actual = (int)Math.Round(desired);
        if (actual < 1) actual = 1;
        _fractionalCarry = desired - actual;

        Interlocked.Increment(ref _calls);

        return actual;
    }

    // ── Control ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resets the controller state. Call after seek, stream restart, or
    /// any event that invalidates the pending-queue baseline.
    /// </summary>
    public void Reset()
    {
        _integral       = 0;
        _fractionalCarry = 0;
        _ratio          = 1.0;
        Log.LogDebug("DriftCorrector reset for '{Owner}'", _ownerName);
    }
}

