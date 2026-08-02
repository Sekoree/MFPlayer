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

    private TimelineViewModel? Timeline => DataContext as TimelineViewModel;
}
