using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>"Edit ›" beside an output's mapping toggle jumps to the Mapping tab already scoped to
    /// that output — mapping is always the mapping OF one output, never a global mode.</summary>
    private void OnEditMapping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel video)
            video.SelectedTab = VideoViewModel.MappingTab;
    }

    // ── canvas gestures ───────────────────────────────────────────────────────────────────────
    // Handled here rather than bound to a command because a composition canvas lives inside a
    // DataTemplate whose DataContext is one pane, and the edit belongs to the view — the pane is a
    // projection, not the thing that owns the journal.

    private void OnLayerGesture(object? sender, PlacementGesture e) => Video?.ApplyLayerGesture(e);

    private void OnMappingSourceGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingSourceGesture(e);

    private void OnMappingTargetGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingTargetGesture(e);

    private void OnGestureCompleted(object? sender, EventArgs e) => Video?.EndGesture();

    private void OnSectionSelected(object? sender, int index) => Video?.SelectSection(index);

    /// <summary>
    /// Pushes a typed section number into the document when the field loses focus.
    /// </summary>
    /// <remarks>
    /// The default binding commits on LostFocus already; this raises the properties back so a value the
    /// document clamped (a width past the edge, a negative opacity) is shown as what was actually
    /// stored rather than as what was typed.
    /// </remarks>
    private void OnSectionFieldCommitted(object? sender, RoutedEventArgs e) => Video?.Refresh();

    private VideoViewModel? Video => DataContext as VideoViewModel;
}
