using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// What the title bar and the File menu say about the project.
/// </summary>
/// <remarks>
/// The window title was the literal "HaCue2", so nothing anywhere on screen named the file that was
/// open or said whether it had been saved — which makes a working Ctrl+S indistinguishable from a
/// broken one.
/// </remarks>
public class FileMenuTests
{
    [Fact]
    public Task TheWindowTitleNamesTheProjectAndItsUnsavedState() => ShellFixture.WithShell(async shell =>
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-title").FullName;

        try
        {
            var window = new ShellWindow { DataContext = shell };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Never saved: the title still names the show rather than the application.
            Assert.Contains("HaCue2", window.Title!, StringComparison.Ordinal);
            Assert.Contains(shell.Project.Title, window.Title!, StringComparison.Ordinal);

            await shell.SaveToAsync(Path.Combine(directory, "show.hacue2proj"));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("show.hacue2proj", window.Title!, StringComparison.Ordinal);
            Assert.DoesNotContain('*', window.Title!);

            shell.Cues.AddCue(CueKind.Comment);
            Dispatcher.UIThread.RunJobs();

            // The marker HaPlay has: unsaved work is visible without opening anything.
            Assert.Contains('*', window.Title!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task TheMenuAnswersWhereTheProjectIsSaved() => ShellFixture.WithShell(async shell =>
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-where").FullName;

        try
        {
            // Before a save the location says what will happen, rather than showing an empty box.
            Assert.Contains("not saved", shell.ProjectLocation, StringComparison.Ordinal);
            Assert.Equal("Save…", shell.SaveLabel);

            var target = Path.Combine(directory, "show.hacue2proj");
            await shell.SaveToAsync(target);

            Assert.Equal(target, shell.ProjectLocation);
            Assert.Equal("Save", shell.SaveLabel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task TheFileMenuIsOnTheTitleBar() => ShellFixture.WithShell(shell =>
    {
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var file = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => (button.Content as string) == "FILE ▾");

        Assert.NotNull(file);
        Assert.IsType<MenuFlyout>(file.Flyout);
    });

    /// <summary>The project already open is not somewhere the recent list can send you.</summary>
    [Fact]
    public Task TheRecentListExcludesTheOpenProject() => ShellFixture.WithShell(async shell =>
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-recents").FullName;

        try
        {
            var target = Path.Combine(directory, "show.hacue2proj");
            await shell.SaveToAsync(target);

            shell.Settings.NoteOpened(target, "Show", "1 cue", DateTimeOffset.Now);
            shell.Settings.NoteOpened(
                Path.Combine(directory, "other.hacue2proj"), "Other", "2 cues", DateTimeOffset.Now);

            Assert.DoesNotContain(shell.Recents, recent => recent.Path == target);
            Assert.Contains(shell.Recents, recent => recent.Name == "Other");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });
}
