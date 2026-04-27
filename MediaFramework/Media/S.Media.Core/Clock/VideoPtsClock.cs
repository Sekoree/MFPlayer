using System.Diagnostics;

namespace S.Media.Core.Clock;

/// <summary>
/// Video clock driven by presented frame PTS values.
/// Uses <see cref="Stopwatch"/> interpolation between frames
/// (same pattern as <c>NDIClock.UpdateFromFrame</c>).
/// <para>
/// <b>Drift handling.</b> Two operating modes:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <i>Default (router-corrected)</i> — small forward/backward drift below
///     <see cref="SeekThreshold"/> is ignored; only large jumps are treated as
///     seeks. Use this when the AVRouter applies its own cross-origin
///     correction (<c>PtsDriftTracker</c>); chasing raw PTS here would form a
///     positive feedback loop with that corrector.
///   </description></item>
///   <item><description>
///     <i><see cref="ApplySelfSlew"/> = true</i> — opt-in slew toward the
///     incoming PTS at <see cref="SelfSlewMaxMsPerSec"/> (default 0.5 ms/s) so
///     the clock cannot diverge unbounded when used as the router master with
///     no upstream corrector. See
///     <c>Doc/Clock-And-AV-Drift-Analysis.md</c> §5.1 / item L.
///   </description></item>
/// </list>
/// </summary>
public sealed class VideoPtsClock : MediaClockBase
{
    // §3.29 / C3: _lastPts and _swAtLastPts are paired state read by Position on
    // consumer threads and written by UpdateFromFrame on the render thread.
    // A short lock is cheaper than paired Interlocked on 64-bit fields and
    // keeps Position tear-free across the pair. Hold time is sub-microsecond.
    private readonly Lock      _stateLock = new();
    private readonly Stopwatch _sw = new();
    private TimeSpan _lastPts;
    private TimeSpan _swAtLastPts;
    private volatile bool _running;
    private bool          _initialised;

    /// <summary>
    /// Forward/backward delta beyond which an <see cref="UpdateFromFrame"/>
    /// call is treated as a seek (re-anchor the clock immediately).
    /// </summary>
    public static TimeSpan SeekThreshold { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// When <see langword="true"/>, sub-<see cref="SeekThreshold"/> drift is
    /// gradually corrected at <see cref="SelfSlewMaxMsPerSec"/>. Default
    /// <see langword="false"/> — leave off when an upstream
    /// <c>PtsDriftTracker</c> already corrects this clock's PTS-vs-router gap.
    /// Toggle on when this clock is the *router master* with no other
    /// corrector in the loop.
    /// </summary>
    public bool ApplySelfSlew { get; init; }

    /// <summary>
    /// Maximum slew rate in milliseconds-of-PTS-correction per real second when
    /// <see cref="ApplySelfSlew"/> is true. 0.5 ms/s is well below the
    /// audibility threshold for video playback rate variations and clears the
    /// largest plausible accumulated drift (~250 ms) in ~8 minutes of playback.
    /// </summary>
    public double SelfSlewMaxMsPerSec { get; init; } = 0.5;

    /// <inheritdoc/>
    public override TimeSpan Position
    {
        get
        {
            lock (_stateLock)
            {
                if (!_running) return _lastPts;
                if (!_initialised)
                {
                    // Monotonic stand-in until the first presented frame calls
                    // <see cref="UpdateFromFrame"/>. Returning zero made this clock
                    // unusable as a router master for push (NDI) before the first pull
                    // present, because all subsequent frames looked "too early" vs
                    // a stuck clock at 0.
                    return _sw.Elapsed;
                }
                return _lastPts + (_sw.Elapsed - _swAtLastPts);
            }
        }
    }

    /// <summary>Nominal frame rate (e.g. 30, 60). Exposed as a concrete property.</summary>
    public double FrameRate { get; }

    /// <inheritdoc/>
    public override bool IsRunning => _running;

    /// <param name="frameRate">Nominal frame rate exposed to consumers (e.g. 30, 60).</param>
    /// <param name="tickIntervalMs">How often the base Tick event fires (default 16 ms ≈ 60 Hz).</param>
    public VideoPtsClock(double frameRate = 30, double tickIntervalMs = 16)
        : base(TimeSpan.FromMilliseconds(tickIntervalMs))
    {
        FrameRate = frameRate;
    }

    /// <inheritdoc/>
    public override void Start()
    {
        if (_running) return;
        lock (_stateLock) _sw.Start();
        _running = true;
        base.Start();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        if (!_running) return;
        _running = false;
        lock (_stateLock) _sw.Stop();
        base.Stop();
    }

    /// <inheritdoc/>
    public override void Reset()
    {
        lock (_stateLock)
        {
            _lastPts     = TimeSpan.Zero;
            _swAtLastPts = TimeSpan.Zero;
            _initialised = false;
            _sw.Reset();
        }
    }

    /// <summary>
    /// Called by the render loop after each presented frame.
    /// Records the frame PTS and resets the interpolation stopwatch.
    /// </summary>
    /// <param name="pts">The PTS of the frame that was just presented.</param>
    public void UpdateFromFrame(TimeSpan pts)
    {
        if (pts < TimeSpan.Zero) return;

        lock (_stateLock)
        {
            var swNow = _sw.Elapsed;

            // Accept PTS=0 as a valid initial anchor so the clock is properly
            // synchronised from the very first presented frame. Without this, the
            // clock runs freely from Start() and races ahead of the actual frame
            // timeline, causing the mixer to drop all frames as stale.
            if (!_initialised)
            {
                _initialised = true;
                _lastPts = pts;
                _swAtLastPts = swNow;
                return;
            }

            var predicted = _lastPts + (swNow - _swAtLastPts);
            var delta = pts - predicted;
            var absDelta = delta < TimeSpan.Zero ? -delta : delta;

            // Re-anchor immediately on a LARGE jump (seek / stream discontinuity).
            // For sub-threshold drift the behaviour depends on ApplySelfSlew:
            //   - false (default): ignore — the AVRouter's PtsDriftTracker corrects
            //     this clock's PTS-vs-router gap upstream, and chasing raw PTS here
            //     would form a positive feedback loop.
            //   - true: apply a bounded slew toward `pts` so the clock cannot
            //     diverge unbounded when used as the router master with no other
            //     corrector (Doc/Clock-And-AV-Drift-Analysis.md §5.1 / item L).
            if (absDelta >= SeekThreshold)
            {
                _lastPts = pts;
                _swAtLastPts = swNow;
                return;
            }

            if (!ApplySelfSlew)
                return;

            // Slew: cap |adjustment| ≤ SelfSlewMaxMsPerSec × secondsSinceLastUpdate.
            // wallDelta is the wall-time elapsed since the previous successful
            // anchor (NOT since the previous UpdateFromFrame call). Using the
            // anchor's wall snapshot keeps the slew rate deterministic across
            // dropped/duplicate frame submissions.
            double wallSecs = (swNow - _swAtLastPts).TotalSeconds;
            if (wallSecs <= 0)
                return;

            double maxAdjustMs = SelfSlewMaxMsPerSec * wallSecs;
            double deltaMs = delta.TotalMilliseconds;
            double clampedAdjustMs = deltaMs > 0
                ? Math.Min(deltaMs,  maxAdjustMs)
                : Math.Max(deltaMs, -maxAdjustMs);

            // Re-anchor to predicted + clampedAdjust. Equivalent to advancing the
            // anchor pair by (predicted + clampedAdjust, swNow), which preserves
            // monotonic wall-time interpolation.
            _lastPts = predicted + TimeSpan.FromMilliseconds(clampedAdjustMs);
            _swAtLastPts = swNow;
        }
    }
}
