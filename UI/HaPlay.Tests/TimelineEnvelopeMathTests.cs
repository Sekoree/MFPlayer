using Avalonia;
using HaPlay.ViewModels;
using HaPlay.Views.Controls;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Volume-envelope editor (Phase B): the timeline canvas's pure envelope math - dB↔y and
/// time↔x mapping, runtime-mirroring level sampling, point/line hit-testing, sorted insert/drag
/// clamping, curve cycling - plus the VM passthrough that persistence reads.</summary>
public sealed class TimelineEnvelopeMathTests
{
    private const double PxPerMs = 0.1; // 100 px per second

    // A 30 s trimmed block on lane 0 (x 1000..4000 at 0.1 px/ms), the fixture most tests share.
    private static readonly Rect Block = TimelineMath.BlockRect(0, 10_000, 30_000, PxPerMs);

    private static CueAutomationPoint Pt(int timeMs, double levelDb, CueFadeCurve curve = CueFadeCurve.Linear) =>
        new() { TimeMs = timeMs, LevelDb = levelDb, CurveToNext = curve };

    // ---- dB ↔ y mapping ----

    [Fact]
    public void EnvelopeYForDb_MapsLinearInDb_WithZeroDbAtOneSixthFromTop()
    {
        // +12 dB = block top, −60 dB = block bottom, so 0 dB sits 12/72 = ⅙ of the height down.
        Assert.Equal(Block.Y, TimelineMath.EnvelopeYForDb(Block, CueAutomationPoint.MaxLevelDb), 6);
        Assert.Equal(Block.Bottom, TimelineMath.EnvelopeYForDb(Block, CueAutomationPoint.SilenceLevelDb), 6);
        Assert.Equal(Block.Y + Block.Height / 6, TimelineMath.EnvelopeYForDb(Block, 0), 6);

        // Out-of-range levels clamp onto the edges (incl. −inf "silence" points).
        Assert.Equal(Block.Y, TimelineMath.EnvelopeYForDb(Block, 40), 6);
        Assert.Equal(Block.Bottom, TimelineMath.EnvelopeYForDb(Block, double.NegativeInfinity), 6);
    }

    [Fact]
    public void EnvelopeDbForY_IsClampedInverse()
    {
        foreach (var db in new[] { -60.0, -30.0, -6.0, 0.0, 6.0, 12.0 })
            Assert.Equal(db, TimelineMath.EnvelopeDbForY(Block, TimelineMath.EnvelopeYForDb(Block, db)), 6);

        Assert.Equal(CueAutomationPoint.MaxLevelDb, TimelineMath.EnvelopeDbForY(Block, Block.Y - 50));
        Assert.Equal(CueAutomationPoint.SilenceLevelDb, TimelineMath.EnvelopeDbForY(Block, Block.Bottom + 50));
    }

    // ---- time ↔ x mapping ----

    [Fact]
    public void EnvelopeTimeForX_IsClipRelativeAndClampedToTrimmedRange()
    {
        // Envelope time 0 = the block's LEFT edge (times are post-StartOffset clip times).
        Assert.Equal(0, TimelineMath.EnvelopeTimeForX(Block, Block.X, PxPerMs, 30_000));
        Assert.Equal(15_000, TimelineMath.EnvelopeTimeForX(Block, Block.X + 1500, PxPerMs, 30_000));
        Assert.Equal(0, TimelineMath.EnvelopeTimeForX(Block, Block.X - 100, PxPerMs, 30_000));
        Assert.Equal(30_000, TimelineMath.EnvelopeTimeForX(Block, Block.Right + 100, PxPerMs, 30_000));
    }

    [Fact]
    public void EnvelopePointCenter_ClampsToBlockRightEdge()
    {
        var inside = TimelineMath.EnvelopePointCenter(Block, Pt(15_000, 0), PxPerMs);
        Assert.Equal(Block.X + 1500, inside.X, 3);
        Assert.Equal(TimelineMath.EnvelopeYForDb(Block, 0), inside.Y, 6);

        // A point authored past the trimmed range (e.g. after a re-trim) parks on the right edge.
        Assert.Equal(Block.Right, TimelineMath.EnvelopePointCenter(Block, Pt(99_000, 0), PxPerMs).X, 3);
    }

    // ---- level sampling (mirror of VolumeEnvelopes.Sample) ----

    [Fact]
    public void EnvelopeLevelDbAt_EmptyIsUnity_FlatBeforeFirstAndAfterLast()
    {
        Assert.Equal(0, TimelineMath.EnvelopeLevelDbAt([], 1234));

        var envelope = new[] { Pt(1000, -6), Pt(2000, 0) };
        Assert.Equal(-6, TimelineMath.EnvelopeLevelDbAt(envelope, 0), 6);
        Assert.Equal(-6, TimelineMath.EnvelopeLevelDbAt(envelope, 1000), 6);
        Assert.Equal(0, TimelineMath.EnvelopeLevelDbAt(envelope, 2000), 6);
        Assert.Equal(0, TimelineMath.EnvelopeLevelDbAt(envelope, 9999), 6);
    }

    [Fact]
    public void EnvelopeLevelDbAt_InterpolatesInLinearGainWithTheSegmentCurve()
    {
        // Linear curve interpolates GAIN, not dB: midpoint of 0 dB → −60 dB (gain 1 → 0) is gain 0.5 = −6.02 dB.
        var down = new[] { Pt(0, 0), Pt(1000, CueAutomationPoint.SilenceLevelDb) };
        Assert.Equal(20 * Math.Log10(0.5), TimelineMath.EnvelopeLevelDbAt(down, 500), 3);

        // EqualPower rising midpoint: gain = sin(π/4) ≈ 0.7071 → −3.01 dB.
        var up = new[] { Pt(0, CueAutomationPoint.SilenceLevelDb, CueFadeCurve.EqualPower), Pt(1000, 0) };
        Assert.Equal(20 * Math.Log10(Math.Sin(Math.PI / 4)), TimelineMath.EnvelopeLevelDbAt(up, 500), 3);

        // Falling segments shape the mirrored progress (FadeCurves.LevelBetween): Exponential at
        // t=0.25 of 0 → −60 dB is gain (1−0.25)³ = 0.421875.
        var expDown = new[] { Pt(0, 0, CueFadeCurve.Exponential), Pt(1000, CueAutomationPoint.SilenceLevelDb) };
        Assert.Equal(20 * Math.Log10(0.421875), TimelineMath.EnvelopeLevelDbAt(expDown, 250), 3);
    }

    // ---- hit-testing ----

    [Fact]
    public void EnvelopePointHit_PicksNearestWithinRadius()
    {
        var envelope = new[] { Pt(0, 0), Pt(10_000, -6), Pt(10_060, -6) };
        var second = TimelineMath.EnvelopePointCenter(Block, envelope[1], PxPerMs);

        Assert.Equal(1, TimelineMath.EnvelopePointHit(Block, envelope, PxPerMs, new Point(second.X - 3, second.Y + 2)));
        // 6 px apart at this zoom - the nearer of the two wins.
        Assert.Equal(2, TimelineMath.EnvelopePointHit(Block, envelope, PxPerMs, new Point(second.X + 5, second.Y)));
        Assert.Equal(-1, TimelineMath.EnvelopePointHit(Block, envelope, PxPerMs, new Point(second.X, second.Y + 20)));
        Assert.Equal(-1, TimelineMath.EnvelopePointHit(Block, [], PxPerMs, second));
    }

    [Fact]
    public void EnvelopeLineHit_UsesVerticalDistanceAtPointerX()
    {
        // Empty envelope = the flat unity line; on it (within 5 px) hits, off it misses.
        var unityY = TimelineMath.EnvelopeYForDb(Block, 0);
        Assert.True(TimelineMath.EnvelopeLineHit(Block, [], PxPerMs, new Point(Block.X + 100, unityY + 3)));
        Assert.False(TimelineMath.EnvelopeLineHit(Block, [], PxPerMs, new Point(Block.X + 100, unityY + 12)));
        // Outside the block's x range never hits.
        Assert.False(TimelineMath.EnvelopeLineHit(Block, [], PxPerMs, new Point(Block.X - 10, unityY)));

        var envelope = new[] { Pt(0, 0), Pt(30_000, CueAutomationPoint.SilenceLevelDb) };
        var midY = TimelineMath.EnvelopeYForDb(Block, TimelineMath.EnvelopeLevelDbAt(envelope, 15_000));
        Assert.True(TimelineMath.EnvelopeLineHit(Block, envelope, PxPerMs, new Point(Block.X + 1500, midY - 2)));
        Assert.False(TimelineMath.EnvelopeLineHit(Block, envelope, PxPerMs, new Point(Block.X + 1500, midY - 30)));
    }

    // ---- sorted insert / drag clamp / segments ----

    [Fact]
    public void EnvelopeInsertIndex_KeepsTimeSortedOrder()
    {
        var envelope = new[] { Pt(1000, 0), Pt(2000, 0), Pt(3000, 0) };
        Assert.Equal(0, TimelineMath.EnvelopeInsertIndex(envelope, 500));
        Assert.Equal(1, TimelineMath.EnvelopeInsertIndex(envelope, 1000)); // after the equal-time point
        Assert.Equal(2, TimelineMath.EnvelopeInsertIndex(envelope, 2500));
        Assert.Equal(3, TimelineMath.EnvelopeInsertIndex(envelope, 9000));
        Assert.Equal(0, TimelineMath.EnvelopeInsertIndex([], 42));
    }

    [Fact]
    public void EnvelopeClampDragTime_StopsAtNeighborsAndTrimmedRange()
    {
        var envelope = new[] { Pt(1000, 0), Pt(2000, 0), Pt(3000, 0) };

        // Middle point: clamped between both neighbors (coincident times allowed = instant step).
        Assert.Equal(1000, TimelineMath.EnvelopeClampDragTime(envelope, 1, 200, 30_000));
        Assert.Equal(3000, TimelineMath.EnvelopeClampDragTime(envelope, 1, 9999, 30_000));
        Assert.Equal(2500, TimelineMath.EnvelopeClampDragTime(envelope, 1, 2500, 30_000));

        // First point: floor 0; last point: ceiling = the trimmed clip length.
        Assert.Equal(0, TimelineMath.EnvelopeClampDragTime(envelope, 0, -500, 30_000));
        Assert.Equal(30_000, TimelineMath.EnvelopeClampDragTime(envelope, 2, 99_000, 30_000));

        // A shrunken trim caps even between-neighbor drags.
        Assert.Equal(2500, TimelineMath.EnvelopeClampDragTime(envelope, 1, 2800, 2500));
    }

    [Fact]
    public void EnvelopeSegmentAt_ReturnsOwningPoint_FlatRegionsHaveNone()
    {
        var envelope = new[] { Pt(1000, 0), Pt(2000, -6), Pt(3000, 0) };
        Assert.Equal(-1, TimelineMath.EnvelopeSegmentAt(envelope, 500)); // flat lead-in
        Assert.Equal(0, TimelineMath.EnvelopeSegmentAt(envelope, 1000));
        Assert.Equal(0, TimelineMath.EnvelopeSegmentAt(envelope, 1999));
        Assert.Equal(1, TimelineMath.EnvelopeSegmentAt(envelope, 2500));
        Assert.Equal(-1, TimelineMath.EnvelopeSegmentAt(envelope, 3000)); // flat tail
        Assert.Equal(-1, TimelineMath.EnvelopeSegmentAt([], 0));
    }

    [Fact]
    public void NextCurve_CyclesAllFourShapes()
    {
        Assert.Equal(CueFadeCurve.EqualPower, TimelineMath.NextCurve(CueFadeCurve.Linear));
        Assert.Equal(CueFadeCurve.Exponential, TimelineMath.NextCurve(CueFadeCurve.EqualPower));
        Assert.Equal(CueFadeCurve.SCurve, TimelineMath.NextCurve(CueFadeCurve.Exponential));
        Assert.Equal(CueFadeCurve.Linear, TimelineMath.NextCurve(CueFadeCurve.SCurve));
    }

    [Fact]
    public void FormatDbLabel_SignsTheReadout()
    {
        Assert.Equal("+3.0 dB", TimelineMath.FormatDbLabel(3));
        Assert.Equal("-6.5 dB", TimelineMath.FormatDbLabel(-6.5));
        Assert.Equal("0.0 dB", TimelineMath.FormatDbLabel(0));
    }

    // ---- VM passthrough ----

    [Fact]
    public void VolumeEnvelope_SetRaisesChangeNotification_AndReachesTheSnapshot()
    {
        var vm = new CuePlayerViewModel();
        vm.AddEmptyMediaCue();
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        var raised = false;
        media.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(CueNodeViewModel.VolumeEnvelope);
        media.VolumeEnvelope = [Pt(500, -6, CueFadeCurve.SCurve)];
        Assert.True(raised); // the canvas re-renders (and lane subscribers refresh) off this

        // The edited list rides ToModel into the snapshot - what persistence and the mapper read.
        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        var point = Assert.Single(node.VolumeEnvelope);
        Assert.Equal(500, point.TimeMs);
        Assert.Equal(-6, point.LevelDb);
        Assert.Equal(CueFadeCurve.SCurve, point.CurveToNext);
    }
}
