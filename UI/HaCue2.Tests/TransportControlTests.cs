using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The transport controls as an operator actually meets them.
/// </summary>
/// <remarks>
/// Every failure here was invisible to a view-model test. A control with no template binds its values
/// correctly and draws nothing; a button that never receives the pointer runs no command and reports no
/// error. Both shipped, and both are things somebody discovers with an audience in the room.
/// </remarks>
public class TransportControlTests
{
    private static Window Host(Control view, object dataContext)
    {
        view.DataContext = dataContext;
        var window = new Window { Width = 1600, Height = 950, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [Fact]
    public Task TheSeekBarIsTemplatedLikeTheProgressBarItIs() => ShellFixture.WithShell(_ =>
    {
        var bar = new SeekBar { Maximum = 1, Value = 0.5, Width = 200, Height = 12 };
        Host(new StackPanel { Children = { bar } }, new object());

        // A templated control resolves its default ControlTheme by STYLE KEY, which is its own concrete
        // type unless it says otherwise. The booth theme keys the bar off {x:Type ProgressBar}, so
        // without StyleKeyOverride the SeekBar found no theme, got no template and rendered NOTHING —
        // every playing cue showed an empty gap where its progress should be.
        Assert.NotEmpty(bar.GetVisualChildren());
    });

    [Fact]
    public Task TheSeekBarFollowsItsBoundValue() => ShellFixture.WithShell(_ =>
    {
        var bar = new SeekBar { Maximum = 1, Value = 0, Width = 200, Height = 12 };
        Host(new StackPanel { Children = { bar } }, new object());

        // PART_Indicator specifically: the outer Border is the track and is full width at every value.
        var indicator = bar.GetVisualDescendants()
            .OfType<Border>()
            .Single(part => part.Name == "PART_Indicator");

        var before = indicator.Bounds.Width;
        bar.Value = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            indicator.Bounds.Width > before,
            $"the indicator was {before} wide at zero and {indicator.Bounds.Width} at full");
    });

    [Fact]
    public Task HoldingPanicArmsIt() => ShellFixture.WithShell(shell =>
    {
        var view = new CuesView();
        var window = Host(view, shell.Cues);

        var panic = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("panic"));

        var centre = panic.TranslatePoint(
            new Point(panic.Bounds.Width / 2, panic.Bounds.Height / 2), window)!.Value;

        Assert.False(shell.Cues.IsPanicArming);

        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Button marks PointerPressed handled in its own class handler, which runs before any instance
        // handler on the same control — so the markup-declared hold handlers were never called and
        // PANIC did nothing at all, on any press.
        Assert.True(shell.Cues.IsPanicArming, "pressing PANIC did not begin the hold");
    });

    [Fact]
    public Task ReleasingPanicEarlyAbandonsTheHold() => ShellFixture.WithShell(shell =>
    {
        var view = new CuesView();
        var window = Host(view, shell.Cues);

        var panic = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("panic"));

        var centre = panic.TranslatePoint(
            new Point(panic.Bounds.Width / 2, panic.Bounds.Height / 2), window)!.Value;

        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Cues.IsPanicArming);

        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // A brush against the button must not take the show down.
        Assert.False(shell.Cues.IsPanicArming, "releasing PANIC early left it armed");
    });

    [Fact]
    public Task ChangingScopeAfterTheTreeShrankDoesNotThrow() => ShellFixture.WithShell(shell =>
    {
        var cues = shell.Cues;
        var group = cues.Groups.First();

        // Scope into a group, select a row deep in it, then scope back to the whole list and on to a
        // group with fewer rows. The selection model addresses rows by index path into a collection
        // Rebuild replaces underneath it; clearing it afterwards walked paths that no longer existed.
        cues.SelectedScope = group;
        cues.SelectedCue = cues.AllRows.Last();

        var listScope = cues.Scopes.First(scope => scope.IsList);

        // This threw ArgumentOutOfRangeException out of the selection-changed handler — and, because
        // the scope is set by a two-way binding, out of a binding setter, where Avalonia swallowed it
        // as a validation error and the rest of the refresh silently never ran.
        cues.SelectedScope = listScope;
        cues.SelectedScope = group;
        cues.Refresh();

        Assert.NotNull(cues.Scopes);
    });

    [Fact]
    public Task RemovingTheSelectedCueThenRefreshingDoesNotThrow() => ShellFixture.WithShell(shell =>
    {
        var cues = shell.Cues;
        var list = cues.ScopedList!;

        cues.SelectedCue = cues.AllRows.Last();
        list.Cues.Clear();

        // The same stale-path hazard reached the ordinary edit path: the tree shrinks to nothing while
        // the selection still points into it.
        cues.Refresh();

        Assert.Null(cues.SelectedCue);
    });
}
