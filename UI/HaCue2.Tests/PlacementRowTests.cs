using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Carrying a cue on several canvases without scrolling past all of them.
/// </summary>
/// <remarks>
/// A placement holds geometry, a fit, an opacity, a crop, a chroma key and a colour adjust — the better
/// part of a screen each. Behind a picker over one editor, a cue on three canvases was three screens of
/// settings reachable only by remembering which entry in the picker was which.
/// </remarks>
public class PlacementRowTests
{
    private static MediaCueNode WithPlacements(ShellViewModel shell, int count)
    {
        var cue = ShellFixture.Bed(shell.Project);
        cue.Placements.Clear();

        for (var index = 0; index < count; index++)
        {
            cue.Placements.Add(new LayerPlacement
            {
                CompositionId = shell.Project.Compositions[0].Id,
                LayerIndex = index,
            });
        }

        ShellFixture.Select(shell.Cues, cue.Id);
        shell.Cues.Refresh();
        return cue;
    }

    [Fact]
    public Task EveryPlacementGetsItsOwnRow() => ShellFixture.WithShell(shell =>
    {
        WithPlacements(shell, 3);

        var headers = shell.Cues.Inspector.PlacementHeaders;

        Assert.Equal(3, headers.Count);
        Assert.Equal(["L0", "L1", "L2"], headers.Select(row => row.Layer));
        Assert.All(headers, row => Assert.Equal(shell.Project.Compositions[0].Name, row.Composition));
    });

    [Fact]
    public Task ExactlyOneRowIsOpen() => ShellFixture.WithShell(shell =>
    {
        WithPlacements(shell, 3);

        // One open row, because there is one editor below it to show.
        Assert.Single(shell.Cues.Inspector.PlacementHeaders, row => row.IsOpen);
    });

    [Fact]
    public Task OpeningARowIsWhatSelectsItForTheEditor() => ShellFixture.WithShell(shell =>
    {
        WithPlacements(shell, 3);
        var inspector = shell.Cues.Inspector;

        inspector.ExpandPlacement(2);

        Assert.Equal(2, inspector.SelectedPlacement);
        Assert.True(inspector.PlacementHeaders[2].IsOpen);
        Assert.False(inspector.PlacementHeaders[0].IsOpen);
    });

    [Fact]
    public Task AClosedRowSaysWhereItsPictureGoes() => ShellFixture.WithShell(shell =>
    {
        var cue = WithPlacements(shell, 2);
        cue.Placements[1].X = 0.5;
        cue.Placements[1].Width = 0.5;
        cue.Placements[1].Opacity = 0.4;
        cue.Placements[1].ChromaKey = new ChromaKeySpec();
        shell.Cues.Refresh();

        var headers = shell.Cues.Inspector.PlacementHeaders;

        // The default placement reads as what it is rather than as four numbers.
        Assert.Equal("full frame", headers[0].Summary);

        // And a placement carrying optional stages says which, so a closed row is still informative.
        Assert.Contains("0.5", headers[1].Summary);
        Assert.Contains("opacity", headers[1].Summary);
        Assert.Contains("keyed", headers[1].Summary);
    });

    [Fact]
    public Task ASingleplacementNeedsNoChooser() => ShellFixture.WithShell(shell =>
    {
        WithPlacements(shell, 1);

        // The rows are a way through several; one placement is just the editor.
        Assert.False(shell.Cues.Inspector.HasSeveralPlacements);
    });

    [Fact]
    public Task TheRowsRenderInTheRealPane() => ShellFixture.WithShell(shell =>
    {
        WithPlacements(shell, 3);
        // The rows live on the VIDEO tab; every other pane is collapsed.
        shell.Cues.Inspector.SelectedTab = "VIDEO";

        var pane = new InspectorPane { DataContext = shell.Cues.Inspector };
        var window = new Window { Width = 420, Height = 950, Content = pane };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = pane.GetVisualDescendants().OfType<RadioButton>().ToList();

        // A row that binds correctly and never reaches the screen is the failure this catches.
        Assert.True(rows.Count >= 3, $"the pane realised {rows.Count} placement row(s)");
        Assert.Single(rows, row => row.IsChecked == true);
    });
}
