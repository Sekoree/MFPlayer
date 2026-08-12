using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Model;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class TimelineSheet : UserControl
{
    public TimelineSheet()
    {
        InitializeComponent();

        // Tunnelled, because the lane list's own ScrollViewer marks wheel events handled before they
        // would bubble here. Plain wheel still scrolls the lanes; the MODIFIED gestures are the
        // sheet's: Shift+wheel (or a touchpad's horizontal delta) pans the window, Ctrl+wheel zooms.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Timeline is not { } timeline)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0)
                timeline.ZoomIn();
            else if (e.Delta.Y < 0)
                timeline.ZoomOut();
            e.Handled = true;
            return;
        }

        var sideways = e.Delta.X != 0
            ? e.Delta.X
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0;
        if (sideways == 0)
            return;

        // A tenth of the window per notch: enough to travel, small enough to aim. Wheel-up/left pans
        // toward the start, matching every horizontal scroll surface.
        timeline.Pan(-sideways * 0.1);
        e.Handled = true;
    }

    /// <summary>Closing the sheet is the Cues view's state, not the sheet's — the sheet is only ever
    /// a projection of "is the timeline open".</summary>
    /// <remarks>
    /// Through <c>Timeline.Owner</c>, never an ancestor walk: in the floating window there is no
    /// <see cref="CuesView"/> above this control, and the ancestor form made CLOSE a silent no-op
    /// there. Undocked, CLOSE means "close the editor", not "dock it" — <c>RequestClose</c> takes the
    /// window down without the re-dock its ordinary Closed handler performs.
    /// </remarks>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Owner: { } cues } timeline)
            return;

        if (timeline.IsUndocked)
            timeline.RequestClose();
        cues.IsTimelineOpen = false;
    }

    // Handled here rather than bound: a lane lives in a DataTemplate whose DataContext is one lane, and
    // the edit belongs to the sheet — the lane is a projection, not the thing holding the journal.
    private void OnClipGesture(object? sender, ClipGesture e) => Timeline?.ApplyClipGesture(e);

    private void OnClipGestureCompleted(object? sender, EventArgs e) => Timeline?.EndGesture();

    private void OnLaneGesture(object? sender, CurveGesture e)
    {
        if (Timeline is { } timeline && (sender as Control)?.DataContext is TimelineLane lane)
            timeline.ApplyLaneGesture(lane, e);
    }

    private void OnLaneGestureCompleted(object? sender, EventArgs e) => Timeline?.EndGesture();

    private void OnSelectAllKeyframes(object? sender, RoutedEventArgs e) => SelectAllKeyframes(sender);

    private void OnCanvasSelectAllKeyframes(object? sender, EventArgs e) => SelectAllKeyframes(sender);

    private void SelectAllKeyframes(object? sender)
    {
        if (Timeline is { } timeline && (sender as Control)?.DataContext is TimelineLane lane)
            timeline.SelectAllKeyframes(lane);
    }

    private async void OnCopyKeyframes(object? sender, RoutedEventArgs e) => await CopyKeyframes(sender);

    private async void OnCanvasCopyKeyframes(object? sender, EventArgs e) => await CopyKeyframes(sender);

    private async Task CopyKeyframes(object? sender)
    {
        if (Timeline is not { } timeline
            || (sender as Control)?.DataContext is not TimelineLane lane
            || timeline.CopySelectedKeyframes(lane) is not { Length: > 0 } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Clipboard ownership is best-effort; the document and selection remain untouched.
        }
    }

    private async void OnPasteKeyframes(object? sender, RoutedEventArgs e) => await PasteKeyframes(sender);

    private async void OnCanvasPasteKeyframes(object? sender, EventArgs e) => await PasteKeyframes(sender);

    private async Task PasteKeyframes(object? sender)
    {
        if (Timeline is not { } timeline
            || (sender as Control)?.DataContext is not TimelineLane lane
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            timeline.PasteKeyframes(lane, await clipboard.TryGetTextAsync());
        }
        catch (Exception)
        {
            // Another process can temporarily own the clipboard. Treat that as an empty paste.
        }
    }

    private void OnDeleteKeyframes(object? sender, RoutedEventArgs e)
    {
        if (Timeline is { } timeline && (sender as Control)?.DataContext is TimelineLane lane)
            timeline.DeleteSelectedKeyframes(lane);
    }

    private void OnToggleEffectLane(object? sender, RoutedEventArgs e)
    {
        if (Timeline is { } timeline && (sender as Control)?.DataContext is TimelineLane lane)
            timeline.ToggleEffectLane(lane);
    }

    private void OnOpenLaneEditor(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { } timeline
            || (sender as Control)?.DataContext is not TimelineLane lane
            || timeline.LaneEditor(lane) is not { } editor
            || this.FindAncestorOfType<Window>() is not { } owner)
            return;

        var window = new AutomationEditorWindow(editor);
        window.Closed += (_, _) => timeline.Refresh();
        window.ShowDialog(owner);
    }

    /// <summary>
    /// Runs the group from the playhead — the rehearsal verb.
    /// </summary>
    /// <remarks>
    /// Distinct from GO, which always starts a group at its top. What somebody rehearsing a scene wants
    /// is the state the show would be in AT that moment: the cues after the playhead scheduled, and the
    /// bed running under them already part-way through.
    /// </remarks>
    private async void OnPlayFrom(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Group: { } group } timeline
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        timeline.TransportProblem =
            await shell.PlayTimelineFromAsync(group, timeline.PlayheadAt) ?? "";
    }

    /// <summary>
    /// Stops everything this group started.
    /// </summary>
    /// <remarks>
    /// The GROUP, not the show: a timeline sheet is open on one scene, and an operator pressing stop
    /// inside it is asking for that scene to stop — not for the music bed running under the whole act
    /// from a different list.
    /// </remarks>
    private async void OnStopTimeline(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Group: { } group } timeline
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        timeline.TransportProblem = await shell.StopTimelineAsync(group) ?? "";
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Timeline?.ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Timeline?.ZoomOut();

    private void OnZoomFit(object? sender, RoutedEventArgs e) => Timeline?.ZoomFit();

    /// <summary>
    /// Places the playhead where the ruler was clicked.
    /// </summary>
    /// <remarks>
    /// Measured against the ruler's own width, which starts after the label column — so the fraction
    /// the view-model gets is a fraction of the TRACKS, not of the whole sheet. Getting that wrong
    /// would put the playhead a fixed distance off, worst at the left where it matters most.
    /// </remarks>
    private void OnRulerPressed(object? sender, PointerPressedEventArgs e)
    {
        // LEFT button only. A right-press scrubbing the playhead is not what the gesture means, and
        // handling it would also swallow any context menu the ruler grows later.
        if (Timeline is not { } timeline
            || sender is not Control ruler
            || ruler.Bounds.Width <= 0
            || !e.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed)
            return;

        timeline.PlacePlayhead(
            e.GetPosition(ruler).X / ruler.Bounds.Width, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        // Captured so the press becomes a DRAG: the playhead follows the pointer smoothly until
        // release, even when it leaves the ruler's strip.
        e.Pointer.Capture(ruler);
        _rulerDragging = true;
        e.Handled = true;
    }

    private bool _rulerDragging;

    private void OnRulerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rulerDragging
            || Timeline is not { } timeline
            || sender is not Control ruler
            || ruler.Bounds.Width <= 0)
            return;

        // The button is re-checked per move rather than trusted from the press: a capture can end
        // without a release reaching us, and a latched flag would scrub on a plain hover afterwards.
        if (!e.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed)
        {
            _rulerDragging = false;
            return;
        }

        // Shift is re-read per move, so the grid can be picked up or dropped mid-drag.
        timeline.PlacePlayhead(
            e.GetPosition(ruler).X / ruler.Bounds.Width, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void OnRulerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rulerDragging)
            return;

        _rulerDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>The drag ended without a release — window deactivated, or something else took the
    /// pointer. Same reset, so a later hover cannot resume scrubbing.</summary>
    private void OnRulerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _rulerDragging = false;

    /// <summary>A lane label click selects the lane's cue — the same selection the tree, the
    /// inspector, "+ AUTOMATION" and DUCK all act on.</summary>
    private void OnLaneLabelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Timeline is not { Owner: { } cues } timeline
            || (sender as Control)?.DataContext is not TimelineLane lane)
            return;

        cues.SelectCue(lane.SubjectId);
        timeline.SyncSelection(lane.SubjectId);
        e.Handled = true;
    }

    /// <summary>
    /// Ducks the clip the tree has selected under everything overlapping it.
    /// </summary>
    /// <remarks>
    /// The BED is the selected cue, not a separate pick: the operator is looking at the lane they want
    /// pushed down, and asking them to choose it again in a dialog is a question they already answered.
    /// </remarks>
    /// <summary>
    /// Moves the sheet between the bottom of the Cues view and a window of its own.
    /// </summary>
    /// <remarks>
    /// The same view-model in both, so an edit made in one is already in the other — there is one
    /// timeline, and two ways to look at it. The window closing docks it again rather than leaving a
    /// sheet nobody can see: a "close" that hides a panel with no way back is how a feature goes
    /// missing.
    /// </remarks>
    private void OnUndock(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Owner: { } cues } timeline)
            return;

        if (timeline.IsUndocked)
        {
            // The floating window owns itself; docking IS its Close, and the handler below puts the
            // sheet back. There is no CuesView above this control any more, which is exactly why the
            // view-model carries its owner.
            this.FindAncestorOfType<Window>()?.Close();
            return;
        }

        timeline.IsUndocked = true;
        cues.IsTimelineOpen = false;

        var window = new TimelineWindow { DataContext = timeline };

        // RequestClose (CLOSE ⎋ while floating, or the group leaving timeline mode) closes the
        // window WITHOUT the re-dock: those closes mean "the editor goes away", and re-docking would
        // resurrect the sheet the caller just asked to be rid of. A plain window close (title bar,
        // DOCK ↙) still docks, so the panel can never be lost.
        var closing = false;
        void CloseWithoutRedock()
        {
            closing = true;
            window.Close();
        }

        timeline.CloseRequested += CloseWithoutRedock;
        window.Closed += (_, _) =>
        {
            timeline.CloseRequested -= CloseWithoutRedock;
            timeline.IsUndocked = false;
            if (!closing)
                cues.IsTimelineOpen = true;
        };

        window.Show(this.FindAncestorOfType<Window>()!);
    }

    private void OnDuck(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { } timeline)
            return;

        // The refusal is SAID, in the transport row's problem slot: Duck's preconditions (a media cue
        // of this group selected, something overlapping it) are easy to miss, and a button that does
        // nothing silently reads as unimplemented.
        if (timeline.Owner is not { SelectedCue: { } selected }
            || timeline.Duck(selected.Id) is not { } prompt)
        {
            timeline.TransportProblem =
                "duck needs a media cue of this group selected, with something overlapping it";
            return;
        }

        timeline.TransportProblem = "";
        PromptWindow.Show(this, prompt, timeline.Refresh);
    }

    /// <summary>
    /// Adds an automation track to the selected cue, from the footer's property picker.
    /// </summary>
    /// <remarks>
    /// Delegates to the inspector's <c>AddLane</c> — the one place that knows which kinds a cue can
    /// carry — so the footer affordance and the inspector's stay one behaviour. The refusal goes to
    /// the transport row, same as Duck's.
    /// </remarks>
    private void OnAddLane(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Owner.Inspector: { } inspector } timeline
            || (sender as Control)?.Tag is not string propertyId)
            return;

        // The menu already disables what the selection cannot carry, so reaching here with a refusal
        // means no cue is selected at all — which the menu has no way to grey out.
        if (!inspector.CanAddLane(propertyId))
        {
            timeline.TransportProblem =
                "select a cue that can carry that lane (and does not have one yet)";
            return;
        }

        timeline.TransportProblem = "";
        inspector.AddLane(propertyId);
        timeline.Refresh();
    }

    private TimelineViewModel? Timeline => DataContext as TimelineViewModel;
}
