using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class TimelineSheet : UserControl
{
    public TimelineSheet() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Closing the sheet is the Cues view's state, not the sheet's — the sheet is only ever
    /// a projection of "is the timeline open".</summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<CuesView>()?.DataContext is CuesViewModel cues)
            cues.IsTimelineOpen = false;
    }

    // Handled here rather than bound: a lane lives in a DataTemplate whose DataContext is one lane, and
    // the edit belongs to the sheet — the lane is a projection, not the thing holding the journal.
    private void OnClipGesture(object? sender, ClipGesture e) => Timeline?.ApplyClipGesture(e);

    private void OnClipGestureCompleted(object? sender, EventArgs e) => Timeline?.EndGesture();

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
    private void OnRulerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (Timeline is not { } timeline || sender is not Control ruler || ruler.Bounds.Width <= 0)
            return;

        timeline.PlacePlayhead(e.GetPosition(ruler).X / ruler.Bounds.Width);
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
        window.Closed += (_, _) =>
        {
            timeline.IsUndocked = false;
            cues.IsTimelineOpen = true;
        };

        window.Show(this.FindAncestorOfType<Window>()!);
    }

    private void OnDuck(object? sender, RoutedEventArgs e)
    {
        if (Timeline is not { Owner: { SelectedCue: { } selected } } timeline)
            return;

        PromptWindow.Show(this, timeline.Duck(selected.Id), timeline.Refresh);
    }

    private TimelineViewModel? Timeline => DataContext as TimelineViewModel;
}
