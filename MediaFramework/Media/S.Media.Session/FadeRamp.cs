namespace S.Media.Session;

/// <summary>Gain-shaping law applied over a fade's linear progress. <see cref="Linear"/> is the historical
/// behavior and the default everywhere, so documents/callers that never pick a curve are unchanged.</summary>
public enum FadeCurve
{
    /// <summary>Straight gain ramp (the pre-curve behavior).</summary>
    Linear,

    /// <summary>Quarter-wave sine (sin(p·π/2)): complementary up/down fades sum to constant power.</summary>
    EqualPower,

    /// <summary>Cube law (p³) - the standard polynomial stand-in for a dB-linear (exponential) taper.
    /// Reaches exactly 0 (a real exponential never does) at the cost of accuracy: against a true −60 dB
    /// dB-linear ramp it reads up to ~12 dB hot around the midpoint (−18 dB vs −30 dB at p=0.5), converging
    /// again toward both ends - the classic audio-fader compromise, not a precise dB-linear law.</summary>
    Exponential,

    /// <summary>Smoothstep (p²(3−2p)): eases both ends of the ramp.</summary>
    SCurve,
}

/// <summary>
/// The curve-aware fade level functions: a fade's linear progress (elapsed/duration, clamped to [0,1]) is
/// shaped by a <see cref="FadeCurve"/> into the gain to apply. Public - hosts computing their own ramps
/// (and tests asserting curve shape) share the session's exact math. A non-positive duration is a hard
/// cut: down = 0, up = 1 immediately (never NaN from 0/0).
/// </summary>
public static class FadeCurves
{
    /// <summary>The down-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>: 1 → 0,
    /// clamped, shaped by <paramref name="curve"/>.</summary>
    public static float LevelDown(TimeSpan elapsed, TimeSpan duration, FadeCurve curve = FadeCurve.Linear) =>
        duration <= TimeSpan.Zero
            ? 0f
            : Shape(Math.Clamp(1d - elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d), curve);

    /// <summary>The up-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>: 0 → 1,
    /// clamped (exactly 1 once past the duration), shaped by <paramref name="curve"/>.</summary>
    public static float LevelUp(TimeSpan elapsed, TimeSpan duration, FadeCurve curve = FadeCurve.Linear) =>
        duration <= TimeSpan.Zero || elapsed >= duration
            ? 1f
            : Shape(Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d), curve);

    /// <summary>Interpolates between two arbitrary levels: a downward move rides the down ramp's curve
    /// shape, an upward move the up ramp's - so a segment toward 0 matches the stop fade exactly and a
    /// rise eases the same way a fade-in does. Endpoints are exact (the curves hit 0/1). Shared by the
    /// fade cue's ramp and the volume-envelope sampler so both interpolate identically.</summary>
    public static float LevelBetween(
        float start, float target, TimeSpan elapsed, TimeSpan duration, FadeCurve curve) =>
        target < start
            ? target + (start - target) * LevelDown(elapsed, duration, curve)
            : start + (target - start) * LevelUp(elapsed, duration, curve);

    /// <summary>Shapes linear progress <paramref name="p"/> ∈ [0,1] into a gain. Every curve maps 0 → 0 and
    /// 1 → 1 and is monotonic, so ramp endpoints (and the "target reached" checks against 0/1) hold for all.</summary>
    private static float Shape(double p, FadeCurve curve) => (float)(curve switch
    {
        FadeCurve.EqualPower => Math.Sin(p * Math.PI / 2d),
        FadeCurve.Exponential => p * p * p,
        FadeCurve.SCurve => p * p * (3d - 2d * p),
        _ => p,
    });
}

/// <summary>
/// Piecewise evaluation of a clip's volume envelope (<see cref="ShowClipBinding.VolumeEnvelope"/>): the
/// envelope factor at a CLIP position (post-StartOffset - it survives seeks and restarts per loop pass).
/// Points must be sorted by time (the mapper emits them sorted); levels are linear gains (dB conversion
/// happens at the GUI/mapper boundary, ≤ −60 dB = exact 0).
/// </summary>
public static class VolumeEnvelopes
{
    /// <summary>Ceiling for an envelope factor: +12 dB as linear gain (the authoring clamp's maximum).</summary>
    public static readonly float MaxLevel = (float)Math.Pow(10, 12 / 20d);

    /// <summary>The envelope factor at <paramref name="position"/>: 1 for an empty envelope, the first
    /// point's level before the first point, the last point's level after the last, and in between the
    /// segment's <see cref="ShowEnvelopePoint.CurveToNext"/>-shaped interpolation (binary search).</summary>
    public static float Sample(IReadOnlyList<ShowEnvelopePoint>? points, TimeSpan position)
    {
        if (points is not { Count: > 0 })
            return 1f;
        if (position <= points[0].Time)
            return points[0].Level;
        if (position >= points[^1].Time)
            return points[^1].Level;

        // Greatest i with points[i].Time <= position (invariant: points[lo].Time <= position < points[hi].Time).
        var lo = 0;
        var hi = points.Count - 1;
        while (lo + 1 < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Time <= position)
                lo = mid;
            else
                hi = mid;
        }

        var from = points[lo];
        var to = points[hi];
        return FadeCurves.LevelBetween(
            from.Level, to.Level, position - from.Time, to.Time - from.Time, from.CurveToNext);
    }
}

/// <summary>
/// The one fade-ramp loop every session fade shares - clip fade-in, natural fade-out, stop fade, and voice
/// fade-out differ only in what a step applies (gain/opacity via their own dispatcher-marshaled closure), when
/// they are done, and what runs afterwards; the loop mechanics live here once. A step receives the ramp's
/// elapsed time and computes its own level from its own duration (the stop fade ramps several groups with
/// different durations off one clock), applies it, and returns true to end the ramp (target reached, or its
/// guard found the clip replaced/stopped). The first step runs immediately (a fade must take effect on the
/// tick it starts, not one interval later).
/// </summary>
internal static class FadeRamp
{
    /// <summary>The step rate every session fade ramps at - fine enough to be click-free, coarse enough that
    /// the short marshaled steps stay negligible dispatcher load.</summary>
    public static readonly TimeSpan DefaultStepInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Runs the ramp inline: step (immediately, then every <paramref name="stepInterval"/>) until the
    /// step reports done or <paramref name="ct"/> fires. The step closure is expected to marshal itself onto
    /// the session dispatcher (<c>InvokeAsync</c>) - the loop itself stays off it, so a long fade never parks
    /// the serial loop (NXT-18). Exceptions propagate to the caller (the awaited stop fade ODE-guards itself).</summary>
    public static async Task RunAsync(TimeSpan stepInterval, CancellationToken ct, Func<TimeSpan, Task<bool>> step)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            if (await step(sw.Elapsed).ConfigureAwait(false))
                return;
            await Task.Delay(stepInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fire-and-forget ramp: <see cref="RunAsync"/> on a worker, then - when the ramp ended on its own
    /// rather than by cancellation - <paramref name="onCompleted"/> (the fade's release/commit tail, expected to
    /// marshal itself like the step). Suppresses <c>ExecutionContext</c> flow so the dispatcher's
    /// <c>AsyncLocal</c> identity cannot leak into the worker (a leaked identity would make the step's
    /// <c>InvokeAsync</c> run inline off the real loop and race transport commands - NXT-22). Cancellation and
    /// step/completion failures are swallowed: a fade hiccup must never crash the session.</summary>
    public static void Start(
        TimeSpan stepInterval,
        CancellationToken ct,
        Func<TimeSpan, Task<bool>> step,
        Func<Task>? onCompleted = null)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunAsync(stepInterval, ct, step).ConfigureAwait(false);
                        if (onCompleted is not null && !ct.IsCancellationRequested)
                            await onCompleted().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* best-effort - a fade hiccup must never crash the session */ }
                },
                ct);
        }
    }

    /// <summary>The down-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>:
    /// 1 → 0, clamped, optionally shaped (<see cref="FadeCurves.LevelDown"/>; default stays linear).</summary>
    public static float LevelDown(TimeSpan elapsed, TimeSpan duration, FadeCurve curve = FadeCurve.Linear) =>
        FadeCurves.LevelDown(elapsed, duration, curve);

    /// <summary>The up-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>:
    /// 0 → 1, clamped, optionally shaped (<see cref="FadeCurves.LevelUp"/>; default stays linear).</summary>
    public static float LevelUp(TimeSpan elapsed, TimeSpan duration, FadeCurve curve = FadeCurve.Linear) =>
        FadeCurves.LevelUp(elapsed, duration, curve);
}
