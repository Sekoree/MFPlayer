using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// Direct unit tests for <see cref="PresentFramePacer"/> plus cadence simulations that mirror the
/// SDL3 render loop's paced pull policy (hold-one-frame slot, depth-&gt;1 latest-wins fallback). The
/// simulations are the real spec: they assert the exact beat rotations the nearest-vblank rule must
/// produce for the classic rate pairs, and that producer jitter cannot flip a beat.
/// </summary>
public sealed class PresentFramePacerTests
{
    private static readonly VideoFormat Bgra2X1 = new(2, 1, PixelFormat.Bgra32, new Rational(60, 1));

    private static VideoFrame Frame(TimeSpan pts) =>
        new(pts, Bgra2X1, [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }], [8]);

    private static PresentFramePacer ReadyPacer(TimeSpan refresh)
    {
        var pacer = new PresentFramePacer();
        for (var i = 0; i < PresentFramePacer.MinimumSwapSamples; i++)
            pacer.ObserveSwapInterval(refresh);
        Assert.True(pacer.RefreshEstimateReady);
        return pacer;
    }

    // ---------- refresh estimation -----------------------------------------

    [Fact]
    public void Not_ready_before_minimum_samples_and_defaults_to_show()
    {
        var pacer = new PresentFramePacer();
        for (var i = 0; i < PresentFramePacer.MinimumSwapSamples - 1; i++)
            pacer.ObserveSwapInterval(TimeSpan.FromMilliseconds(6));
        Assert.False(pacer.RefreshEstimateReady);

        // Latest-wins degradation: an unpaceable pacer must never hold a frame hostage.
        Assert.Equal(
            PresentPacingDecision.ShowNext,
            pacer.DecideAtVblank(TimeSpan.Zero, hasCurrentFrame: true,
                currentFramePts: TimeSpan.Zero, nextFramePts: TimeSpan.FromMilliseconds(16)));

        pacer.ObserveSwapInterval(TimeSpan.FromMilliseconds(6));
        Assert.True(pacer.RefreshEstimateReady);
    }

    [Fact]
    public void Missed_vblank_double_interval_is_rejected_not_averaged()
    {
        var refresh = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 165);
        var pacer = ReadyPacer(refresh);
        var before = pacer.EstimatedRefreshPeriod;

        // A stall that misses a vblank reads as ~2R at the swap boundary. Averaging even one of
        // those in would bend the grid every cadence decision stands on.
        pacer.ObserveSwapInterval(refresh + refresh);
        Assert.Equal(before, pacer.EstimatedRefreshPeriod);
        Assert.True(pacer.RefreshEstimateReady);
    }

    [Fact]
    public void Implausible_intervals_are_ignored_entirely()
    {
        var pacer = new PresentFramePacer();
        pacer.ObserveSwapInterval(TimeSpan.FromMilliseconds(0.2)); // sub-1ms: 1000+ Hz, garbage
        pacer.ObserveSwapInterval(TimeSpan.FromMilliseconds(200)); // 5 Hz, garbage
        Assert.Equal(TimeSpan.Zero, pacer.EstimatedRefreshPeriod);
    }

    [Fact]
    public void Sustained_rate_change_reseeds_and_reearns_readiness()
    {
        // Window dragged from a 165 Hz panel to a 60 Hz one: every new interval rejects against the
        // old estimate, so after a sustained run the pacer must conclude the panel changed rather
        // than keep rejecting reality forever.
        var pacer = ReadyPacer(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 165));
        var sixty = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

        for (var i = 0; i < 8; i++)
            pacer.ObserveSwapInterval(sixty);

        Assert.False(pacer.RefreshEstimateReady); // reseeded: one sample, must re-earn trust
        Assert.Equal(sixty, pacer.EstimatedRefreshPeriod);

        for (var i = 0; i < PresentFramePacer.MinimumSwapSamples - 1; i++)
            pacer.ObserveSwapInterval(sixty);
        Assert.True(pacer.RefreshEstimateReady);
    }

    // ---------- decision rule ----------------------------------------------

    [Fact]
    public void First_frame_anchors_and_shows_immediately()
    {
        var pacer = ReadyPacer(TimeSpan.FromMilliseconds(10));
        Assert.Equal(
            PresentPacingDecision.ShowNext,
            pacer.DecideAtVblank(TimeSpan.Zero, hasCurrentFrame: false,
                currentFramePts: TimeSpan.Zero, nextFramePts: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Backwards_pts_reanchors_and_shows()
    {
        var pacer = ReadyPacer(TimeSpan.FromMilliseconds(10));
        pacer.DecideAtVblank(TimeSpan.Zero, false, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        // A seek: PTS jumps backwards. The old mapping describes nothing; holding against it would
        // be arbitrary latency on the first frame after the seek.
        Assert.Equal(
            PresentPacingDecision.ShowNext,
            pacer.DecideAtVblank(TimeSpan.FromMilliseconds(10), hasCurrentFrame: true,
                currentFramePts: TimeSpan.FromSeconds(10), nextFramePts: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Large_forward_pts_gap_shows_immediately_instead_of_holding()
    {
        var pacer = ReadyPacer(TimeSpan.FromMilliseconds(10));
        pacer.DecideAtVblank(TimeSpan.Zero, false, TimeSpan.Zero, TimeSpan.Zero);

        // A 10 s PTS gap (chapter skip, slideshow) must not become a 10 s hold: the producer chose
        // to submit NOW, so present promptly and re-anchor the timeline on the new frame.
        Assert.Equal(
            PresentPacingDecision.ShowNext,
            pacer.DecideAtVblank(TimeSpan.FromMilliseconds(10), hasCurrentFrame: true,
                currentFramePts: TimeSpan.Zero, nextFramePts: TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// 40 fps on a 100 Hz grid is exactly 2.5 vblanks per frame, which parks every other beat's
    /// decision precisely ON the R/2 midpoint - the adversarial alignment where a memoryless
    /// comparison flips on a rounding error. With the first-ever anchor (no current frame, zero
    /// headroom, offset 10 ms) the THIRD decision sees delta of exactly R/2 = 5 ms; the deadband's
    /// hold-count rule must resolve it deterministically (the current frame has only been displayed
    /// 2 of ceil(2.5)=3 vblanks, so it holds and completes the long beat), and the long run must
    /// settle into the 3-2 alternation.
    /// </summary>
    [Fact]
    public void Exact_midpoint_tie_resolves_deterministically_via_hold_count()
    {
        var (decisions, showVblanks) = TieScenario(swapJitter: null, vblankCount: 60);

        // Hand-traceable prefix: anchor show, a clear hold, the exact-tie hold, then the switch.
        Assert.Equal(PresentPacingDecision.ShowNext, decisions[0]);
        Assert.Equal(PresentPacingDecision.HoldCurrent, decisions[1]);
        Assert.Equal(PresentPacingDecision.HoldCurrent, decisions[2]); // delta == R/2 exactly
        Assert.Equal(PresentPacingDecision.ShowNext, decisions[3]);

        // After the anchor headroom is absorbed the cadence must be the strict 3-2 alternation.
        var beats = new List<int>();
        for (var i = 1; i < showVblanks.Count; i++)
            beats.Add(showVblanks[i] - showVblanks[i - 1]);
        var settled = beats.Skip(6).ToArray();
        Assert.True(settled.Length >= 10, $"expected at least 10 settled beats, got {settled.Length}");
        Assert.All(settled, b => Assert.InRange(b, 2, 3));
        for (var i = 1; i < settled.Length; i++)
            Assert.True(settled[i] != settled[i - 1], $"beats {i - 1},{i} both {settled[i]}: 2.5 must alternate");
    }

    [Fact]
    public void Vblank_timestamp_jitter_inside_the_deadband_cannot_flip_the_tie()
    {
        // ±0.25 ms of swap-return jitter, well inside the ±R/16 = ±0.625 ms deadband at 100 Hz. The
        // decision sequence must be bit-identical to the clean run - this is the hysteresis working:
        // a memoryless comparator at the exact midpoint would flip on the sign of the jitter.
        var (clean, _) = TieScenario(swapJitter: null, vblankCount: 60);
        var (jittered, _) = TieScenario(
            swapJitter: i => TimeSpan.FromMilliseconds(i % 2 == 0 ? 0.25 : -0.25),
            vblankCount: 60);
        Assert.Equal(clean, jittered);
    }

    private static (PresentPacingDecision[] Decisions, List<int> ShowVblanks) TieScenario(
        Func<int, TimeSpan>? swapJitter, int vblankCount)
    {
        var refresh = TimeSpan.FromMilliseconds(10); // 100 Hz
        var framePeriod = TimeSpan.FromMilliseconds(25); // 40 fps -> ratio exactly 2.5
        var pacer = ReadyPacer(refresh);

        var decisions = new List<PresentPacingDecision>();
        var showVblanks = new List<int>();
        var hasCurrent = false;
        var currentPts = TimeSpan.Zero;
        var nextIndex = 0;
        for (var vblank = 0; vblank < vblankCount; vblank++)
        {
            var swapReturn = TimeSpan.FromTicks(refresh.Ticks * vblank)
                             + (swapJitter?.Invoke(vblank) ?? TimeSpan.Zero);
            var nextPts = TimeSpan.FromTicks(framePeriod.Ticks * nextIndex);
            var decision = pacer.DecideAtVblank(swapReturn, hasCurrent, currentPts, nextPts);
            decisions.Add(decision);
            if (decision == PresentPacingDecision.ShowNext)
            {
                showVblanks.Add(vblank);
                currentPts = nextPts;
                hasCurrent = true;
                nextIndex++;
            }
        }

        return (decisions.ToArray(), showVblanks);
    }

    // ---------- cadence simulations ----------------------------------------

    /// <summary>
    /// Result of driving the pacer through the same pull policy the SDL3 render loop uses: one
    /// hold-one-frame slot, FIFO pull at depth &lt;= 1, latest-wins fallback at depth &gt; 1, repeats
    /// counted for deliberate holds and for flowing-content empty pulls alike.
    /// </summary>
    private sealed class SimResult
    {
        public readonly List<int> ShowVblanks = [];
        public int Repeats;
        public long Drops;

        public int[] Beats()
        {
            var beats = new int[Math.Max(0, ShowVblanks.Count - 1)];
            for (var i = 1; i < ShowVblanks.Count; i++)
                beats[i - 1] = ShowVblanks[i] - ShowVblanks[i - 1];
            return beats;
        }
    }

    private static SimResult Simulate(
        long refreshTicks,
        long framePeriodTicks,
        int vblankCount,
        Func<int, TimeSpan>? arrivalJitter = null)
    {
        var refresh = TimeSpan.FromTicks(refreshTicks);
        var latency = TimeSpan.FromMilliseconds(5); // decode-to-submit delay, arbitrary but realistic
        var queue = new PresentFrameQueue();
        var pacer = new PresentFramePacer();
        var result = new SimResult();

        VideoFrame? held = null;
        long droppedNew = 0;
        var hasCurrent = false;
        var currentPts = TimeSpan.Zero;
        var emptyPulls = 0;
        var nextFrameIndex = 0;

        for (var k = 0; k < vblankCount; k++)
        {
            var now = TimeSpan.FromTicks(refreshTicks * k); // this vblank's swap return
            if (k > 0)
                pacer.ObserveSwapInterval(refresh);

            // Producer side: everything that has arrived by this vblank is in the queue, exactly as
            // Submit would have put it there between loop iterations.
            while (true)
            {
                var pts = TimeSpan.FromTicks(framePeriodTicks * nextFrameIndex);
                var arrival = pts + latency + (arrivalJitter?.Invoke(nextFrameIndex) ?? TimeSpan.Zero);
                if (arrival > now)
                    break;
                queue.Enqueue(Frame(pts), out var evicted);
                evicted?.Dispose();
                nextFrameIndex++;
            }

            VideoFrame? show = null;
            var hold = false;
            var paced = pacer.RefreshEstimateReady && k > 0;
            if (paced)
            {
                if (held is not null && queue.Count > 1)
                {
                    held.Dispose();
                    held = null;
                    droppedNew++;
                    pacer.ResetPhase();
                }

                if (held is null)
                {
                    if (queue.Count > 1)
                    {
                        show = queue.TryDequeueLatest(out _, out var superseded);
                        foreach (var stale in superseded)
                            stale.Dispose();
                        pacer.ResetPhase();
                    }
                    else
                    {
                        held = queue.TryDequeue();
                    }
                }

                if (show is null && held is not null)
                {
                    var decision = pacer.DecideAtVblank(now, hasCurrent, currentPts, held.PresentationTime);
                    if (decision == PresentPacingDecision.ShowNext)
                    {
                        show = held;
                        held = null;
                    }
                    else
                    {
                        hold = true;
                    }
                }
            }
            else
            {
                if (held is not null)
                {
                    pacer.ResetPhase();
                    show = held;
                    held = null;
                }

                var newest = queue.TryDequeueLatest(out _, out var superseded);
                foreach (var stale in superseded)
                    stale.Dispose();
                if (newest is not null)
                {
                    if (show is not null)
                    {
                        show.Dispose();
                        droppedNew++;
                    }

                    show = newest;
                }
            }

            if (show is not null)
            {
                result.ShowVblanks.Add(k);
                currentPts = show.PresentationTime;
                hasCurrent = true;
                show.Dispose();
                emptyPulls = 0;
            }
            else if (hold)
            {
                result.Repeats++;
                emptyPulls = 0;
            }
            else if (hasCurrent && emptyPulls < 4)
            {
                emptyPulls++;
                result.Repeats++;
            }
        }

        held?.Dispose();
        result.Drops = droppedNew + queue.DroppedOldest;
        return result;
    }

    /// <summary>
    /// 60 fps on 165 Hz is 2.75 vblanks per frame; the only correct steady state is the Bresenham
    /// rotation of 2.75, i.e. beats of {3,3,3,2} repeating (every 4 consecutive beats sum to 11).
    /// Latest-wins produced 4-2 flips and drop+repeat pairs here; the pacer must not.
    /// </summary>
    [Fact]
    public void Sixty_fps_on_165Hz_is_an_exact_3_3_3_2_rotation_with_zero_drops()
    {
        // Tick values chosen so framePeriod/refresh is EXACTLY 2.75 (165.00 Hz / 59.998 fps); the
        // real clocks are never this clean, but the ratio is the property under test.
        var result = Simulate(refreshTicks: 60_608, framePeriodTicks: 166_672, vblankCount: 1200);

        Assert.Equal(0, result.Drops);

        var beats = result.Beats().Skip(30).ToArray(); // past anchor + servo settling
        Assert.True(beats.Length > 350, $"expected hundreds of settled beats, got {beats.Length}");
        Assert.All(beats, b => Assert.InRange(b, 2, 3));
        for (var i = 0; i + 4 <= beats.Length; i++)
        {
            var window = beats[i] + beats[i + 1] + beats[i + 2] + beats[i + 3];
            Assert.True(window == 11,
                $"beat window at {i} sums to {window}, not 11: [{beats[i]},{beats[i + 1]},{beats[i + 2]},{beats[i + 3]}]");
        }
    }

    /// <summary>
    /// ±1.5 ms of producer arrival jitter is the measured failure mode that flips latest-wins beats.
    /// The pacer's decision consumes only PTS and the vblank grid - arrival time affects nothing but
    /// availability, and the hold slot gives every frame about a vblank of slack - so the cadence
    /// must be exactly the clean 3-3-3-2 rotation.
    /// </summary>
    [Fact]
    public void Arrival_jitter_of_1_5ms_does_not_change_the_60_on_165_cadence()
    {
        var rng = new Random(12345);
        var result = Simulate(
            refreshTicks: 60_608,
            framePeriodTicks: 166_672,
            vblankCount: 1200,
            arrivalJitter: _ => TimeSpan.FromMilliseconds(rng.NextDouble() * 3.0 - 1.5));

        Assert.Equal(0, result.Drops);

        var beats = result.Beats().Skip(30).ToArray();
        Assert.True(beats.Length > 350, $"expected hundreds of settled beats, got {beats.Length}");
        Assert.All(beats, b => Assert.InRange(b, 2, 3));
        for (var i = 0; i + 4 <= beats.Length; i++)
        {
            var window = beats[i] + beats[i + 1] + beats[i + 2] + beats[i + 3];
            Assert.True(window == 11,
                $"beat window at {i} sums to {window}, not 11: [{beats[i]},{beats[i + 1]},{beats[i + 2]},{beats[i + 3]}]");
        }
    }

    /// <summary>
    /// 59.94-style content on a 60 Hz-style panel (ratio exactly 1.001): the producer loses one
    /// frame time roughly every 1001 frames (~16.7 s), so the correct output is a lone 2-vblank beat
    /// about every 1000 shows, no drops ever, and in particular never a drop and a repeat next to
    /// each other (the latest-wins artifact where a beat flip discards one frame and doubles its
    /// neighbour).
    /// </summary>
    [Fact]
    public void NTSC_content_on_60Hz_repeats_once_per_thousand_frames_and_never_drops()
    {
        // 167000/167167 ticks: 59.88 Hz panel, ratio exactly 1001/1000. ~334 simulated seconds.
        var result = Simulate(refreshTicks: 167_000, framePeriodTicks: 167_167, vblankCount: 20_000);

        Assert.Equal(0, result.Drops);

        var beats = result.Beats().Skip(50).ToArray();
        Assert.All(beats, b => Assert.InRange(b, 1, 2));

        var repeatPositions = new List<int>();
        for (var i = 0; i < beats.Length; i++)
        {
            if (beats[i] == 2)
                repeatPositions.Add(i);
        }

        // ~19900 settled shows / 1001 ≈ 19.9 stretched beats expected.
        Assert.InRange(repeatPositions.Count, 17, 22);
        for (var i = 1; i < repeatPositions.Count; i++)
        {
            var spacing = repeatPositions[i] - repeatPositions[i - 1];
            Assert.True(spacing is > 900 and < 1100,
                $"stretched beats {i - 1} and {i} are {spacing} shows apart; expected ~1001 (never adjacent)");
        }
    }

    /// <summary>
    /// 24 fps on 60 Hz is 2.5 vblanks per frame - the film pulldown case. There is no integer beat
    /// that fits, so the correct cadence is the strict 3-2 alternation (2:3 pulldown); any 3-3 or
    /// 2-2 pair means a beat slipped.
    /// </summary>
    [Fact]
    public void Film_24_on_60Hz_is_a_strict_3_2_alternation()
    {
        // 166660/416650 ticks: 60.00 Hz-ish with ratio EXACTLY 2.5.
        var result = Simulate(refreshTicks: 166_660, framePeriodTicks: 416_650, vblankCount: 2000);

        Assert.Equal(0, result.Drops);

        var beats = result.Beats().Skip(30).ToArray();
        Assert.True(beats.Length > 500, $"expected hundreds of settled beats, got {beats.Length}");
        Assert.All(beats, b => Assert.InRange(b, 2, 3));
        for (var i = 1; i < beats.Length; i++)
        {
            Assert.True(beats[i] != beats[i - 1],
                $"beats {i - 1} and {i} are both {beats[i]}; 2.5 vblanks/frame must alternate 3-2");
        }
    }

    /// <summary>
    /// Content faster than the panel (120 fps on 60 Hz) cannot be paced - there is no vblank for
    /// every frame - so the loop must degrade to exactly the old latest-wins behaviour: a new frame
    /// on every single vblank, the surplus dropped, and no repeats and no added latency from holding.
    /// </summary>
    [Fact]
    public void Content_faster_than_refresh_degrades_to_latest_wins()
    {
        // Ratio exactly 0.5: two frames arrive per vblank.
        var result = Simulate(refreshTicks: 166_660, framePeriodTicks: 83_330, vblankCount: 2000);

        Assert.Equal(0, result.Repeats);
        Assert.All(result.Beats(), b => Assert.Equal(1, b));

        // Roughly one of the two frames per vblank is superseded; allow slack for startup.
        Assert.True(result.Drops > result.ShowVblanks.Count / 2,
            $"expected ~1 drop per vblank, got {result.Drops} over {result.ShowVblanks.Count} shows");
    }
}
