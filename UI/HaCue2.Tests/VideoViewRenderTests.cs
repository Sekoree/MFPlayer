using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The Video view as actually rendered.
/// </summary>
/// <remarks>
/// The view-model tests prove the panes EDIT; these prove the markup in front of them loads, binds and
/// puts the controls on screen. They are different failures — a numeric field bound to a property that
/// does not exist compiles cleanly and simply shows nothing — and this view was rebuilt around a new
/// tab order, so "is the pane an operator is looking for even reachable" is the question worth asking.
/// </remarks>
public class VideoViewRenderTests
{
    private static Window Host(Control view, object dataContext)
    {
        view.DataContext = dataContext;

        var window = new Window { Width = 1600, Height = 950, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static (VideoViewModel Video, VideoOutputDefinition Output, CompositionDefinition Composition)
        Rig(ShellViewModel shell)
    {
        var composition = new CompositionDefinition { Name = "Cyc", Width = 1920, Height = 1080 };
        var output = new VideoOutputDefinition
        {
            Name = "Projector A",
            Kind = VideoOutputKind.LocalScreen,
            CompositionId = composition.Id,
            Mapping = [new MappingSection { Name = "Left wall" }],
        };

        shell.Project.Compositions.Add(composition);
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedOutput = video.Outputs.Single(row => row.Id == output.Id);

        return (video, output, composition);
    }

    /// <summary>Outputs come FIRST, because that is what an operator makes first.</summary>
    [Fact]
    public Task TheViewOpensOnOutputs() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Rig(shell);

        Assert.True(video.IsOutputsPane);
        Assert.Equal(
            ["OUTPUTS", "COMPOSITIONS", "MAPPING", "AUDITION"],
            video.Tabs.Select(tab => tab.Key.ToUpperInvariant()));
    });

    [Fact]
    public Task TheOutputsPaneRendersTheDocumentsOwnRows() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Rig(shell);
        var window = Host(new VideoView(), video);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();

        Assert.Contains("Projector A", text);
        Assert.Contains("Cyc", text);
    });

    /// <summary>
    /// The mapping pane's numeric fields are on screen and carry the section's values.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the rebuild: the geometry used to be "%"-suffixed text boxes parsed by
    /// stripping non-digits, which cannot express a destination in output pixels and rounds away the
    /// exactness an edge blend is made of.
    /// </remarks>
    [Fact]
    public Task TheMappingPaneRendersNumericFieldsForTheSelectedSection() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = Rig(shell);

        output.Mapping[0].SourceWidth = 0.5;
        output.Mapping[0].TargetWidth = 1;
        video.SelectedTab = video.MappingTab;
        video.Refresh();

        var window = Host(new VideoView(), video);

        var values = window.GetVisualDescendants().OfType<NumericUpDown>()
            .Where(box => box.IsEffectivelyVisible)
            .Select(box => box.Value)
            .ToList();

        // Source in fractions of the canvas…
        Assert.Contains(0.5m, values);
        // …destination in pixels of the output raster, which with no raster stated is the composition's.
        Assert.Contains(1920m, values);
    });

    /// <summary>The splitter is reachable, and pressing it is what an operator does first to a wall.</summary>
    [Fact]
    public Task TheSplitterIsOnTheMappingPane() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Rig(shell);
        video.SelectedTab = video.MappingTab;

        var window = Host(new VideoView(), video);

        var split = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .FirstOrDefault(button => (button.Content as string) == "SPLIT");

        Assert.NotNull(split);
    });

    /// <summary>
    /// The composition pane names the outputs its canvas feeds, and offers the rest.
    /// </summary>
    /// <remarks>
    /// A composition feeding nothing looks identical to one feeding three projectors, which is the most
    /// expensive thing to discover at a get-in.
    /// </remarks>
    [Fact]
    public Task TheCompositionPaneNamesWhatItFeeds() => ShellFixture.WithShell(shell =>
    {
        var (video, output, composition) = Rig(shell);

        shell.Project.VideoOutputs.Add(new VideoOutputDefinition { Name = "Lobby TV" });

        video.SelectedTab = video.CompositionsTab;
        video.SelectedCompositionId = composition.Id;
        video.Refresh();

        Assert.Equal(output.Name, Assert.Single(video.SelectedCompositionFeeds).Name);
        Assert.Contains("Lobby TV", video.AssignableOutputs);

        var window = Host(new VideoView(), video);

        var text = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .ToList();

        Assert.Contains("Projector A", text);
    });

    /// <summary>
    /// Every list an operator deletes from carries a context menu.
    /// </summary>
    /// <remarks>
    /// Adding was reachable and removing was not: the outputs table had no menu at all, so an output
    /// created by a mis-click stayed in the show for the life of the project.
    /// </remarks>
    [Fact]
    public Task TheOutputsTableHasARemoveMenu() => ShellFixture.WithShell(shell =>
    {
        var (video, _, _) = Rig(shell);
        var window = Host(new VideoView(), video);

        var menus = window.GetVisualDescendants().OfType<ListBox>()
            .Where(list => list.IsEffectivelyVisible)
            .Select(list => list.ContextMenu)
            .OfType<ContextMenu>()
            .SelectMany(menu => menu.Items.OfType<MenuItem>())
            .Select(item => item.Header as string)
            .ToList();

        Assert.Contains("Remove…", menus);
        Assert.Contains("Rename…", menus);
    });
}
