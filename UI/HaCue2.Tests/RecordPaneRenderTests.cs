using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The record pane as actually rendered.
/// </summary>
/// <remarks>
/// The view-model tests prove the pane EDITS; these prove the markup in front of it LOADS and binds to
/// it. They are different failures: a pattern box bound to nothing and a pattern box that is not there
/// both leave the operator unable to record, and only one of them is visible from the view-model side.
/// Flyout contents in particular are built lazily, so a broken token template compiles cleanly and
/// throws the first time somebody opens the dropdown.
/// </remarks>
public class RecordPaneRenderTests
{
    /// <summary>Shows a view in a window and flushes the dispatcher, so bindings have run.</summary>
    private static Window Host(Control view, object dataContext)
    {
        view.DataContext = dataContext;

        var window = new Window { Width = 1400, Height = 900, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    [Fact]
    public Task TheAudioRecordPaneRendersAndShowsTheDocumentsPattern() => ShellFixture.WithShell(shell =>
    {
        var line = new AudioLineDefinition
        {
            Name = "Archive",
            Kind = AudioLineKind.FileRecord,
            Record = new RecordTarget { Pattern = "archive-{n}.mka", Directory = "/tmp/shows" },
        };

        shell.Project.AudioLines.Add(line);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);

        // The pane lives on the Devices tab, and an unselected tab's content is not in the visual tree
        // at all — a render test that forgot this would be asserting about a screen nobody had opened.
        audio.SelectedTab = audio.DevicesTab;
        audio.SelectedLine = audio.Lines.Single(row => row.Id == line.Id);

        var window = Host(new AudioView(), audio);

        var boxes = window.GetVisualDescendants().OfType<TextBox>().Select(box => box.Text).ToList();

        // The values on screen are the document's, not the literals the pane was drawn with.
        Assert.Contains("archive-{n}.mka", boxes);
        Assert.Contains("/tmp/shows", boxes);
        Assert.DoesNotContain(boxes, text => text == "show-{date}-{n}.flac");
        Assert.DoesNotContain(boxes, text => text == "~/shows/recordings");
    });

    [Fact]
    public Task TheArmButtonIsOnScreenForARecordLine() => ShellFixture.WithShell(shell =>
    {
        var line = new AudioLineDefinition { Name = "Archive", Kind = AudioLineKind.FileRecord };
        shell.Project.AudioLines.Add(line);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);

        // The pane lives on the Devices tab, and an unselected tab's content is not in the visual tree
        // at all — a render test that forgot this would be asserting about a screen nobody had opened.
        audio.SelectedTab = audio.DevicesTab;
        audio.SelectedLine = audio.Lines.Single(row => row.Id == line.Id);

        var window = Host(new AudioView(), audio);

        var arm = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => (button.Content as string) == "ARM");

        Assert.NotNull(arm);
    });

    [Fact]
    public Task TheTokenDropdownBuildsWithoutThrowing() => ShellFixture.WithShell(shell =>
    {
        var line = new AudioLineDefinition
        {
            Name = "Archive",
            Kind = AudioLineKind.FileRecord,
            Record = new RecordTarget { Pattern = "archive.mka" },
        };

        shell.Project.AudioLines.Add(line);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);

        // The pane lives on the Devices tab, and an unselected tab's content is not in the visual tree
        // at all — a render test that forgot this would be asserting about a screen nobody had opened.
        audio.SelectedTab = audio.DevicesTab;
        audio.SelectedLine = audio.Lines.Single(row => row.Id == line.Id);

        var window = Host(new AudioView(), audio);

        var dropdown = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => (button.Content as string) == "{ } ▾");

        Assert.NotNull(dropdown);

        var flyout = Assert.IsType<Flyout>(dropdown.Flyout);

        // Opened for real. The token template is built lazily, so this is the only thing that proves it
        // is not a compile-clean template that throws the first time an operator clicks it. The popup
        // lives in its own visual root rather than under the window, so its content is reached directly.
        flyout.ShowAt(dropdown);
        Dispatcher.UIThread.RunJobs();

        var content = Assert.IsAssignableFrom<Control>(flyout.Content);

        var offered = content.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text)
            .ToList();

        foreach (var token in audio.Record.Tokens)
            Assert.Contains(token.Token, offered);

        flyout.Hide();
    });

    [Fact]
    public Task TheRecordPaneIsAbsentForAnInterfaceLine() => ShellFixture.WithShell(shell =>
    {
        var line = new AudioLineDefinition { Name = "Main", Kind = AudioLineKind.LocalAudio };
        shell.Project.AudioLines.Add(line);

        var audio = new AudioViewModel(shell.Journal, shell.Runtime);

        // The pane lives on the Devices tab, and an unselected tab's content is not in the visual tree
        // at all — a render test that forgot this would be asserting about a screen nobody had opened.
        audio.SelectedTab = audio.DevicesTab;
        audio.SelectedLine = audio.Lines.Single(row => row.Id == line.Id);

        var window = Host(new AudioView(), audio);

        // Hidden rather than dimmed: a filename pattern is not a disabled property of a sound card.
        var arm = window.GetVisualDescendants().OfType<Button>()
            .Where(button => (button.Content as string) is "ARM" or "DISARM")
            .Where(button => button.IsEffectivelyVisible);

        Assert.Empty(arm);
    });
}
