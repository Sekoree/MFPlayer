using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Journal;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The Settings window's two-scope nav, driven through the real window.
/// </summary>
/// <remarks>
/// A view-model test cannot see this fault. Both navs bound <c>SelectedItem</c> to ONE property, and
/// what broke it was the LISTBOX's behaviour: a selector whose selected item is not among its own items
/// clears itself and writes that null back. Nothing said so until an operator clicked "Show behaviour"
/// and the right-hand side went blank with an exception per bound property. So this drives the control.
/// </remarks>
public class SettingsWindowRenderTests
{
    private static (SettingsWindow Window, SettingsViewModel Settings) Show()
    {
        var project = ShellFixture.Project();
        var settings = new SettingsViewModel(project, new ProjectJournal(project));
        var window = new SettingsWindow { DataContext = settings };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, settings);
    }

    /// <summary>The nav lists, in the order the window lays them out: application first, then project.</summary>
    private static IReadOnlyList<ListBox> Navs(SettingsWindow window) =>
        [.. window.GetVisualDescendants().OfType<ListBox>()
            .Where(list => list.ItemsSource is IEnumerable<SettingsPane>)];

    [Fact]
    public Task ClickingAProjectPaneShowsItInsteadOfBlankingTheWindow() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (window, settings) = Show();
            try
            {
                var navs = Navs(window);
                Assert.Equal(2, navs.Count);

                var projectNav = navs[1];
                projectNav.SelectedItem = settings.ProjectPanes
                    .Single(pane => pane.Name == "Show behaviour");
                Dispatcher.UIThread.RunJobs();

                // The application nav clears itself — its item is not the chosen one — and that null
                // must NOT become the window's selection.
                Assert.Null(navs[0].SelectedItem);
                Assert.NotNull(settings.SelectedPane);
                Assert.True(settings.IsShowBehaviourPane);

                // And something is actually on screen. The failure this pins is a right-hand side with
                // every pane hidden, which no exception assertion would have caught.
                Assert.Contains(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsVisible && (text.Text ?? "").Contains(
                        "journaled", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task WalkingEveryNavRowLeavesSomethingOnScreenEachTime() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var (window, settings) = Show();
            try
            {
                var navs = Navs(window);

                foreach (var (nav, panes) in new[]
                         {
                             (navs[0], (IReadOnlyList<SettingsPane>)settings.ApplicationPanes),
                             (navs[1], settings.ProjectPanes),
                         })
                {
                    foreach (var pane in panes)
                    {
                        nav.SelectedItem = pane;
                        Dispatcher.UIThread.RunJobs();

                        // Not "no exception" — CONTENT. A nav row that leads to an empty right-hand
                        // side cannot be told apart from a broken one, which is exactly what "Project
                        // status" was before it gained a pane.
                        var visibleText = window.GetVisualDescendants()
                            .OfType<TextBlock>()
                            .Count(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text));

                        Assert.True(
                            visibleText > 6,
                            $"the “{pane.Name}” pane rendered {visibleText} pieces of text");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
}
