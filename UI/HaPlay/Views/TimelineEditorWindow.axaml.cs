using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HaPlay.ViewModels;

namespace HaPlay.Views;

/// <summary>
/// Pop-out timeline editor for ONE Timeline group (one window per group, the
/// <see cref="ScriptEditorWindow"/> shell precedent). All arrangement edits flow through the group
/// children's <see cref="CueNodeViewModel"/> properties; the window owns only zoom (viewport-scoped)
/// and disposes its view model on close to stop the playhead timer.
/// </summary>
public partial class TimelineEditorWindow : Window
{
    public TimelineEditorWindow()
    {
        InitializeComponent();
        // Fit-on-open after the first layout pass - the viewport width is 0 until then.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => Timeline.ZoomFit(TimelineScroll.Viewport.Width), DispatcherPriority.Background);
    }

    private void OnZoomFitClick(object? sender, RoutedEventArgs e) =>
        Timeline.ZoomFit(TimelineScroll.Viewport.Width);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Timeline.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Timeline.ZoomOut();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as TimelineEditorWindowViewModel)?.Dispose();
    }
}
