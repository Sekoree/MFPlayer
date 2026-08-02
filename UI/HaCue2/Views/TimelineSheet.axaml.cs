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
