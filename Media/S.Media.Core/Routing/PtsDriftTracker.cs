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
/// The struct is intentionally mutable; callers either hold it as a field
/// (pull callback — single thread) or store a reference in a per-input
/// dictionary (push tick — single push thread). No internal synchronization.
/// </para>
/// </summary>
internal sealed class PtsDriftTracker
{
    public bool HasOrigin;
    public long PtsOriginTicks;
    public long ClockOriginTicks;

    /// <summary>
    /// Seeds <see cref="PtsOriginTicks"/> / <see cref="ClockOriginTicks"/> from the first
    /// observed frame. No-op once <see cref="HasOrigin"/> is true.
    /// </summary>
    public void SeedIfNeeded(long ptsTicks, long clockTicks)
    {
        if (HasOrigin) return;
        PtsOriginTicks   = ptsTicks;
        ClockOriginTicks = clockTicks;
        HasOrigin        = true;
    }

    /// <summary>PTS of <paramref name="ptsTicks"/> expressed relative to the seeded origin, plus a per-input time offset.</summary>
    public long RelativePts(long ptsTicks, long timeOffsetTicks) =>
        ptsTicks - PtsOriginTicks + timeOffsetTicks;

    /// <summary>Master-clock position expressed relative to the seeded origin.</summary>
    public long RelativeClock(long clockTicks) =>
        clockTicks - ClockOriginTicks;

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
        if      (errorTicks >  deadBandTicks) PtsOriginTicks += (long)((errorTicks - deadBandTicks) * gain);
        else if (errorTicks < -deadBandTicks) PtsOriginTicks += (long)((errorTicks + deadBandTicks) * gain);
        // else: within dead-band, do nothing.
    }

    public void Reset()
    {
        HasOrigin        = false;
        PtsOriginTicks   = 0;
        ClockOriginTicks = 0;
    }
}

