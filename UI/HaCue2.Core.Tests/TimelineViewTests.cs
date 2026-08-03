using HaCue2.Presentation;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The timeline's view window: what ZOOM ± and FIT move.
/// </summary>
/// <remarks>
/// Every rule here is about not losing the thing the operator is looking at. Zooming about the left
/// edge walks it off the screen; letting the window drift past the group's end leaves the lanes
/// squashed into part of a sheet with nothing beside them.
/// </remarks>
public class TimelineViewTests
{
    private const double TenMinutes = 600_000;

    [Fact]
    public void FitIsTheWholeGroup()
    {
        var view = TimelineView.Whole(TenMinutes);

        Assert.Equal(0, view.StartMs);
        Assert.Equal(TenMinutes, view.LengthMs);
        Assert.Equal(0, view.Fraction(0));
        Assert.Equal(1, view.Fraction(TenMinutes));
    }

    [Fact]
    public void ZoomingKeepsTheCentreOfTheScreenWhereItWas()
    {
        var view = TimelineView.Whole(TenMinutes).Zoom(0.5, TenMinutes);

        // Half the group, centred on where the middle of the screen was. Zooming about the LEFT edge
        // would take whatever the operator was looking at off the right of the sheet.
        Assert.Equal(TenMinutes / 2, view.LengthMs);
        Assert.Equal(TenMinutes / 4, view.StartMs, 3);
        Assert.Equal(0.5, view.Fraction(TenMinutes / 2), 6);
    }

    [Fact]
    public void ZoomingOutStopsAtTheWholeGroup()
    {
        var view = TimelineView.Whole(TenMinutes).Zoom(4, TenMinutes);

        // There is nothing beyond the group to show, and a window wider than it would draw the lanes
        // squashed into part of the sheet with empty space beside them.
        Assert.Equal(0, view.StartMs);
        Assert.Equal(TenMinutes, view.LengthMs);
    }

    [Fact]
    public void ZoomingInStopsAtHalfASecond()
    {
        var view = TimelineView.Whole(TenMinutes);

        for (var step = 0; step < 40; step++)
            view = view.Zoom(0.5, TenMinutes);

        // Already the snap grid. Past it there is nothing an operator could act on, and the ruler's
        // labels stop distinguishing one tick from the next.
        Assert.Equal(TimelineView.MinimumLengthMs, view.LengthMs);
    }

    [Fact]
    public void AWindowNeverStartsBeforeZeroOrRunsPastTheEnd()
    {
        var atTheEnd = new TimelineView(TenMinutes - 1_000, 1_000);

        var wider = atTheEnd.Zoom(8, TenMinutes);

        Assert.True(wider.StartMs >= 0);
        Assert.True(wider.StartMs + wider.LengthMs <= TenMinutes + 0.001);
    }

    [Fact]
    public void AClipOutsideTheWindowKeepsAnOutOfRangeFraction()
    {
        var view = new TimelineView(60_000, 30_000);

        // Not clamped: the lane draws nothing for it. Clamping would pile every off-screen cue against
        // the edges as a row of slivers that look exactly like real clips.
        Assert.True(view.Fraction(10_000) < 0);
        Assert.True(view.Fraction(200_000) > 1);
        Assert.Equal(0.5, view.Fraction(75_000), 6);
    }

    [Fact]
    public void AFractionAndAMomentAreTheSameQuestionBothWays()
    {
        var view = new TimelineView(60_000, 30_000);

        // The ruler converts one way (a click becomes a time) and the lanes the other. They have to
        // agree, or the playhead lands somewhere other than where it was drawn.
        Assert.Equal(75_000, view.At(0.5), 6);
        Assert.Equal(0.5, view.Fraction(view.At(0.5)), 6);
    }

    [Fact]
    public void AnEmptyGroupStillHasAWindowToDrawIn() =>
        // Dividing by a zero-length window is how every position becomes an infinity.
        Assert.Equal(TimelineView.MinimumLengthMs, TimelineView.Whole(0).LengthMs);
}
