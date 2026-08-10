namespace S.Media.Core.Video;

/// <summary>What a presenter should do with the next queued frame at the upcoming vblank.</summary>
public enum PresentPacingDecision
{
    /// <summary>Promote the queued frame; the upcoming vblank is the one nearest its ideal display time.</summary>
    ShowNext,

    /// <summary>Re-present the current frame; the queued frame's ideal display time is nearer a later vblank.</summary>
    HoldCurrent,
}

/// <summary>
/// Decides, at each vblank of a swap-paced presenter, whether the next queued frame should be shown
/// now or held one more refresh. Pure in the sense that it never reads a clock or touches a device:
/// the caller feeds it observed swap intervals and monotonic timestamps, so the whole cadence logic
/// is testable by simulation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A latest-wins presenter shows whatever is queued at every vblank, so the
/// content cadence is decided purely by the arrival phase of submits. When the content rate divides
/// the refresh rate unevenly - 60 fps on a 165 Hz panel is 2.75 vblanks per frame - the correct
/// steady state is the Bresenham pattern of that ratio (3-3-3-2 repeating), but arrival phase sits
/// within a millisecond of a vblank for some beats, and ±1 ms of producer jitter flips a 3-3 pair
/// into 4-2, or worse into a drop immediately followed by a repeat. That reads as visible stutter in
/// content that is actually arriving perfectly.
/// </para>
/// <para>
/// <b>The rule.</b> Each frame should be shown at the vblank whose predicted display time is closest
/// to the frame's ideal display time. Ideal times live on the display's monotonic clock via a phase
/// offset (<c>ideal = pts + offset</c>); the predicted display time of a frame promoted right after a
/// swap returned at <c>t</c> is <c>t + R</c> (the next vblank), so with <c>delta = ideal - (t + R)</c>
/// the nearest-vblank rule is exactly <c>delta &lt;= R/2</c> → SHOW, else HOLD. Summed over frames,
/// consecutive show-vblank indices are <c>floor((pts + offset - threshold) / R)</c> differences - the
/// Bresenham sequence of the ratio - so a stable offset provably yields 3-3-3-2 for 60-on-165 and
/// 3-2-3-2 for 24-on-60, with no drops and no adjacent drop/repeat pairs.
/// </para>
/// <para>
/// <b>Why jitter cannot flip a beat.</b> The decision consumes only the frame PTS (clean, authored
/// spacing), the internal offset, and the vblank grid. Producer arrival time is deliberately NOT an
/// input: jitter can only affect whether a frame is queued in time at all, and the caller's one-frame
/// hold slot gives every frame roughly a vblank of arrival slack. The offset moves only through a
/// slow bounded servo (below), so the phase of <c>delta</c> against the R/2 boundary is essentially
/// constant from beat cycle to beat cycle.
/// </para>
/// <para>
/// <b>Hysteresis at the midpoint.</b> When the two clocks are rationally locked with an adversarial
/// phase, some beat's <c>delta</c> can sit exactly on R/2, where any memoryless comparison is one
/// rounding error away from flipping. Inside a deadband of ±R/16 around the boundary the comparison
/// is therefore ignored and the beat length decides instead: hold while the current frame has been
/// displayed fewer than <c>ceil(framePeriod / R)</c> vblanks. That keeps the marginal beat at its
/// long (correct-rounding-up) length deterministically, so noise smaller than the deadband cannot
/// oscillate the cadence, while a genuine slow phase drift still crosses the band once and shifts
/// the beat exactly once.
/// </para>
/// <para>
/// <b>The phase servo.</b> After each shown frame, the offset is nudged toward zero display error
/// with gain 1/8, clamped to 0.25 ms per shown frame. This tracks slow content-clock-vs-pixel-clock
/// drift without letting a bad sample yank the phase, and it self-centers: the equilibrium is the
/// phase where the beat pattern's display errors average zero, which is also the phase that
/// maximizes every beat's distance from the R/2 decision boundary. A mapping that has become
/// nonsense (seek, long stall - <c>|delta|</c> beyond a frame period plus a refresh) re-anchors
/// instead of chasing, so a drifting content clock can never accumulate more than about one frame
/// period of hold; anything faster degrades to show-immediately, which is latest-wins behaviour.
/// </para>
/// <para>
/// <b>Refresh estimation.</b> The refresh period comes from an EMA over swap-return intervals the
/// caller observed while the swap was actually blocking on vblank. Samples more than 1.5x the
/// estimate (a missed vblank shows up as a double-length interval) or less than 0.75x (a queued,
/// non-blocking swap) are rejected rather than averaged in; a sustained run of rejections means the
/// window moved to a different panel, so the estimate reseeds from the new samples and pacing
/// disengages until it has converged again.
/// </para>
/// <para>
/// Single-threaded by design: all members are expected to be called from the presenting thread.
/// </para>
/// </remarks>
public sealed class PresentFramePacer
{
    /// <summary>
    /// Accepted swap samples required before <see cref="RefreshEstimateReady"/>. Long enough that one
    /// odd startup interval cannot define the grid, short enough to engage within a fraction of a
    /// second at any plausible refresh rate.
    /// </summary>
    public const int MinimumSwapSamples = 8;

    /// <summary>EMA weight: a new interval counts for 1/N. Matches the presenter's latency smoothing.</summary>
    private const int RefreshSmoothing = 16;

    /// <summary>Phase servo gain divisor: one shown frame corrects 1/8 of its display error.</summary>
    private const int ServoGainDivisor = 8;

    /// <summary>Deadband half-width divisor: hysteresis spans ±R/16 around the R/2 switch boundary.
    /// Half of the R/8 margin the self-centered equilibrium leaves, so steady-state beats decide
    /// outside the band while a boundary-riding beat decides inside it by hold count.</summary>
    private const int HysteresisDivisor = 16;

    /// <summary>
    /// Hard cap on phase movement per shown frame. Bounds how fast display error can be converted
    /// into latency drift; clock skew faster than this (per frame!) is not drift but a broken
    /// mapping, and hits the re-anchor path instead.
    /// </summary>
    private static readonly TimeSpan MaxPhaseStepPerShow = TimeSpan.FromMilliseconds(0.25);

    /// <summary>Sanity bounds on a single swap interval: 20-1000 Hz. Outside is measurement garbage.</summary>
    private static readonly TimeSpan MinPlausibleRefresh = TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan MaxPlausibleRefresh = TimeSpan.FromMilliseconds(50);

    /// <summary>Consecutive rejected samples before concluding the panel itself changed and reseeding.</summary>
    private const int RejectionsBeforeReseed = 8;

    /// <summary>
    /// PTS spacing above this is not a cadence to pace but a discontinuity (chapter skip, slideshow,
    /// long stall). Below 5 fps a one-vblank placement difference is invisible anyway, and holding a
    /// frame toward an ideal time seconds away would turn the pacer into a scheduler - the producer
    /// already chose WHEN to submit; this class only chooses WHICH vblank, so a gap presents
    /// promptly and re-anchors. This is also what bounds the worst-case hold at roughly one frame
    /// period: |delta| can never exceed this cap plus a refresh without re-anchoring.
    /// </summary>
    private static readonly TimeSpan MaxPacedFramePeriod = TimeSpan.FromMilliseconds(200);

    private long _refreshTicks;
    private int _acceptedSamples;
    private int _consecutiveRejections;

    private bool _hasAnchor;
    private long _offsetTicks;
    private int _consecutiveHolds;

    /// <summary>
    /// True once enough blocking swap intervals have been folded in for the vblank grid prediction to
    /// be trusted. Callers must keep latest-wins behaviour while this is false.
    /// </summary>
    public bool RefreshEstimateReady => _acceptedSamples >= MinimumSwapSamples;

    /// <summary>Smoothed refresh period, or <see cref="TimeSpan.Zero"/> before the first accepted sample.</summary>
    public TimeSpan EstimatedRefreshPeriod => TimeSpan.FromTicks(_refreshTicks);

    /// <summary>
    /// Folds one swap-return-to-swap-return interval into the refresh estimate. Only call for
    /// intervals between two swaps that actually blocked on vblank; a non-blocking swap in between
    /// makes the interval span an unknown number of refreshes and must break the caller's chain.
    /// </summary>
    public void ObserveSwapInterval(TimeSpan interval)
    {
        if (interval < MinPlausibleRefresh || interval > MaxPlausibleRefresh)
            return;

        var sample = interval.Ticks;
        if (_acceptedSamples == 0)
        {
            _refreshTicks = sample;
            _acceptedSamples = 1;
            _consecutiveRejections = 0;
            return;
        }

        var estimate = _refreshTicks;
        if (sample > estimate + estimate / 2 || sample < estimate - estimate / 4)
        {
            // A missed vblank reads as ~2R and a queued swap as ~0; averaging either in would bend
            // the grid the cadence stands on. But a long RUN of rejections is not noise - it is the
            // window on a different panel - so reseed and make the estimate re-earn readiness.
            if (++_consecutiveRejections >= RejectionsBeforeReseed)
            {
                _refreshTicks = sample;
                _acceptedSamples = 1;
                _consecutiveRejections = 0;
                ResetPhase();
            }

            return;
        }

        _consecutiveRejections = 0;
        _refreshTicks = estimate + (sample - estimate) / RefreshSmoothing;
        if (_acceptedSamples < MinimumSwapSamples)
            _acceptedSamples++;
    }

    /// <summary>
    /// Forgets the content-to-display phase mapping (the next decision re-anchors) while keeping the
    /// refresh estimate. Call when the presenter leaves pacing (swap stopped blocking, latest-wins
    /// fallback fired) - the vblank the old phase was measured against is no longer the loop's clock.
    /// </summary>
    public void ResetPhase()
    {
        _hasAnchor = false;
        _consecutiveHolds = 0;
    }

    /// <summary>
    /// Decides what to do with the next queued frame at the vblank following a swap that returned at
    /// <paramref name="lastSwapReturnTime"/> (any monotonic clock; must be the same clock across
    /// calls). <paramref name="currentFramePts"/> is the PTS of the frame currently on screen and
    /// <paramref name="nextFramePts"/> the PTS of the candidate; <paramref name="hasCurrentFrame"/>
    /// is false before anything has been presented, in which case the candidate anchors the phase and
    /// shows immediately.
    /// </summary>
    public PresentPacingDecision DecideAtVblank(
        TimeSpan lastSwapReturnTime,
        bool hasCurrentFrame,
        TimeSpan currentFramePts,
        TimeSpan nextFramePts)
    {
        var refresh = _refreshTicks;
        if (refresh <= 0)
        {
            // No grid to pace against - behave like latest-wins rather than guessing.
            return PresentPacingDecision.ShowNext;
        }

        var vblank = lastSwapReturnTime.Ticks + refresh;

        if (!_hasAnchor || !hasCurrentFrame)
        {
            var headroom = hasCurrentFrame
                ? Math.Clamp(nextFramePts.Ticks - currentFramePts.Ticks - refresh, 0, refresh)
                : 0;
            return AnchorAndShow(nextFramePts.Ticks, vblank, headroom);
        }

        var framePeriod = nextFramePts.Ticks - currentFramePts.Ticks;
        if (framePeriod <= 0 || framePeriod > MaxPacedFramePeriod.Ticks)
        {
            // Backwards/duplicate PTS is a seek or source restart; a huge forward gap is a
            // discontinuity, not a cadence (see MaxPacedFramePeriod). Neither is worth holding
            // against - re-anchor on the new frame and show it now. No headroom: the period is not
            // trustworthy here, and a later re-anchor with a valid period restores the jitter margin.
            return AnchorAndShow(nextFramePts.Ticks, vblank, headroomTicks: 0);
        }

        var ideal = nextFramePts.Ticks + _offsetTicks;
        var delta = ideal - vblank;

        if (delta > framePeriod + refresh || delta < -(framePeriod + refresh))
        {
            // The mapping no longer describes reality (seek, stall, clock drift faster than the servo
            // cap). Holding against a nonsense ideal time would add unbounded latency; showing now and
            // re-anchoring is exactly the latest-wins behaviour this class degrades to.
            return AnchorAndShow(nextFramePts.Ticks, vblank,
                Math.Clamp(framePeriod - refresh, 0, refresh));
        }

        var boundary = refresh / 2;
        var deadband = refresh / HysteresisDivisor;

        bool show;
        if (delta <= boundary - deadband)
        {
            show = true;
        }
        else if (delta >= boundary + deadband)
        {
            show = false;
        }
        else
        {
            // On the boundary the comparison is one rounding error from flipping, so the beat length
            // decides instead: complete the long (round-up) beat. Deterministic under any noise
            // smaller than the deadband; a real phase drift exits the band and shifts the beat once.
            var targetBeatVblanks = (framePeriod + refresh - 1) / refresh;
            var displayedSoFar = _consecutiveHolds + 1;
            show = displayedSoFar >= targetBeatVblanks;
        }

        if (!show)
        {
            _consecutiveHolds++;
            return PresentPacingDecision.HoldCurrent;
        }

        // Servo: fold 1/8 of this frame's display error into the phase, hard-capped so a single bad
        // frame cannot yank the grid alignment. Negative feedback (late frames push ideals later),
        // self-centering toward the zero-mean-error phase, which is the max-margin phase.
        var error = vblank - ideal;
        var step = error / ServoGainDivisor;
        var cap = MaxPhaseStepPerShow.Ticks;
        if (step > cap) step = cap;
        else if (step < -cap) step = -cap;
        _offsetTicks += step;
        _consecutiveHolds = 0;
        return PresentPacingDecision.ShowNext;
    }

    private PresentPacingDecision AnchorAndShow(long ptsTicks, long vblankTicks, long headroomTicks)
    {
        // The anchor frame shows NOW, but its ideal time is written headroomTicks LATER than its
        // actual display. Anchoring at the display time itself would give successor frames near-zero
        // arrival slack whenever the anchor frame happened to arrive just before a vblank: each
        // successor's ideal would sit almost exactly on the vblank it must be queued by, and ±1.5 ms
        // of producer jitter then makes frames miss their show vblank sporadically - exactly the
        // beat-flip this class exists to remove.
        //
        // The headroom cannot simply be a refresh, though: a frame's hold-slot occupancy is
        // (ideal - availability), and the slot must free up faster than frames arrive or the queue
        // backs up into the caller's depth fallback and drops. The per-frame slack budget is exactly
        // framePeriod - refresh, so callers pass min(framePeriod - refresh, refresh): 60-on-165
        // (2.75 vblanks/frame) gets a full refresh of jitter margin, while 59.94-on-60 (1.001) gets
        // ~none - correctly, because at ratio ~1 there is no slack with which to absorb jitter by
        // holding and the cadence is availability-driven anyway. The cost is bounded by one refresh
        // of pacing latency, inside the ~1-frame budget, and the servo keeps the phase near the
        // nearest zero-mean-error equilibrium afterwards, so the margin does not decay back into
        // the miss-prone alignment.
        _offsetTicks = vblankTicks + headroomTicks - ptsTicks;
        _hasAnchor = true;
        _consecutiveHolds = 0;
        return PresentPacingDecision.ShowNext;
    }
}
