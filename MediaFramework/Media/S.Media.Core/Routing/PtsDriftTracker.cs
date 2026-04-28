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
/// <c>RouteEntry</c> (push tick — single push thread). The hot-path
/// accessors (<see cref="RelativePts"/>, <see cref="RelativeClock"/>,
/// <see cref="IntegrateError"/>) are lock-free; the only seed/reset path
/// that needs the (HasOrigin, Pts, Clock) triple to update atomically uses
/// a <see cref="Lock"/>, which is uncontended in steady state.
/// </para>
/// <para>
/// §heavy-media-fixes phase 7 — the previous design held <see cref="_seedResetLock"/>
/// on every read/write, including the per-frame <see cref="IntegrateError"/>
/// and <see cref="RelativePts"/> calls on the push tick. Those reads now
/// use <see cref="Volatile.Read{T}(ref T)"/> on the long fields and an
/// <see cref="Interlocked.Add(ref long, long)"/> for the integrator's
/// read-modify-write, so the realtime path no longer enters the lock.
/// </para>
/// </summary>
internal sealed class PtsDriftTracker
{
    // Used only for the SeedIfNeeded / Reset triple update — the rare paths
    // that need (HasOrigin, Pts, Clock) to flip atomically. Hot-path readers
    // never take the lock.
    private readonly Lock _seedResetLock = new();

    private volatile bool _hasOrigin;
    private long _ptsOriginTicks;
    private long _clockOriginTicks;

    /// <summary>
    /// True once <see cref="SeedIfNeeded"/> has captured the first frame's
    /// PTS / master-clock pair. Reads are lock-free.
    /// </summary>
    public bool HasOrigin => _hasOrigin;

    /// <summary>Stream-time PTS of the seed frame.</summary>
    public long PtsOriginTicks => Volatile.Read(ref _ptsOriginTicks);

    /// <summary>Master-clock position at the seed frame.</summary>
    public long ClockOriginTicks => Volatile.Read(ref _clockOriginTicks);

    /// <summary>
    /// Seeds <see cref="PtsOriginTicks"/> / <see cref="ClockOriginTicks"/> from the first
    /// observed frame. No-op once <see cref="HasOrigin"/> is true.
    /// </summary>
    public void SeedIfNeeded(long ptsTicks, long clockTicks)
    {
        // Optimistic fast-path: most calls hit this once HasOrigin is set.
        if (_hasOrigin) return;
        lock (_seedResetLock)
        {
            if (_hasOrigin) return;
            // Order matters: write ptsOriginTicks / clockOriginTicks BEFORE
            // _hasOrigin so a concurrent reader that observes _hasOrigin=true
            // will already see the corresponding origin values. The
            // `volatile bool` provides the release barrier for the long
            // writes that precede it on the same thread.
            Volatile.Write(ref _ptsOriginTicks, ptsTicks);
            Volatile.Write(ref _clockOriginTicks, clockTicks);
            _hasOrigin = true;
        }
    }

    /// <summary>PTS of <paramref name="ptsTicks"/> expressed relative to the seeded origin, plus a per-input time offset.</summary>
    public long RelativePts(long ptsTicks, long timeOffsetTicks)
        => ptsTicks - Volatile.Read(ref _ptsOriginTicks) + timeOffsetTicks;

    /// <summary>Master-clock position expressed relative to the seeded origin.</summary>
    public long RelativeClock(long clockTicks)
        => clockTicks - Volatile.Read(ref _clockOriginTicks);

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
        // Atomic read-modify-write so a concurrent Reset can't race the
        // integrator into writing a non-zero delta over a freshly cleared
        // origin.
        Interlocked.Add(ref _ptsOriginTicks, delta);
    }

    public void Reset()
    {
        lock (_seedResetLock)
        {
            // Clear _hasOrigin first so any reader that races the reset will
            // either see the old (consistent) state or the cleared state, but
            // never a mid-update (HasOrigin=true, Pts=0) snapshot.
            _hasOrigin = false;
            Volatile.Write(ref _ptsOriginTicks, 0);
            Volatile.Write(ref _clockOriginTicks, 0);
        }
    }

    public PtsDriftTrackerSnapshot Snapshot()
    {
        // Snapshot is only used for diagnostics / events; cost of the lock
        // is fine here and gives us the same triple-atomicity that the old
        // implementation provided.
        lock (_seedResetLock)
        {
            bool hasOrigin = _hasOrigin;
            long pts = _ptsOriginTicks;
            long clk = _clockOriginTicks;
            return new PtsDriftTrackerSnapshot(
                hasOrigin,
                TimeSpan.FromTicks(pts),
                TimeSpan.FromTicks(clk),
                TimeSpan.FromTicks(pts - clk));
        }
    }
}
