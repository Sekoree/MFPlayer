using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Authoring an automation lane (register item 18).
/// </summary>
/// <remarks>
/// The model, the compile path and the curve editor's own <c>EffectLaneTarget</c> all existed before
/// this: what was missing was any way to ADD a lane, so lanes reached the engine only when the fixture
/// generator or the timeline's duck helper happened to write one. Same shape of gap as the
/// control-flow panes and the trigger bindings.
/// </remarks>
public class EffectLaneTests
{
    private static MediaCueNode SelectBed(ShellViewModel shell)
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);
        return bed;
    }

    [Fact]
    public Task ACueStartsWithNoLanes() => ShellFixture.WithShell(shell =>
    {
        SelectBed(shell);

        // Hidden until added: a cue showing four empty lanes would imply it has automation it does not.
        Assert.Empty(shell.Cues.Inspector.EffectLanes);
        Assert.False(shell.Cues.Inspector.HasEffectLanes);
    });

    [Fact]
    public Task AddingALaneReachesTheDocumentAndIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        var lane = Assert.Single(bed.EffectLanes);
        Assert.Equal(EffectLaneKind.Volume, lane.Kind);

        shell.Undo();
        Assert.Empty(((MediaCueNode)shell.Project.FindCue(bed.Id)!).EffectLanes);
    });

    [Fact]
    public Task ANewLaneOpensOnSomethingTheOperatorCanGrab() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        // Two points at unity: an editor opening on an empty list has no handles, and a flat lane at
        // unity changes nothing until it is dragged — so adding one is safe mid-show.
        var lane = bed.EffectLanes[0];
        Assert.Equal(2, lane.Points.Count);
        Assert.All(lane.Points, point => Assert.Equal(1, point.Y, 3));
    });

    [Fact]
    public Task OnlyOneLanePerKind() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        // A cue with two volume lanes has no defined level, and the compiler takes the first — so the
        // second would be invisible rather than additive.
        Assert.Single(bed.EffectLanes);
        Assert.False(shell.Cues.Inspector.CanAddLane((int)EffectLaneKind.Volume));
        Assert.True(shell.Cues.Inspector.CanAddLane((int)EffectLaneKind.Opacity));
    });

    [Fact]
    public Task DifferentKindsCoexist() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);

        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Opacity);

        Assert.Equal(2, bed.EffectLanes.Count);
    });

    [Fact]
    public Task ALaneCanBeRemoved() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        shell.Cues.Inspector.RemoveLane(0);

        Assert.Empty(bed.EffectLanes);
    });

    [Fact]
    public Task TheLaneEditorTargetsTheLanesOwnPoints() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        var editor = shell.Cues.Inspector.LaneEditor(0);
        Assert.NotNull(editor);

        // Drag the second point down. The SAME editor a fade curve uses — one sorted list of
        // normalized points, which is why the plan asks for one editor rather than two.
        editor!.Apply(new HaCue2.Controls.CurveGesture(
            HaCue2.Controls.CurveGestureKind.Move, 1, 1, 0.75));
        editor.EndGesture();

        Assert.Equal(2, bed.EffectLanes[0].Points.Count);
        // Canvas y is flipped, so a drag to 0.75 down the canvas is a level of 0.25.
        Assert.Equal(0.25, bed.EffectLanes[0].Points[1].Y, 2);
    });

    [Fact]
    public Task EditingALaneIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);

        var editor = shell.Cues.Inspector.LaneEditor(0)!;
        editor.Apply(new HaCue2.Controls.CurveGesture(
            HaCue2.Controls.CurveGestureKind.Move, 1, 1, 0.75));
        editor.EndGesture();

        shell.Undo();

        var lane = ((MediaCueNode)shell.Project.FindCue(bed.Id)!).EffectLanes[0];
        Assert.Equal(1, lane.Points[1].Y, 3);
    });

    [Fact]
    public Task ALaneWithTooFewPointsSaysSoRatherThanCountingThem() => ShellFixture.WithShell(shell =>
    {
        var bed = SelectBed(shell);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);
        bed.EffectLanes[0].Points = [new LanePoint(0, 1)];
        shell.Cues.Inspector.Reload();

        // A point count would imply the lane reaches the engine. It does not: the compiler needs more
        // than one point to build an envelope.
        Assert.Contains("needs at least two", shell.Cues.Inspector.EffectLanes[0].Detail, StringComparison.Ordinal);
    });

    [Fact]
    public Task AnOutboundLaneWithNoEndpointSaysNothingIsSent() => ShellFixture.WithShell(shell =>
    {
        SelectBed(shell);
        shell.Cues.Inspector.AddLane((int)EffectLaneKind.OscRamp);

        Assert.Contains(
            "nothing is sent",
            shell.Cues.Inspector.EffectLanes[0].Detail,
            StringComparison.Ordinal);
    });

    [Fact]
    public Task AGroupCanCarryLanesAndACommentCannot() => ShellFixture.WithShell(shell =>
    {
        var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
        ShellFixture.Select(shell.Cues, group.Id);
        Assert.True(shell.Cues.Inspector.CanCarryLanes);

        shell.Cues.Inspector.AddLane((int)EffectLaneKind.Volume);
        Assert.Single(group.EffectLanes);

        var comment = shell.Project.AllCues().OfType<CommentCueNode>().First();
        ShellFixture.Select(shell.Cues, comment.Id);

        // Nothing to automate on a marker. The button is disabled rather than adding a lane the
        // compiler would ignore.
        Assert.False(shell.Cues.Inspector.CanCarryLanes);
    });
}
