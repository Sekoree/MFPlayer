using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    /// <summary>Finds a transport key the way a screen reader would, by the name it announces.</summary>
    private static Button TransportButton(Control view, string automationName) =>
        view.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == automationName);

    /// <summary>A whole click — press AND release, which is what raises Click.</summary>
    private static void Click(Window window, Control target)
    {
        var centre = target.TranslatePoint(
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
    public Task ClickingTheInspectorToggleActuallyShowsAndHidesTheRightColumn() =>
        ShellFixture.WithShell(shell =>
        {
            var view = new CuesView();
            var window = Host(view, shell.Cues);
            var toggle = TransportButton(view, "Show or hide cue inspector");

            Assert.True(shell.Cues.IsRightPanelOpen, "the inspector should start open at 1600 px");

            Click(window, toggle);

            // This button did NOTHING, on any click. It was a ToggleButton whose IsChecked was bound to
            // IsRightPanelOpen, and IsChecked binds two-way by DEFAULT: the click wrote the property
            // through the binding, and then the Click handler inverted it straight back. Two writers,
            // one per click, cancelling exactly.
            //
            // Only a real pointer can see this. The responsive test next door sets IsRightPanelOpen
            // itself, so it exercised the half that was never broken.
            Assert.False(shell.Cues.IsRightPanelOpen, "clicking HIDE INSPECTOR left the inspector open");
            Assert.Equal(0, shell.Cues.RightPanelWidth.Value);

            Click(window, toggle);

            Assert.True(shell.Cues.IsRightPanelOpen, "clicking SHOW INSPECTOR left the inspector hidden");
            Assert.Equal(316, shell.Cues.RightPanelWidth.Value);
        });

    [Fact]
    public Task TheInspectorToggleIsSkinnedLikeTheKeysBesideIt() => ShellFixture.WithShell(shell =>
    {
        var view = new CuesView();
        Host(view, shell.Cues);

        var inspector = TransportButton(view, "Show or hide cue inspector");
        var standby = TransportButton(view, "Move standby up");

        // Avalonia resolves an implicit ControlTheme by STYLE KEY — the control's own concrete type.
        // ToggleButton is not Button, so the booth theme keyed off {x:Type Button} never reached it: it
        // drew in SimpleTheme's stock chrome beside eight mono booth keys, and `.tight`, which is
        // declared INSIDE that theme, styled nothing at all.
        //
        // Asserted against the neighbouring key rather than against literals, so the two move together
        // when the skin does.
        Assert.Equal(standby.FontFamily, inspector.FontFamily);
        Assert.Equal(standby.Padding, inspector.Padding);
    });

    [Fact]
    public Task TheActiveGroupExpanderDrawsTheTreesChevron() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.ActivePanelRows.Add(new ActiveGroupRow
        {
            GroupId = Guid.NewGuid(),
            Number = "12",
            Label = "Walk-in playlist",
            Mode = "playlist",
        });

        var view = new CuesView();
        var window = Host(view, shell.Cues);

        var expander = window.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(button => AutomationProperties.GetName(button) == "Expand active group");
        var row = (ActiveGroupRow)expander.DataContext!;

        // The chevron theme templates a Path and flips its geometry on :checked. SimpleTheme's stock
        // ToggleButton — which is what this got, because an implicit ControlTheme matches the exact type
        // and the booth theme is keyed off {x:Type Button} — templates a ContentPresenter and would have
        // drawn the literal "▾" that used to be its Content, in Inter, in a stock box.
        Assert.Single(expander.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());

        Assert.True(row.IsExpanded, "a group opens showing its children");
        Click(window, expander);

        Assert.False(row.IsExpanded, "clicking the chevron did not collapse the group");
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
