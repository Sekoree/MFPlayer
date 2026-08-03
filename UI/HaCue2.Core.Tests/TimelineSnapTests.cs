using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Controls;
using HaCue2.Session;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The timeline's snap/free toggle, driven the way a pointer drives it.
/// </summary>
/// <remarks>
/// Half a second is the right grid for laying a show out and the wrong one for landing a stab on a
/// frame. Both are asserted against the DOCUMENT rather than against the view-model, because what the
/// operator is actually asking is where the clip ends up in the file that gets saved.
/// </remarks>
public class TimelineSnapTests
{
    private static (TimelineViewModel Timeline, MediaCueNode Clip, HaCueProject Project) Sheet()
    {
        var clip = new MediaCueNode { Number = "1.1", Label = "Storm bed", MediaPath = "storm.wav" };

        var group = new GroupCueNode
        {
            Number = "1",
            Label = "Opening",
            FireMode = GroupFireMode.Timeline,
            Children = [clip],
        };

        var project = new HaCueProject
        {
            Title = "snap",
            CueLists = [new CueList { Name = "Act 1", Cues = [group] }],
        };

        var timeline = new TimelineViewModel(project, new ShowRuntime(), new ProjectJournal(project));
        return (timeline, clip, project);
    }

    /// <summary>A body drag to a fraction of the lane, as the clip control raises it.</summary>
    private static ClipGesture Drag(Guid subject, double left) =>
        new(Index: 0, subject, ClipEdge.Body, left, 0.2);

    [Fact]
    public void TheSheetSnapsByDefault()
    {
        var (timeline, clip, _) = Sheet();

        Assert.Equal("snap", timeline.SnapMode);
        Assert.True(timeline.IsSnapping);
        Assert.Contains("snap 0.5 s", timeline.Hint, StringComparison.Ordinal);

        timeline.ApplyClipGesture(Drag(clip.Id, 0.37));
        timeline.EndGesture();

        // Wherever the span put it, it landed on the grid. That is the whole promise the hint makes.
        Assert.Equal(0, clip.TimelineOffsetMs % 500);
    }

    [Fact]
    public void TurningSnappingOffLetsAClipLandBetweenTheGridLines()
    {
        var (timeline, clip, _) = Sheet();

        timeline.ApplyClipGesture(Drag(clip.Id, 0.37));
        timeline.EndGesture();
        var snapped = clip.TimelineOffsetMs;

        timeline.SnapMode = "free";
        Assert.False(timeline.IsSnapping);

        // A drag that is deliberately NOT a multiple of the grid. Under snapping it would round back
        // to where it already is, so a toggle that did nothing would look exactly like one that worked.
        timeline.ApplyClipGesture(Drag(clip.Id, 0.37 + (137d / 100_000)));
        timeline.EndGesture();

        Assert.NotEqual(snapped, clip.TimelineOffsetMs);
        Assert.NotEqual(0, clip.TimelineOffsetMs % 500);
    }

    [Fact]
    public void TheHintStopsPromisingAGridThatIsNoLongerInForce()
    {
        var (timeline, _, _) = Sheet();

        timeline.SnapMode = "free";

        // The sheet's hint is the only place the grid is stated. Leaving it reading "snap 0.5 s" over
        // free dragging would make the one visible explanation of the behaviour wrong.
        Assert.DoesNotContain("snap", timeline.Hint, StringComparison.Ordinal);
        Assert.Contains("free", timeline.Hint, StringComparison.Ordinal);
    }
}
