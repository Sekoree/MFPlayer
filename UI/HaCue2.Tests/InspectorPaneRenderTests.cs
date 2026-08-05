using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>User-visible regressions in the cue inspector.</summary>
public class InspectorPaneRenderTests
{
    private static (InspectorPane Pane, Window Window) Show(object dataContext)
    {
        var pane = new InspectorPane { DataContext = dataContext };
        var window = new Window { Width = 420, Height = 700, Content = pane };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (pane, window);
    }

    [Fact]
    public Task TrimFieldsAndTheGraphicalEditorRenderOnTheDefaultMediaPane() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);

            var (pane, window) = Show(shell.Cues.Inspector);
            try
            {
                var names = pane.GetVisualDescendants()
                    .Where(control => control.IsVisible)
                    .Select(AutomationProperties.GetName)
                    .Where(name => name is not null)
                    .ToHashSet();

                Assert.Contains("Trim in", names);
                Assert.Contains("Trim out", names);
                Assert.Contains("Open clip trimming editor", names);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task CueSendMatrixHasTwoAxisScrollingAndAFullScrollbarGutter() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            shell.Cues.Inspector.SelectedTab = "AUDIO";

            var (pane, window) = Show(shell.Cues.Inspector);
            try
            {
                var viewport = pane.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Single(scroll => scroll.Name == "CueSendMatrixViewport");

                Assert.Equal("Auto", viewport.HorizontalScrollBarVisibility.ToString());
                Assert.Equal("Auto", viewport.VerticalScrollBarVisibility.ToString());
                Assert.Equal(360, viewport.MaxHeight);
                Assert.True(viewport.Padding.Bottom >= 40);
            }
            finally
            {
                window.Close();
            }
        });
}
