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

    [Fact]
    public Task EveryOutputOnTheCanvasGetsABox() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _, _) = Wall(shell);

        Assert.Equal(2, video.LayoutBoxes.Count);
        Assert.Equal(["Projector L", "Projector R"], video.LayoutBoxes.Select(box => box.Label));
    });

    [Fact]
    public Task AnUnmappedOutputShowsTheWholeCanvas() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _, _) = Wall(shell);
        var box = video.LayoutBoxes[0];

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
        video.Refresh();

        // This is what makes the layout worth more than a picture: a cue can be dropped exactly onto
        // one projector of a wall without anybody working out what fraction that is.
        Assert.Contains(0.5, video.LayoutGuidesX);

        var pane = video.Compositions.Single(item => item.Id == canvas.Id);
        Assert.Contains(0.5, pane.GuidesX);
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
    public Task TheEditorIsClosedUntilItIsOpened() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _, _) = Wall(shell);

        Assert.False(video.IsLayoutOpen);

        video.OpenLayout();

        // Opening it is also what takes you to the pane it belongs over.
        Assert.True(video.IsCompositionsPane);
        Assert.True(video.IsLayoutOpen);
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
        video.OpenLayout();

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
/// The composition editor saying what it is for.
/// </summary>
/// <remarks>
/// The fields sat disabled and blank whenever nothing was selected, which reads as a broken panel
/// rather than a form waiting for a subject — and gave no clue they EDIT the canvas rather than
/// describe it. There are two different empty states and they need different answers.
/// </remarks>
public class CompositionEditorTests
{
    [Fact]
    public Task WithNoCanvasAtAllItNamesTheWayOut() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Compositions.Clear();
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);

        Assert.False(video.IsEditingComposition);
        Assert.Contains("ADD", video.CompositionEditorHint);
    });

    [Fact]
    public Task WithNoIdChosenItFollowsTheFirstCanvas() => ShellFixture.WithShell(shell =>
    {
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedCompositionId = null;

        // "None picked" is not a state this editor has: a null id follows the first composition, so
        // the form always has a subject as long as one canvas exists.
        Assert.True(video.IsEditingComposition);
        Assert.Contains(shell.Project.Compositions[0].Name, video.CompositionEditorHint);
    });

    [Fact]
    public Task WithOnePickedItSaysWhatItIsEditing() => ShellFixture.WithShell(shell =>
    {
        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        var composition = shell.Project.Compositions[0];
        video.SelectedCompositionId = composition.Id;

        Assert.True(video.IsEditingComposition);
        Assert.Contains(composition.Name, video.CompositionEditorHint);
        Assert.Contains("editing", video.CompositionEditorHint);
        Assert.Contains("switch", video.CompositionEditorHint);
    });
}
