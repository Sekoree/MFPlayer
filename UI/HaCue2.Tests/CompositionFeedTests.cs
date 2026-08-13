using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Sending a composition to an output, and seeing that it went.
/// </summary>
/// <remarks>
/// <para>
/// The reported defect: assigning an output to a composition appeared to do nothing. It reached the
/// document and it reached the FEEDS chips, but the canvas - the large picture that is the reason the
/// screen exists - went on drawing whatever screens the composition had when its pane was built.
/// <c>Refresh</c> updated every other projection on the pane and not that one, so the boxes were only
/// ever correct until the first assignment.
/// </para>
/// <para>
/// Driven through <see cref="VideoViewModel.AssignSelectedOutput"/> rather than by setting
/// <c>CompositionId</c> directly, because the defect was in the refresh that follows the edit and
/// writing the field by hand skips it.
/// </para>
/// </remarks>
public class CompositionFeedTests
{
    /// <summary>A canvas and an UNBOUND output, which is what the add-output dialog now produces.</summary>
    private static (VideoViewModel Video, CompositionDefinition Canvas, VideoOutputDefinition Output)
        Unassigned(ShellViewModel shell, VideoOutputKind kind = VideoOutputKind.LocalScreen)
    {
        shell.Project.Compositions.Clear();
        shell.Project.VideoOutputs.Clear();

        var canvas = new CompositionDefinition { Name = "Cyc", Width = 1920, Height = 1080 };
        var output = new VideoOutputDefinition { Name = "Projector", Kind = kind };

        shell.Project.Compositions.Add(canvas);
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedTab = video.CompositionsTab;
        video.SelectedCompositionId = canvas.Id;

        return (video, canvas, output);
    }

    private static CompositionPaneViewModel PaneOf(VideoViewModel video, CompositionDefinition canvas) =>
        video.Compositions.Single(pane => pane.Id == canvas.Id);

    [Fact]
    public Task AssignmentReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);

        Assert.True(video.CanAssignOutput);
        video.AssignSelectedOutput();

        Assert.Equal(canvas.Id, output.CompositionId);
    });

    [Fact]
    public Task AnAssignedScreenAppearsOnTheCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);

        Assert.Empty(PaneOf(video, canvas).OutputBoxes);

        video.AssignSelectedOutput();

        // THE defect. Everything else about the assignment worked; this is the part an operator was
        // looking at while deciding it had not.
        var box = Assert.Single(PaneOf(video, canvas).OutputBoxes);
        Assert.Equal(output.Id, box.SubjectId);
        Assert.Equal(output.Name, box.Label);
    });

    [Fact]
    public Task NewlyAssignedScreensDefaultToTheirRasterSize() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Compositions.Clear();
        shell.Project.VideoOutputs.Clear();
        var canvas = new CompositionDefinition { Name = "Wall", Width = 3840, Height = 1080 };
        var left = new VideoOutputDefinition
        {
            Name = "Left", MappingWidth = 1920, MappingHeight = 1080,
        };
        var right = new VideoOutputDefinition
        {
            Name = "Right", MappingWidth = 1920, MappingHeight = 1080,
        };
        shell.Project.Compositions.Add(canvas);
        shell.Project.VideoOutputs.Add(left);
        shell.Project.VideoOutputs.Add(right);
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedTab = video.CompositionsTab;
        video.SelectedCompositionId = canvas.Id;

        video.AssignableIndex = 0;
        video.AssignSelectedOutput();
        video.AssignableIndex = 0;
        video.AssignSelectedOutput();

        Assert.Equal(0.5, Assert.Single(left.Mapping).SourceWidth, 6);
        Assert.Equal(0, left.Mapping[0].SourceX, 6);
        Assert.Equal(0.5, Assert.Single(right.Mapping).SourceWidth, 6);
        Assert.Equal(0.5, right.Mapping[0].SourceX, 6);
    });

    [Fact]
    public Task AnAssignedOutputAppearsUnderFeeds() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);

        video.AssignSelectedOutput();

        Assert.Equal(output.Name, Assert.Single(PaneOf(video, canvas).Feeds).Name);
        Assert.Equal(output.Name, Assert.Single(video.SelectedCompositionFeeds).Name);
    });

    [Fact]
    public Task UnassigningTakesItBackOffTheCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);
        video.AssignSelectedOutput();

        video.UnassignOutput(output.Id);

        // The same refresh, in the other direction: a screen removed from a canvas that went on being
        // drawn on it is the same bug wearing the opposite sign.
        Assert.Empty(PaneOf(video, canvas).OutputBoxes);
        Assert.Empty(PaneOf(video, canvas).Feeds);
    });

    [Fact]
    public Task TheHeaderStopsSayingNothingShowsTheCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _) = Unassigned(shell);

        Assert.Contains("no output shows this canvas", PaneOf(video, canvas).LayoutSummary,
            StringComparison.Ordinal);

        video.AssignSelectedOutput();

        // Computed off the document, so nothing else would have told it to re-read.
        Assert.DoesNotContain("no output shows this canvas", PaneOf(video, canvas).LayoutSummary,
            StringComparison.Ordinal);
    });

    [Fact]
    public Task ASenderIsCountedEvenThoughItDrawsNoBox() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _) = Unassigned(shell, VideoOutputKind.Ndi);

        video.AssignSelectedOutput();

        var pane = PaneOf(video, canvas);

        // An NDI sender takes the WHOLE canvas, so drawing it would put a box over everything and hide
        // the overlaps and gaps the layout exists to show. It still has to be counted: telling somebody
        // who has just assigned one that no output shows this canvas reads as an assignment that failed.
        Assert.Empty(pane.OutputBoxes);
        Assert.Single(pane.Feeds);
        Assert.DoesNotContain("no output shows this canvas", pane.LayoutSummary, StringComparison.Ordinal);
        Assert.Contains("whole canvas", pane.LayoutSummary, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheCanvasTheRailAssignsToIsMarkedOnScreen() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _) = Unassigned(shell);

        var second = new CompositionDefinition { Name = "Lobby", Width = 1920, Height = 1080 };
        shell.Project.Compositions.Add(second);
        video.Refresh();

        video.SelectedCompositionId = second.Id;
        video.Refresh();

        // Every pane is open at once and each edits itself, so without a mark nothing on screen said
        // which canvas the FEEDS rail belonged to - an operator could press + ASSIGN having just read
        // the name on a different pane.
        Assert.False(PaneOf(video, canvas).IsSelected);
        Assert.True(PaneOf(video, second).IsSelected);
    });

    [Fact]
    public Task TheRailNamesTheCanvasItAssignsTo() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _) = Unassigned(shell);

        // Every pane on the left is open at once, so "this composition" named nothing.
        Assert.Contains(canvas.Name, video.FeedsHint, StringComparison.Ordinal);
    });

    // ── one layout editor, on the composition ─────────────────────────────────────────────────

    [Fact]
    public Task ClickingAScreenSelectsItAndItsCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);
        video.AssignSelectedOutput();
        video.SelectedCompositionId = null;

        video.SelectScreen(canvas.Id, 0);

        // Both halves: the canvas that was clicked on, and the screen inside it. The rail beside the
        // panes describes one output, and six panes can be open at once.
        Assert.Equal(canvas.Id, video.SelectedCompositionId);
        Assert.Equal(output.Id, video.SelectedOutput?.Id);
        Assert.True(video.HasScreenSelected);
        Assert.Contains(output.Name, PaneOf(video, canvas).SelectionNote, StringComparison.Ordinal);
    });

    [Fact]
    public Task TypingASliceWritesItLikeADragDoes() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);
        video.AssignSelectedOutput();
        video.SelectScreen(canvas.Id, 0);

        video.SliceWidth = 0.5;

        // A canvas alone cannot hit exactly half, which is the number a two-projector wall is made of.
        // Written through the same path a drag takes, including creating the section an output showing
        // the whole canvas does not have yet.
        var section = Assert.Single(output.Mapping);
        Assert.Equal(0.5, section.SourceWidth, 6);
        Assert.Equal(0.5, video.SliceWidth, 6);

        // And it lands full-frame on its own screen, which is what dividing a canvas means.
        Assert.Equal(0, section.TargetX, 6);
        Assert.Equal(1, section.TargetWidth, 6);
    });

    [Fact]
    public Task ATypedSliceIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, output) = Unassigned(shell);
        video.AssignSelectedOutput();
        video.SelectScreen(canvas.Id, 0);

        video.SliceWidth = 0.5;
        shell.Undo();

        // Assignment now authors the resolution-sized default. Undoing the typed adjustment returns
        // to that default rather than deleting a section that already existed.
        var section = Assert.Single(output.Mapping);
        Assert.Equal(1, section.SourceWidth, 6);
    });

    [Fact]
    public Task TheSliceFieldsHaveNoSubjectWithoutAScreen() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Unassigned(shell);

        // Unassigned, so it is not showing a slice of anything - the rail says so rather than offering
        // four numbers that write nowhere.
        Assert.False(video.HasScreenSelected);
    });

    // ── the seams reach the cue that has to land on them ──────────────────────────────────────

    /// <summary>A canvas split down the middle between two projectors, and a cue placed on it.</summary>
    private static (VideoViewModel Video, CompositionDefinition Wall, MediaCueNode Cue) SplitWall(
        ShellViewModel shell)
    {
        shell.Project.Compositions.Clear();
        shell.Project.VideoOutputs.Clear();

        var wall = new CompositionDefinition { Name = "Wall", Width = 3840, Height = 1080 };
        var left = new VideoOutputDefinition { Name = "Projector L", CompositionId = wall.Id };
        var right = new VideoOutputDefinition { Name = "Projector R", CompositionId = wall.Id };

        shell.Project.Compositions.Add(wall);
        shell.Project.VideoOutputs.Add(left);
        shell.Project.VideoOutputs.Add(right);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.5, 1)));
        video.ApplyLayoutGesture(new PlacementGesture(1, right.Id, 0, new NormalizedRect(0.5, 0, 0.5, 1)));
        video.EndGesture();

        var cue = ShellFixture.Bed(shell.Project);
        cue.Placements.Clear();
        cue.Placements.Add(new LayerPlacement { CompositionId = wall.Id, LayerIndex = 0 });

        ShellFixture.Select(shell.Cues, cue.Id);
        shell.Cues.Refresh();

        return (video, wall, cue);
    }

    [Fact]
    public Task ACuePlacementSnapsToTheSeamBetweenTwoProjectors() => ShellFixture.WithShell(shell =>
    {
        var (_, _, _) = SplitWall(shell);

        // The whole point of dividing a canvas: a cue can be dropped exactly onto ONE screen of a wall
        // without anybody working out what fraction that is. The pane built these seams from the start
        // and nothing consumed them - the canvas an operator drags a picture on never saw them.
        Assert.Contains(0.5, shell.Cues.Inspector.PlacementGuidesX);
    });

    [Fact]
    public Task APlacementDragPublishesOnlyAfterPointerRelease() => ShellFixture.WithShell(shell =>
    {
        var (_, _, cue) = SplitWall(shell);
        var inspector = shell.Cues.Inspector;
        var changes = 0;
        shell.Journal.Changed += () => changes++;

        inspector.ApplyPlacementGesture(
            new PlacementGesture(0, cue.Id, 0, new NormalizedRect(0.1, 0.1, 0.8, 0.8)));
        inspector.ApplyPlacementGesture(
            new PlacementGesture(0, cue.Id, 0, new NormalizedRect(0.2, 0.1, 0.8, 0.8)));

        Assert.Equal(0, changes);
        inspector.EndPlacementGesture();
        Assert.Equal(1, changes);
    });

    [Fact]
    public Task TheGuidesFollowTheCanvasThePlacementIsOn() => ShellFixture.WithShell(shell =>
    {
        var (_, _, cue) = SplitWall(shell);

        var lobby = new CompositionDefinition { Name = "Lobby", Width = 1920, Height = 1080 };
        shell.Project.Compositions.Add(lobby);
        cue.Placements.Add(new LayerPlacement { CompositionId = lobby.Id, LayerIndex = 1 });
        shell.Cues.Refresh();

        var inspector = shell.Cues.Inspector;
        inspector.ExpandPlacement(0);
        Assert.Contains(0.5, inspector.PlacementGuidesX);

        inspector.ExpandPlacement(1);

        // A cue can be on several canvases at once. Offering the wall's seam while editing the lobby
        // placement would snap it to a join that is not on that screen - worse than no guide at all.
        Assert.DoesNotContain(0.5, inspector.PlacementGuidesX);
    });

    [Fact]
    public Task ASenderContributesNoSeam() => ShellFixture.WithShell(shell =>
    {
        var (video, wall, _) = SplitWall(shell);

        shell.Project.VideoOutputs.Add(
            new VideoOutputDefinition { Name = "Stream", Kind = VideoOutputKind.Ndi, CompositionId = wall.Id });
        video.Refresh();

        // It takes the WHOLE canvas, so its edges are the canvas's own - already snap targets. Counting
        // it would only add duplicates, which is what the view-model's own copy of this used to do.
        Assert.Equal([0, 0.5, 1], shell.Cues.Inspector.PlacementGuidesX);
    });

    [Fact]
    public Task TheCompositionPaneDrawsTheOnlyLayoutCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Unassigned(shell);
        video.AssignSelectedOutput();

        var view = new VideoView { DataContext = video };
        var window = new Window { Width = 1600, Height = 950, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var canvases = view.GetVisualDescendants()
            .OfType<PlacementCanvas>()
            .Where(item => item.IsEffectivelyVisible)
            .ToList();

        // ONE, and it takes drags. There used to be two: a read-only illustration on the pane and a
        // full-screen editor behind an EDIT › button drawing the same rectangles, which is what made
        // the screen unreadable - the one an operator was looking at was the one that did nothing.
        var canvas = Assert.Single(canvases);
        Assert.True(canvas.IsEditable);
        Assert.Single(canvas.Boxes);

        window.Close();
    });
}
