using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Controls;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Timeline Phase D "Duck under…" (sidechain-lite) - the pure interval/splice math in
/// <see cref="TimelineDuckMath"/> plus the dialog VM that writes the dip through
/// <see cref="CueNodeViewModel.VolumeEnvelope"/>. Time bases under test: lane overlap on the group
/// timeline (audible start = TimelineStartMs + PreWaitMs), dips in bed CLIP time (timeline minus the
/// bed's audible start, clamped to the trimmed [0, EffectiveDurationMs] - StartOffset never adds in).
/// </summary>
public sealed class TimelineDuckMathTests
{
    private const int Eff = 30_000; // default bed trimmed length used by most fixtures

    private static CueAutomationPoint Pt(int timeMs, double levelDb, CueFadeCurve curve = CueFadeCurve.Linear) =>
        new() { TimeMs = timeMs, LevelDb = levelDb, CurveToNext = curve };

    private static TimelineIntervalMs I(int startMs, int endMs) => new(startMs, endMs);

    private static IReadOnlyList<CueAutomationPoint> Duck(
        IReadOnlyList<CueAutomationPoint> envelope, TimelineIntervalMs[] voices,
        int bedStartMs = 0, int bedEffectiveMs = Eff,
        double depthDb = -12, int rampMs = 300, int leadMs = 0,
        CueFadeCurve curve = CueFadeCurve.EqualPower) =>
        TimelineDuckMath.ApplyDucks(envelope, bedStartMs, bedEffectiveMs, voices, depthDb, rampMs, leadMs, curve);

    private static void AssertPoint(
        CueAutomationPoint point, int timeMs, double levelDb, CueFadeCurve curve = CueFadeCurve.Linear)
    {
        Assert.Equal(timeMs, point.TimeMs);
        Assert.Equal(levelDb, point.LevelDb, 6);
        Assert.Equal(curve, point.CurveToNext);
    }

    private static CueNodeViewModel MediaNode(
        int durationMs, int timelineStartMs = 0, int preWaitMs = 0, int startOffsetMs = 0, int endOffsetMs = 0) =>
        new(CueNodeKind.Media)
        {
            DurationMs = durationMs,
            TimelineStartMs = timelineStartMs,
            PreWaitMs = preWaitMs,
            StartOffsetMs = startOffsetMs,
            EndOffsetMs = endOffsetMs,
        };

    // ---- intervals ----

    [Fact]
    public void Overlaps_IsHalfOpen_TouchingEdgesDoNotOverlap()
    {
        Assert.True(TimelineDuckMath.Overlaps(I(0, 11), I(10, 20)));
        Assert.True(TimelineDuckMath.Overlaps(I(0, 100), I(10, 20))); // containment
        Assert.False(TimelineDuckMath.Overlaps(I(0, 10), I(10, 20))); // touching
        Assert.False(TimelineDuckMath.Overlaps(I(0, 10), I(30, 40)));
    }

    [Fact]
    public void MergeIntervals_SortsMergesOverlappingAndAdjacent_DropsEmpty()
    {
        var merged = TimelineDuckMath.MergeIntervals(
            [I(10_000, 20_000), I(0, 5_000), I(4_000, 8_000), I(8_000, 9_000), I(30, 30), I(50, 40)]);
        Assert.Equal(new[] { I(0, 9_000), I(10_000, 20_000) }, merged);
        Assert.Empty(TimelineDuckMath.MergeIntervals([]));
    }

    [Fact]
    public void BlockIntervalMs_UsesAudibleStartAndTrimmedDuration()
    {
        // Audible start = TimelineStartMs + PreWaitMs; width = the trimmed clip.
        var node = MediaNode(10_000, timelineStartMs: 1_000, preWaitMs: 500, startOffsetMs: 2_000, endOffsetMs: 1_000);
        Assert.Equal(I(1_500, 8_500), TimelineDuckMath.BlockIntervalMs(node));

        // Unprobed media renders (and overlaps) as the 5 s fallback block.
        var unprobed = MediaNode(0, timelineStartMs: 3_000);
        Assert.Equal(I(3_000, 3_000 + TimelineMath.FallbackBlockDurationMs), TimelineDuckMath.BlockIntervalMs(unprobed));
    }

    // ---- dip synthesis ----

    [Fact]
    public void ApplyDucks_EmptyEnvelope_WritesTheFourPointDip()
    {
        var result = Duck([], [I(10_000, 15_000)]);
        Assert.Equal(4, result.Count);
        // [start−lead−ramp, start−lead, end+lead, end+lead+ramp] with [restore, depth, depth, restore];
        // the chosen curve shapes the down and up ramps, the hold stays Linear.
        AssertPoint(result[0], 9_700, 0, CueFadeCurve.EqualPower);
        AssertPoint(result[1], 10_000, -12);
        AssertPoint(result[2], 15_000, -12, CueFadeCurve.EqualPower);
        AssertPoint(result[3], 15_300, 0);
    }

    [Fact]
    public void ApplyDucks_ConvertsTimelineToClipTime_ByTheBedAudibleStartOnly()
    {
        // Bed: TimelineStartMs 1000 + PreWait 200 → audible start 1200; StartOffset 2000 trims a
        // 30 s file to eff 28 s. Envelope times are post-StartOffset clip times anchored at the
        // block's left edge, so the voice interval shifts by −1200 - the StartOffset must NOT add.
        var result = Duck([], [I(5_000, 8_000)], bedStartMs: 1_200, bedEffectiveMs: 28_000);
        Assert.Equal(new[] { 3_500, 3_800, 6_800, 7_100 }, result.Select(p => p.TimeMs));
    }

    [Fact]
    public void ApplyDucks_NoOverlapWithTheBed_ReturnsTheOriginalReference()
    {
        var envelope = new[] { Pt(0, -6) };
        Assert.Same(envelope, Duck(envelope, [I(40_000, 45_000)])); // fully after the bed
        Assert.Same(envelope, Duck(envelope, [I(1_000, 1_000)])); // zero-length
        Assert.Same(envelope, Duck(envelope, [I(10_000, 15_000)], bedEffectiveMs: 0)); // unprobed bed
    }

    [Fact]
    public void ApplyDucks_RestoreLevelsSampleTheBedEnvelope_NotUnity()
    {
        // A bed already riding at −6 dB stays −6 outside the dip and dips RELATIVE to it.
        var result = Duck([Pt(0, -6)], [I(10_000, 15_000)]);
        Assert.Equal(5, result.Count); // the kept t=0 point plus the dip
        AssertPoint(result[0], 0, -6);
        AssertPoint(result[1], 9_700, -6, CueFadeCurve.EqualPower);
        AssertPoint(result[2], 10_000, -18);
        AssertPoint(result[3], 15_000, -18, CueFadeCurve.EqualPower);
        AssertPoint(result[4], 15_300, -6);
    }

    [Fact]
    public void ApplyDucks_DepthFollowsEachDipEdgeSample()
    {
        // Step envelope: 0 dB before 12 s, −6 dB after - each dip edge dips relative to ITS level.
        var result = Duck([Pt(12_000, 0), Pt(12_000, -6)], [I(10_000, 15_000)]);
        Assert.Equal(4, result.Count); // the step points sat inside the dip span and were replaced
        AssertPoint(result[0], 9_700, 0, CueFadeCurve.EqualPower);
        AssertPoint(result[1], 10_000, -12);
        AssertPoint(result[2], 15_000, -18, CueFadeCurve.EqualPower);
        AssertPoint(result[3], 15_300, -6);
    }

    [Fact]
    public void ApplyDucks_PreservesOutsidePoints_ReplacesInsideOnes()
    {
        var envelope = new[] { Pt(1_000, -6), Pt(12_000, -6, CueFadeCurve.SCurve), Pt(20_000, -6) };
        var result = Duck(envelope, [I(10_000, 15_000)]);
        Assert.Equal(6, result.Count);
        AssertPoint(result[0], 1_000, -6); // kept
        AssertPoint(result[1], 9_700, -6, CueFadeCurve.EqualPower);
        AssertPoint(result[2], 10_000, -18);
        AssertPoint(result[3], 15_000, -18, CueFadeCurve.EqualPower);
        AssertPoint(result[4], 15_300, -6);
        AssertPoint(result[5], 20_000, -6); // kept; the 12 s point inside the span is gone
    }

    [Fact]
    public void ApplyDucks_ReApplyWithSameInputs_IsIdempotent()
    {
        var envelope = new[] { Pt(1_000, -6), Pt(12_000, -3, CueFadeCurve.SCurve), Pt(20_000, -6) };
        var once = Duck(envelope, [I(10_000, 15_000)]);
        var twice = Duck(once, [I(10_000, 15_000)]);
        Assert.Equal(once, twice);

        // Also idempotent across a lead/curve variation and with two merged voices.
        var voices = new[] { I(5_000, 6_000), I(6_500, 7_500) };
        once = Duck(envelope, voices, leadMs: 200, curve: CueFadeCurve.SCurve);
        twice = Duck(once, voices, leadMs: 200, curve: CueFadeCurve.SCurve);
        Assert.Equal(once, twice);
    }

    // ---- trim-edge clamping ----

    [Fact]
    public void ApplyDucks_OverlapBeforeTheClipStart_HoldsDepthFromTimeZero()
    {
        // Voice-over already talking when the bed starts: the entry ramp falls fully off the clip,
        // so the restore-in point is dropped and the bed enters at depth.
        var result = Duck([], [I(3_000, 9_000)], bedStartMs: 5_000);
        Assert.Equal(3, result.Count);
        AssertPoint(result[0], 0, -12);
        AssertPoint(result[1], 4_000, -12, CueFadeCurve.EqualPower);
        AssertPoint(result[2], 4_300, 0);
    }

    [Fact]
    public void ApplyDucks_RampPartiallyBeforeTheClipStart_CompressesOntoTimeZero()
    {
        // Only the ramp's head clips (dip starts at −100): the restore point clamps to 0 and the
        // ramp compresses into [0, 200].
        var result = Duck([], [I(200, 5_000)]);
        Assert.Equal(4, result.Count);
        AssertPoint(result[0], 0, 0, CueFadeCurve.EqualPower);
        AssertPoint(result[1], 200, -12);
        AssertPoint(result[2], 5_000, -12, CueFadeCurve.EqualPower);
        AssertPoint(result[3], 5_300, 0);
    }

    [Fact]
    public void ApplyDucks_OverlapPastTheClipEnd_HoldsDepthToTheEnd()
    {
        var result = Duck([], [I(29_900, 32_000)]);
        Assert.Equal(3, result.Count); // exit ramp fully off the clip → no restore-out point
        AssertPoint(result[0], 29_600, 0, CueFadeCurve.EqualPower);
        AssertPoint(result[1], 29_900, -12);
        AssertPoint(result[2], 30_000, -12, CueFadeCurve.EqualPower);
    }

    // ---- parameters ----

    [Fact]
    public void ApplyDucks_ZeroRamp_MakesInstantStepsAtTheDipEdges()
    {
        var result = Duck([], [I(10_000, 15_000)], rampMs: 0);
        Assert.Equal(4, result.Count);
        // Coincident pairs render/play as steps; order restore→depth entering, depth→restore leaving.
        AssertPoint(result[0], 10_000, 0, CueFadeCurve.EqualPower);
        AssertPoint(result[1], 10_000, -12);
        AssertPoint(result[2], 15_000, -12, CueFadeCurve.EqualPower);
        AssertPoint(result[3], 15_000, 0);
    }

    [Fact]
    public void ApplyDucks_LeadStartsTheDuckEarlyAndRecoversLate()
    {
        var result = Duck([], [I(10_000, 15_000)], leadMs: 200);
        Assert.Equal(new[] { 9_500, 9_800, 15_200, 15_500 }, result.Select(p => p.TimeMs));
        Assert.Equal(-12, result[1].LevelDb, 6); // depth reached at start − lead
    }

    [Fact]
    public void ApplyDucks_CloseOverlaps_MergeIntoOneDipWithoutARecoverBump()
    {
        // Gap (500 ms) < 2·(lead+ramp): the padded footprints fuse - one hold, no bump between.
        var result = Duck([], [I(5_000, 6_000), I(6_500, 7_500)]);
        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { 4_700, 5_000, 7_500, 7_800 }, result.Select(p => p.TimeMs));
    }

    [Fact]
    public void ApplyDucks_DistantOverlaps_GetTheirOwnDips()
    {
        var result = Duck([], [I(5_000, 6_000), I(20_000, 21_000)]);
        Assert.Equal(8, result.Count);
        Assert.Equal(
            new[] { 4_700, 5_000, 6_000, 6_300, 19_700, 20_000, 21_000, 21_300 },
            result.Select(p => p.TimeMs));
    }

    [Fact]
    public void ApplyDucks_ClampsTheDepthToTheAuthoringFloor()
    {
        var result = Duck([Pt(0, -55)], [I(10_000, 15_000)]);
        Assert.Equal(-55, result[1].LevelDb, 6); // restore
        Assert.Equal(CueAutomationPoint.SilenceLevelDb, result[2].LevelDb, 6); // −67 clamps to −60
    }

    // ---- dialog VM (write-through CueNodeViewModel) ----

    [Fact]
    public void For_ListsOnlyOtherMediaLanesOverlappingTheBed_PreWaitAware()
    {
        var bed = MediaNode(30_000);
        var overlapping = MediaNode(5_000, timelineStartMs: 10_000);
        // Would overlap at its authored start, but the pre-wait pushes the AUDIBLE block past the bed.
        var preWaitedOut = MediaNode(5_000, timelineStartMs: 29_000, preWaitMs: 2_000);
        // Authored past most of the bed, still overlapping through its audible start.
        var preWaitedIn = MediaNode(5_000, timelineStartMs: 27_000, preWaitMs: 1_000);
        var action = new CueNodeViewModel(CueNodeKind.Action) { TimelineStartMs = 12_000 };

        var vm = DuckUnderDialogViewModel.For(bed, [bed, overlapping, preWaitedOut, preWaitedIn, action]);

        Assert.Equal(new[] { overlapping, preWaitedIn }, vm.Lanes.Select(l => l.Node));
        Assert.All(vm.Lanes, l => Assert.True(l.IsSelected)); // default = all overlapping
        Assert.True(vm.HasOverlappingLanes);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void Apply_WritesTheDipThroughTheBedViewModel()
    {
        var bed = MediaNode(30_000);
        var voice = MediaNode(5_000, timelineStartMs: 10_000);
        var vm = DuckUnderDialogViewModel.For(bed, [bed, voice]);

        var raised = false;
        bed.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(CueNodeViewModel.VolumeEnvelope);

        Assert.True(vm.Apply()); // defaults: −12 dB, 300 ms ramp, 0 lead, EqualPower
        Assert.True(raised);

        var envelope = bed.VolumeEnvelope;
        Assert.Equal(4, envelope.Count);
        AssertPoint(envelope[0], 9_700, 0, CueFadeCurve.EqualPower);
        AssertPoint(envelope[1], 10_000, -12);
        AssertPoint(envelope[2], 15_000, -12, CueFadeCurve.EqualPower);
        AssertPoint(envelope[3], 15_300, 0);

        // Re-apply through the VM changes nothing (idempotent authoring).
        vm.Apply();
        Assert.Equal(envelope, bed.VolumeEnvelope);
    }

    [Fact]
    public void Apply_WithNoLaneSelected_IsANoOp()
    {
        var bed = MediaNode(30_000);
        var voice = MediaNode(5_000, timelineStartMs: 10_000);
        var vm = DuckUnderDialogViewModel.For(bed, [bed, voice]);

        vm.Lanes[0].IsSelected = false;
        Assert.False(vm.CanApply);
        Assert.False(vm.Apply());
        Assert.Empty(bed.VolumeEnvelope);
    }
}
