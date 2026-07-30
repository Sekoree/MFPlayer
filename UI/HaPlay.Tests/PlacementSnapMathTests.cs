using Avalonia;
using HaPlay.Views.Controls;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Drag geometry for the two normalized-rectangle editors (video placements in a composition,
/// composition sections in an output). Pure math, so the rules are pinned here rather than by dragging:
/// snapping pulls edges/centre onto the canvas guides, and a rect may be moved fully OUT of bounds - which
/// the previous hard clamp to [0, 1-size] made impossible, so a lower-third could not be parked off-canvas
/// and a composition could not be letterboxed inside a mismatched output.</summary>
public sealed class PlacementSnapMathTests
{
    private static readonly Rect Canvas = new(0, 0, 700, 394); // a realistic editor canvas

    [Fact]
    public void Threshold_IsAPixelDistance_SoThePullFeelsTheSameOnAnySizedCanvas()
    {
        // Same 7 px pull = a bigger normalized threshold on a small canvas. A fixed normalized threshold
        // (the obvious shortcut) would feel sticky on a small canvas and dead on a large one.
        Assert.Equal(PlacementSnapMath.SnapPixels / 700, PlacementSnapMath.Threshold(700), 9);
        Assert.True(PlacementSnapMath.Threshold(200) > PlacementSnapMath.Threshold(2000));
        // Degenerate canvas disables snapping instead of dividing by zero.
        Assert.Equal(0, PlacementSnapMath.Threshold(0));
    }

    [Theory]
    // near the left edge -> flush left
    [InlineData(0.004, 0.25, 0.0)]
    // near centre alignment (centre of a 0.25-wide rect at 0.5 => x = 0.375)
    [InlineData(0.372, 0.25, 0.375)]
    // near the right edge -> flush right (x = 1 - 0.25)
    [InlineData(0.748, 0.25, 0.75)]
    public void SnapMoveAxis_PullsTheNearestAnchorOntoAGuide(double x, double w, double expected)
    {
        Assert.Equal(expected, PlacementSnapMath.SnapMoveAxis(x, w, PlacementSnapMath.Threshold(700), true), 6);
    }

    [Fact]
    public void SnapMoveAxis_LeavesAPositionAloneWhenNothingIsClose_OrWhenDisabled()
    {
        var threshold = PlacementSnapMath.Threshold(700);
        Assert.Equal(0.31, PlacementSnapMath.SnapMoveAxis(0.31, 0.25, threshold, true), 6);
        // Disabled: even a position sitting right on a guide is passed through untouched.
        Assert.Equal(0.004, PlacementSnapMath.SnapMoveAxis(0.004, 0.25, threshold, enabled: false), 6);
    }

    [Fact]
    public void ClampMoveAxis_AllowsFullyOutOfBounds_BothWays_ButNoFurther()
    {
        // Fully off the leading edge is exactly -size, fully off the trailing edge exactly 1.
        Assert.Equal(-0.25, PlacementSnapMath.ClampMoveAxis(-0.25, 0.25), 6);
        Assert.Equal(1.0, PlacementSnapMath.ClampMoveAxis(1.0, 0.25), 6);
        // Partly out of bounds - the case the old clamp made unreachable - is untouched.
        Assert.Equal(-0.1, PlacementSnapMath.ClampMoveAxis(-0.1, 0.25), 6);
        Assert.Equal(0.9, PlacementSnapMath.ClampMoveAxis(0.9, 0.25), 6);
        // …and it stops there, so a rect dragged hard past the edge stays retrievable.
        Assert.Equal(-0.25, PlacementSnapMath.ClampMoveAxis(-5, 0.25), 6);
        Assert.Equal(1.0, PlacementSnapMath.ClampMoveAxis(5, 0.25), 6);
    }

    [Fact]
    public void SnapResizeAxis_SnapsTheTrailingEdgeToAGuide_AndKeepsTheSizeUsable()
    {
        var threshold = PlacementSnapMath.Threshold(700);
        // Origin 0.25, dragging the trailing edge to ~0.5 (the centre guide) => size 0.25.
        Assert.Equal(0.25, PlacementSnapMath.SnapResizeAxis(0.25, 0.2485, threshold, true), 6);
        // …and to the far edge => size 0.75.
        Assert.Equal(0.75, PlacementSnapMath.SnapResizeAxis(0.25, 0.7488, threshold, true), 6);
        // Oversize past the canvas is allowed (crop-to-fill) up to the sane cap.
        Assert.Equal(1.4, PlacementSnapMath.SnapResizeAxis(0.0, 1.4, threshold, enabled: false), 6);
        Assert.Equal(PlacementSnapMath.MaxSize, PlacementSnapMath.SnapResizeAxis(0, 99, threshold, false), 6);
        // A collapse is refused - a zero-sized rect cannot be grabbed again.
        Assert.Equal(PlacementSnapMath.MinSize, PlacementSnapMath.SnapResizeAxis(0, 0, threshold, false), 6);
    }

    [Fact]
    public void SnapAndClampMove_CentresARect_WhichIsHowLetterboxingIsAuthored()
    {
        // A 16:9 composition inside a 4:3 output: the operator drags it roughly to the middle and the
        // centre guide parks it EXACTLY there, which is what makes the black bars even.
        const double w = 1.0, h = 0.75;
        var p = PlacementSnapMath.SnapAndClampMove(0.002, 0.123, w, h, Canvas, snap: true);
        Assert.Equal(0.0, p.X, 6);
        Assert.Equal(0.125, p.Y, 6); // (1 - 0.75) / 2 - dead centre vertically
    }

    [Theory]
    // dragged left / up, partly out
    [InlineData(-0.2, -0.2)]
    // dragged right / down, partly out
    [InlineData(0.9, 0.9)]
    // fully out in both axes - the far limit
    [InlineData(-0.25, -0.25)]
    public void AnOutOfBoundsRect_KeepsItsSize_ItOnlyMOVES(double x, double y)
    {
        // The symptom that made this feature unusable: dragging out of bounds LOOKED like the box being
        // resized right/down and doing nothing left/up. Nothing was ever resized - the box kept its size and
        // moved, but the canvas filled the whole control, so the part outside was clipped at the control edge
        // and what remained visible was simply narrower. The editors now reserve a work-area margin and ghost
        // the overhang; this pins the invariant the visuals were misrepresenting.
        const double w = 0.25, h = 0.25;
        var p = PlacementSnapMath.SnapAndClampMove(x, y, w, h, Canvas, snap: false);

        Assert.Equal(x, p.X, 6);
        Assert.Equal(y, p.Y, 6);
        // Size is not a function of position anywhere in this math - a move cannot change it.
        Assert.Equal(w, PlacementSnapMath.SnapResizeAxis(p.X, w, PlacementSnapMath.Threshold(Canvas.Width), false), 6);
    }

    [Fact]
    public void SnapDoesNotPreventLeavingTheCanvas_OnceThePointerClearsTheThreshold()
    {
        // The "left/up does nothing" half: at x=0 the left edge sits ON a guide, so a small drag is pulled
        // back - correct snapping, and why Ctrl exists. What must NOT happen is being pinned there: a drag
        // beyond the threshold has to go negative.
        var threshold = PlacementSnapMath.Threshold(Canvas.Width);
        Assert.Equal(0.0, PlacementSnapMath.SnapMoveAxis(-threshold / 2, 0.25, threshold, true), 6);
        var escaped = PlacementSnapMath.SnapMoveAxis(-threshold * 3, 0.25, threshold, true);
        Assert.True(escaped < 0, $"a drag well past the snap threshold stayed pinned at {escaped}");
        // …and with snapping off it leaves immediately.
        Assert.Equal(-threshold / 2, PlacementSnapMath.SnapMoveAxis(-threshold / 2, 0.25, threshold, false), 6);
    }

    [Fact]
    public void ActiveGuides_ReportOnlyTheGuidesTheRectIsActuallySittingOn()
    {
        // Flush left, vertically centred.
        var (xs, ys) = PlacementSnapMath.ActiveGuides(0.0, 0.125, 1.0, 0.75, Canvas);
        Assert.Contains(0.0, xs);
        Assert.Contains(0.5, ys);
        // A rect parked well away from every guide reports none - the editors must not draw phantom lines.
        var (noneX, noneY) = PlacementSnapMath.ActiveGuides(0.31, 0.31, 0.2, 0.2, Canvas);
        Assert.Empty(noneX);
        Assert.Empty(noneY);
    }
}
