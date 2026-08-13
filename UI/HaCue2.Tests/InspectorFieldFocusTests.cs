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
/// Typing in an inspector field must not throw the operator out of it.
/// </summary>
/// <remarks>
/// Every keystroke journals a command, and the journal's change notification refreshes the whole cue
/// view. Anything in that path that tears down or hides the focused control makes a field un-typable:
/// one character lands, focus goes, and the operator has to click back in for the second one.
/// </remarks>
public class InspectorFieldFocusTests
{
    private static TextBox FieldNamed(Visual root, string header) =>
        root.GetVisualDescendants()
            .OfType<HeaderedContentControl>()
            .Where(field => (field.Header as string) == header)
            .SelectMany(field => field.GetVisualDescendants().OfType<TextBox>())
            .First();

    [Fact]
    public Task TypingInTheInspectorLabelKeepsTheFieldFocused() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);

            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 1_400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var label = FieldNamed(view, "Label");
            label.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(label.IsFocused, "the field could not be focused at all");

            label.CaretIndex = label.Text?.Length ?? 0;
            window.KeyTextInput("X");
            Dispatcher.UIThread.RunJobs();

            Assert.EndsWith("X", cue.Label, StringComparison.Ordinal);
            Assert.True(label.IsFocused, "one character threw the operator out of the field");

            window.KeyTextInput("Y");
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("XY", cue.Label, StringComparison.Ordinal);

            window.Close();
        });

    /// <summary>
    /// The same for a GROUP, which is the case the operator hits: renaming one meant clicking back
    /// into the field for every single character.
    /// </summary>
    [Fact]
    public Task RenamingAGroupKeepsTheFieldFocused() =>
        ShellFixture.WithShell(shell =>
        {
            var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
            ShellFixture.Select(shell.Cues, group.Id);
            // A group opens on its own pane; the name lives on GENERAL, which is where a rename starts.
            shell.Cues.Inspector.SelectedTab = "GENERAL";

            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 1_400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var label = FieldNamed(view, "Label");
            label.Focus();
            label.CaretIndex = label.Text?.Length ?? 0;
            Dispatcher.UIThread.RunJobs();

            window.KeyTextInput("A");
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("B");
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("C");
            Dispatcher.UIThread.RunJobs();

            Assert.EndsWith("ABC", group.Label, StringComparison.Ordinal);
            Assert.True(label.IsFocused, "the group name field lost focus mid-rename");

            window.Close();
        });

    /// <summary>
    /// The same defect on the Audio view, whose row lists are rebuilt by the same journal signal.
    /// </summary>
    /// <remarks>
    /// Renaming a logical output lost more than focus: the list dropped its selection with its rows, so
    /// the inspector beside it went back to "no output selected" after the first character.
    /// </remarks>
    [Fact]
    public Task RenamingALogicalOutputKeepsTheFieldFocusedAndTheOutputSelected() =>
        ShellFixture.WithShell(shell =>
        {
            var view = new AudioView { DataContext = shell.Audio };
            var window = new Window { Width = 1_400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var selected = shell.Audio.SelectedOutput?.Id;
            Assert.NotNull(selected);

            var name = FieldNamed(view, "Name");
            name.Focus();
            name.CaretIndex = name.Text?.Length ?? 0;
            Dispatcher.UIThread.RunJobs();

            window.KeyTextInput("X");
            Dispatcher.UIThread.RunJobs();

            Assert.True(name.IsFocused, "one character threw the operator out of the field");
            Assert.Equal(selected, shell.Audio.SelectedOutput?.Id);
            Assert.EndsWith("X", shell.Audio.SelectedOutput!.Name, StringComparison.Ordinal);

            window.Close();
        });

    /// <summary>
    /// The tab strip must survive an edit untouched. Replacing its source is what dropped focus, and a
    /// tab that reset to null on every keystroke also threw the operator back to GENERAL.
    /// </summary>
    [Fact]
    public Task EditingDoesNotChurnTheTabStrip() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);
            shell.Cues.Inspector.SelectedTab = "NOTE";

            var tabs = shell.Cues.Inspector.Tabs;
            var churn = 0;
            shell.Cues.Inspector.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(InspectorViewModel.Tabs))
                    churn++;
            };

            shell.Cues.Inspector.NoteValue = "a note";

            Assert.Equal(0, churn);
            Assert.Same(tabs, shell.Cues.Inspector.Tabs);
            Assert.Equal("NOTE", shell.Cues.Inspector.SelectedTab);
        });
}
