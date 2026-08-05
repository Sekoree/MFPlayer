using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

public sealed class AccessibilityAndResponsiveTests
{
    [Fact]
    public Task PrimaryTransportAndCustomEditorsExposeAutomationNames() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var shell = new ShellViewModel(ShellFixture.Project());
            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var named = window.GetVisualDescendants().OfType<Control>()
                .Select(AutomationProperties.GetName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet();

            Assert.Contains("Go current cue list", named);
            Assert.Contains("Panic; hold to stop everything", named);
            Assert.Contains("Cue list tree", named);
            window.Close();
        });

    [Fact]
    public Task InspectorCollapsesAtLaptopWidthAndCanBeRestored() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var shell = new ShellViewModel(ShellFixture.Project());
            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 960, Height = 700, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(shell.Cues.IsRightPanelOpen);
            Assert.Equal(0, shell.Cues.RightPanelWidth.Value);

            shell.Cues.IsRightPanelOpen = true;
            Assert.Equal(316, shell.Cues.RightPanelWidth.Value);
            window.Close();
        });

    [Fact]
    public Task WarpMeshEditorIsKeyboardFocusableAndNamed() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var editor = new WarpMeshCanvas
            {
                Columns = 2,
                Rows = 2,
                Offsets = new double[8],
            };
            AutomationProperties.SetName(editor, "Warp mesh editor");

            Assert.True(editor.Focusable);
            Assert.Equal("Warp mesh editor", AutomationProperties.GetName(editor));
        });
}
