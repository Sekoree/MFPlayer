using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Dividing a canvas between the screens that show it.
/// </summary>
/// <remarks>
/// The capability HaPlay's cue player had and this did not: which PART of a composition each output
/// displays. It is a question about all of them at once — overlap is a blend zone and canvas nobody
/// covers is a gap — and neither is visible one output at a time, which is why it lives on the
/// composition rather than in the per-output mapping pane.
/// </remarks>
public class OutputLayoutTests
{
    private static (VideoViewModel Video, CompositionDefinition Canvas, VideoOutputDefinition Left,
        VideoOutputDefinition Right) Wall(ShellViewModel shell)
    {
        var canvas = new CompositionDefinition { Name = "Wall", Width = 3840, Height = 1080 };
        var left = new VideoOutputDefinition { Name = "Projector L", CompositionId = canvas.Id };
        var right = new VideoOutputDefinition { Name = "Projector R", CompositionId = canvas.Id };

        shell.Project.Compositions.Add(canvas);
        shell.Project.VideoOutputs.Add(left);
        shell.Project.VideoOutputs.Add(right);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedCompositionId = canvas.Id;
        return (video, canvas, left, right);
    }

    /// <summary>
    /// The canvas as it is actually drawn — the composition's own pane.
    /// </summary>
    /// <remarks>
    /// These used to read <c>video.LayoutBoxes</c>, a projection scoped to "the selected composition"
    /// that fed the full-screen layout overlay. The overlay is gone and every pane draws its own
    /// layout, so asserting on the view-model's copy would be testing a surface nobody can see —
    /// which is exactly how the pane's boxes went stale without a test noticing.
    /// </remarks>
    private static CompositionPaneViewModel Pane(VideoViewModel video, CompositionDefinition canvas) =>
        video.Compositions.Single(pane => pane.Id == canvas.Id);

    [Fact]
    public Task EveryOutputOnTheCanvasGetsABox() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _, _) = Wall(shell);

        Assert.Equal(2, Pane(video, canvas).OutputBoxes.Count);
        Assert.Equal(
            ["Projector L", "Projector R"],
            Pane(video, canvas).OutputBoxes.Select(box => box.Label));
    });

    [Fact]
    public Task AnUnmappedOutputShowsTheWholeCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _, _) = Wall(shell);
        var box = Pane(video, canvas).OutputBoxes[0];

        // The honest answer, and the one every output starts with.
        Assert.Equal(0, box.Left, 6);
        Assert.Equal(0, box.Top, 6);
        Assert.Equal(1, box.Width, 6);
        Assert.Equal(1, box.Height, 6);
    });

    [Fact]
    public Task DraggingAScreensEdgeWritesItsSliceIntoItsOwnMapping() => ShellFixture.WithShell(shell =>
    {
        var (video, _, left, _) = Wall(shell);

        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.5, 1)));
        video.EndGesture();

        // Written where the ENGINE reads it from: the mapping editor is the other view of the same
        // numbers, not a second place to author geometry.
        var section = Assert.Single(left.Mapping);
        Assert.Equal(0, section.SourceX, 6);
        Assert.Equal(0.5, section.SourceWidth, 6);

        // Identity destination: the slice fills the screen, which is what dividing a canvas means
        // before anybody warps anything.
        Assert.Equal(0, section.TargetX, 6);
        Assert.Equal(1, section.TargetWidth, 6);
    });

    [Fact]
    public Task ASliceStaysInsideTheCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, _, left, _) = Wall(shell);

        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0.9, 0, 0.5, 1)));
        video.EndGesture();

        // The opposite rule from a cue placement: a slice is a region OF the canvas, so a slice
        // outside the canvas is a crop of nothing.
        var section = left.Mapping[0];
        Assert.True(section.SourceX + section.SourceWidth <= 1.0001, "the slice left the canvas");
    });

    [Fact]
    public Task TheWholeDragIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var (video, _, left, _) = Wall(shell);

        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.7, 1)));
        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.6, 1)));
        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.5, 1)));
        video.EndGesture();

        shell.Journal.Undo();

        // A drag that undid in three steps could be walked back into a shape nobody ever saw.
        Assert.Empty(left.Mapping);
    });

    [Fact]
    public Task TheSliceEdgesBecomeSnapGuidesForCuePlacements() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, left, right) = Wall(shell);

        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.5, 1)));
        video.ApplyLayoutGesture(new PlacementGesture(1, right.Id, 0, new NormalizedRect(0.5, 0, 0.5, 1)));
        video.EndGesture();

        // What makes the layout worth more than a picture: a cue can be dropped exactly onto one
        // projector of a wall without anybody working out what fraction that is. Asserted on the ONE
        // source both readers share — the composition's layout and the inspector's cue-placement
        // canvas — because a seam an operator lines a picture up against and a seam the show actually
        // renders at have to be the same number. That it reaches the drag is CompositionFeedTests.
        Assert.Equal(
            [0, 0.5, 1],
            VideoPresentation.SliceGuides(shell.Project, canvas.Id, horizontal: true));
    });

    [Fact]
    public Task TheLayoutSaysWhatItAddsUpTo() => ShellFixture.WithShell(shell =>
    {
        var (video, _, left, _) = Wall(shell);

        Assert.Contains("whole canvas", video.LayoutSummary);

        video.ApplyLayoutGesture(new PlacementGesture(0, left.Id, 0, new NormalizedRect(0, 0, 0.5, 1)));
        video.EndGesture();

        Assert.DoesNotContain("each showing the whole canvas", video.LayoutSummary);
    });

    [Fact]
    public Task TheLayoutIsOnTheCompositionRatherThanBehindAButton() => ShellFixture.WithShell(shell =>
    {
        var (video, canvas, _, _) = Wall(shell);
        var pane = video.Compositions.Single(item => item.Id == canvas.Id);

        // There is no editor to OPEN any more. The composition's own pane draws the layout and takes
        // the drags, which is what the second surface behind an EDIT › button used to be for.
        Assert.Equal(2, pane.OutputBoxes.Count);
    });

    [Fact]
    public Task MappingOpensOverTheOutputsPaneRatherThanAsATab() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _, _) = Wall(shell);

        Assert.False(video.IsMappingOpen);

        video.OpenMapping();

        Assert.True(video.IsOutputsPane);
        Assert.True(video.IsMappingOpen);

        // And leaving the pane puts it away: it is an editor ON an output, not a place of its own.
        video.SelectedTab = video.CompositionsTab;
        Assert.False(video.IsMappingOpen);
    });

    [Fact]
    public Task TheLayoutEditorRendersItsCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _, _) = Wall(shell);
        video.SelectedTab = video.CompositionsTab;

        var view = new VideoView { DataContext = video };
        var window = new Window { Width = 1600, Height = 950, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var canvases = view.GetVisualDescendants()
            .OfType<PlacementCanvas>()
            .Where(item => item.IsEffectivelyVisible)
            .ToList();

        // A layout that binds correctly and never reaches the screen is the failure this catches.
        Assert.NotEmpty(canvases);
        Assert.Contains(canvases, item => item.Boxes.Count == 2);
    });
}

/// <summary>
/// Which canvas the FEEDS rail is assigning to.
/// </summary>
/// <remarks>
/// These used to cover a right-hand form that edited "the selected composition" — name, size, rate and
/// idle image. Those fields live on each composition's own pane now, so the form is gone and with it
/// the two empty states it needed. What survives is the rule underneath it, which the assignment rail
/// still depends on: a null id follows the FIRST composition, so the rail always has a subject as long
/// as one canvas exists, and "nothing picked" is not a state anybody can get stuck in.
/// </remarks>
public class CompositionSelectionTests
{
    [Fact]
    public Task WithNoCanvasAtAllThereIsNothingToAssignTo() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Compositions.Clear();
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);

        Assert.True(video.HasNoCompositions);
        Assert.False(video.HasComposition);
        Assert.False(video.CanAssignOutput);

        // And the outputs pane says which comes first, because the order is a real dependency and
        // nothing else on the screen states it.
        Assert.Contains("COMPOSITION first", video.OutputsEmptyDetail, StringComparison.Ordinal);
    });

    [Fact]
    public Task WithNoIdChosenItFollowsTheFirstCanvas() => ShellFixture.WithShell(shell =>
    {
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedCompositionId = null;

        Assert.True(video.HasComposition);
        Assert.Equal(shell.Project.Compositions[0].Name, video.CompositionHeader);
        Assert.Contains(shell.Project.Compositions[0].Name, video.FeedsHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task WithOnePickedTheRailNamesIt() => ShellFixture.WithShell(shell =>
    {
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        var composition = shell.Project.Compositions[^1];
        video.SelectedCompositionId = composition.Id;

        Assert.Equal(composition.Name, video.CompositionHeader);
        Assert.Contains(composition.Name, video.FeedsHint, StringComparison.Ordinal);
    });
}
