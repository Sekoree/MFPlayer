using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Ducking a bed under a voice-over.
/// </summary>
/// <remarks>
/// An authoring helper: it writes ordinary keyframes, so the operator can see what will happen and fix
/// it by hand. Every test here is about the dip being one somebody would have drawn.
/// </remarks>
public sealed class DuckMathTests
{
    private const int BedLength = 60_000;

    [Fact]
    public void TouchingSpansDoNotOverlap()
    {
        // A voice-over starting exactly where the bed ends needs no duck.
        Assert.False(DuckMath.Overlaps(new TimelineSpan(0, 1000), new TimelineSpan(1000, 2000)));
        Assert.True(DuckMath.Overlaps(new TimelineSpan(0, 1001), new TimelineSpan(1000, 2000)));
    }

    [Fact]
    public void BackToBackVoiceOversMakeOneDip()
    {
        var merged = DuckMath.Merge(
        [
            new TimelineSpan(0, 1000),
            new TimelineSpan(1000, 2000),
            new TimelineSpan(5000, 6000),
        ]);

        // Two sentences with no gap must not let the bed bob up between them — that artefact is what
        // ducking exists to prevent.
        Assert.Equal(2, merged.Count);
        Assert.Equal(new TimelineSpan(0, 2000), merged[0]);
    }

    [Fact]
    public void ADuckWritesFourOrdinaryPoints()
    {
        var ducked = DuckMath.ApplyDucks(
            [], bedStartMs: 0, BedLength,
            [new TimelineSpan(20_000, 30_000)],
            depthDb: -12, rampMs: 500, leadMs: 250);

        // restore · depth · depth · restore — nothing an operator could not have dragged themselves.
        Assert.Equal(4, ducked.Count);
        Assert.True(ducked[1].Y < ducked[0].Y);
        Assert.Equal(ducked[1].Y, ducked[2].Y, 4);
        Assert.Equal(ducked[0].Y, ducked[3].Y, 4);
    }

    [Fact]
    public void ABedAlreadyRidingLowReturnsToWhereItWas()
    {
        // A bed sitting at half level must come back to half, not be yanked up to unity.
        IReadOnlyList<LanePoint> riding = [new(0, 0.5), new(1, 0.5)];

        var ducked = DuckMath.ApplyDucks(
            riding, 0, BedLength,
            [new TimelineSpan(20_000, 30_000)],
            depthDb: -6, rampMs: 500, leadMs: 0);

        var restore = ducked.First(point => point.X > 0.5);
        Assert.Equal(0.5, DuckMath.Sample(ducked, 0.95), 3);
        Assert.True(restore.Y <= 0.5001);
    }

    [Fact]
    public void PointsInsideTheDipAreReplacedAndOnesOutsideSurvive()
    {
        IReadOnlyList<LanePoint> existing =
        [
            new(0.05, 1),      // well before
            new(0.40, 0.9),    // inside the dip
            new(0.95, 1),      // well after
        ];

        var ducked = DuckMath.ApplyDucks(
            existing, 0, BedLength,
            [new TimelineSpan(20_000, 30_000)],
            depthDb: -12, rampMs: 500, leadMs: 0);

        Assert.Contains(ducked, point => Math.Abs(point.X - 0.05) < 0.0001);
        Assert.Contains(ducked, point => Math.Abs(point.X - 0.95) < 0.0001);
        Assert.DoesNotContain(ducked, point => Math.Abs(point.X - 0.40) < 0.0001);
    }

    [Fact]
    public void ADipIsSortedAndInsideTheClip()
    {
        var ducked = DuckMath.ApplyDucks(
            [], 0, BedLength,
            [new TimelineSpan(50_000, 70_000), new TimelineSpan(1_000, 3_000)],
            depthDb: -18, rampMs: 800, leadMs: 400);

        // The lane feeds an envelope the engine evaluates left to right, and every x is a fraction.
        Assert.Equal(ducked.OrderBy(point => point.X), ducked);
        Assert.All(ducked, point => Assert.InRange(point.X, 0, 1));
        Assert.All(ducked, point => Assert.InRange(point.Y, 0, 1));
    }

    [Fact]
    public void AVoiceOverAlreadyTalkingHoldsTheDepthFromTheEdge()
    {
        // The voice starts before the bed does: there is no "before" to restore from, so the dip has
        // to begin already down rather than ramping up from nothing.
        var ducked = DuckMath.ApplyDucks(
            [], bedStartMs: 10_000, BedLength,
            [new TimelineSpan(0, 20_000)],
            depthDb: -12, rampMs: 500, leadMs: 0);

        Assert.True(ducked[0].X <= 0.0001);
        Assert.True(ducked[0].Y < 0.9);
    }

    [Fact]
    public void AVoiceOverThatDoesNotOverlapChangesNothing()
    {
        IReadOnlyList<LanePoint> original = [new(0, 1), new(1, 1)];

        var ducked = DuckMath.ApplyDucks(
            original, 0, BedLength,
            [new TimelineSpan(80_000, 90_000)],
            depthDb: -12, rampMs: 500, leadMs: 0);

        // The SAME list back, not a copy: nothing happened, so nothing should look like an edit.
        Assert.Same(original, ducked);
    }

    [Fact]
    public void SilenceIsSilenceRatherThanAlmost()
    {
        var ducked = DuckMath.ApplyDucks(
            [], 0, BedLength,
            [new TimelineSpan(20_000, 30_000)],
            depthDb: GainRange.SilenceFloorDb, rampMs: 100, leadMs: 0);

        Assert.Equal(0, ducked[1].Y, 6);
    }

    [Fact]
    public void SamplingAnEmptyLaneIsUnity()
    {
        // No automation means no attenuation — the honest reading of "nobody drew anything".
        Assert.Equal(1, DuckMath.Sample([], 0.5));
    }

    [Fact]
    public void RestoreSamplingUsesTheAuthoredBezierRatherThanAChord()
    {
        IReadOnlyList<LanePoint> curve =
        [
            new(0, 0, OutHandleX: 0, OutHandleY: 1),
            new(1, 0, InHandleX: 1, InHandleY: 1),
        ];

        Assert.Equal(0.75, DuckMath.Sample(curve, 0.5), 4);
    }
}
