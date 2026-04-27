namespace S.Media.Core.Routing;

/// <summary>
/// Shared PTS↔clock drift-correction state machine used by both the push-video
/// fan-out (<see cref="AVRouter.PushVideoTick"/>) and the pull-video render
/// callback (<c>AVRouter.VideoPresentCallbackForEndpoint</c>).
///
/// <para>
/// Semantics:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Origin seeding</b> — the first frame captured establishes the offset between
///   stream PTS and master clock; all subsequent frames are measured relative to this origin.
///   </description></item>
///   <item><description>
///   <b>Dead-band integrator</b> — proportional correction with a dead-band equal to
///   <c>tolerance/2</c> silences the ±½-frame quantization bias that would otherwise
///   ramp the origin forward every tick and cause limit-cycle oscillation.
///   </description></item>
///   <item><description>
///   <b>Skip-on-catchup</b> — callers should skip <see cref="IntegrateError"/> on ticks
///   where a catch-up / frame-skip fired, because the error at that point reflects
///   decoder lag rather than a real PTS↔clock offset (see §2.4 of Code-Review-Findings).
///   </description></item>
/// </list>
///
/// <para>
/// The class is intentionally mutable; callers either hold it as a field
/// (pull callback — single thread) or as a per-route field on
/// <c>RouteEntry</c> (push tick — single push thread).  Reads/writes from
/// the owning thread need no locking.
/// </para>
/// <para>
/// <b>Cross-thread <see cref="Reset"/></b> — <see cref="AVRouter.SetClock"/> /
/// <see cref="AVRouter.RegisterClock"/> /
/// <see cref="AVRouter.UnregisterClock"/> reset every push tracker from the
/// caller thread while the push thread is reading the same fields.
/// <see cref="Reset"/> writes the three fields under <see cref="_resetLock"/>
/// and the read-side accessors take the same lock around their multi-field
/// reads, so the push thread can never observe a torn snapshot
/// (<c>HasOrigin == true</c> with a half-cleared origin pair, or vice versa).
/// Hold time is sub-microsecond and the lock is uncontended in steady state.
/// </para>
/// </summary>
internal sealed class PtsDriftTracker
{
    // §reset-race fix: paired with reads in SeedIfNeeded / RelativePts /
    // RelativeClock / IntegrateError / Snapshot so a concurrent Reset cannot
    // tear the (HasOrigin, Pts, Clock) triple. Field is a `Lock` (System.Threading)
    // for sub-microsecond uncontended fast paths on .NET 9+.
    private readonly Lock _resetLock = new();

    public bool HasOrigin;
    public long PtsOriginTicks;
    public long ClockOriginTicks;

    /// <summary>
    /// Seeds <see cref="PtsOriginTicks"/> / <see cref="ClockOriginTicks"/> from the first
    /// observed frame. No-op once <see cref="HasOrigin"/> is true.
    /// </summary>
    public void SeedIfNeeded(long ptsTicks, long clockTicks)
    {
        lock (_resetLock)
        {
            if (HasOrigin) return;
            PtsOriginTicks   = ptsTicks;
            ClockOriginTicks = clockTicks;
            HasOrigin        = true;
        }
    }

    /// <summary>PTS of <paramref name="ptsTicks"/> expressed relative to the seeded origin, plus a per-input time offset.</summary>
    public long RelativePts(long ptsTicks, long timeOffsetTicks)
    {
        lock (_resetLock)
            return ptsTicks - PtsOriginTicks + timeOffsetTicks;
    }

    /// <summary>Master-clock position expressed relative to the seeded origin.</summary>
    public long RelativeClock(long clockTicks)
    {
        lock (_resetLock)
            return clockTicks - ClockOriginTicks;
    }

    /// <summary>
    /// Integrates the PTS↔clock error into <see cref="PtsOriginTicks"/> with a
    /// dead-band of <paramref name="toleranceTicks"/>/2. Callers should pass the
    /// already-computed relative values and gate this call on the catch-up flag.
    /// </summary>
    public void IntegrateError(long relativePtsTicks, long relativeClockTicks, long toleranceTicks, double gain)
    {
        if (gain <= 0) return;
        long errorTicks    = relativePtsTicks - relativeClockTicks;
        long deadBandTicks = Math.Max(1, toleranceTicks / 2);
        long delta = 0;
        if      (errorTicks >  deadBandTicks) delta = (long)((errorTicks - deadBandTicks) * gain);
        else if (errorTicks < -deadBandTicks) delta = (long)((errorTicks + deadBandTicks) * gain);
        if (delta == 0) return;
        lock (_resetLock)
            PtsOriginTicks += delta;
    }

    public void Reset()
    {
        lock (_resetLock)
        {
            HasOrigin        = false;
            PtsOriginTicks   = 0;
            ClockOriginTicks = 0;
        }
    }

    public PtsDriftTrackerSnapshot Snapshot()
    {
        bool hasOrigin;
        long pts;
        long clk;
        lock (_resetLock)
        {
            hasOrigin = HasOrigin;
            pts       = PtsOriginTicks;
            clk       = ClockOriginTicks;
        }
        return new PtsDriftTrackerSnapshot(
            hasOrigin,
            TimeSpan.FromTicks(pts),
            TimeSpan.FromTicks(clk),
            TimeSpan.FromTicks(pts - clk));
    }
}
