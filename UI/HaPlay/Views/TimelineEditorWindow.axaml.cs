using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HaPlay.Resources;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Controls;

namespace HaPlay.Views;

/// <summary>
/// Pop-out timeline editor for ONE Timeline group (one window per group, the
/// <see cref="ScriptEditorWindow"/> shell precedent). All arrangement edits flow through the group
/// children's <see cref="CueNodeViewModel"/> properties; the window owns only zoom (viewport-scoped)
/// and disposes its view model on close to stop the playhead timer. Right-clicking a media block in
/// edit mode opens the block context menu (Phase D "Duck under…").
/// </summary>
public partial class TimelineEditorWindow : Window
{
    public TimelineEditorWindow()
    {
        InitializeComponent();
        // Fit-on-open after the first layout pass - the viewport width is 0 until then.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => Timeline.ZoomFit(TimelineScroll.Viewport.Width), DispatcherPriority.Background);
        Timeline.BlockContextRequested += OnBlockContextRequested;
    }

    private void OnZoomFitClick(object? sender, RoutedEventArgs e) =>
        Timeline.ZoomFit(TimelineScroll.Viewport.Width);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Timeline.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Timeline.ZoomOut();

    /// <summary>Block context menu (edit mode; the canvas only raises this then). One item today:
    /// "Duck under…" opens the Phase D sidechain-lite authoring dialog for the clicked bed.</summary>
    private void OnBlockContextRequested(object? sender, TimelineBlockContextEventArgs e)
    {
        var duckItem = new MenuItem { Header = Strings.TimelineDuckMenuItem };
        duckItem.Click += async (_, _) => await OpenDuckDialogAsync(e.Node);
        var flyout = new MenuFlyout();
        flyout.Items.Add(duckItem);
        flyout.ShowAt(Timeline, showAtPointer: true);
    }

    private async Task OpenDuckDialogAsync(CueNodeViewModel bed)
    {
        if (DataContext is not TimelineEditorWindowViewModel vm)
            return;
        var dialog = new Dialogs.DuckUnderDialog
        {
            DataContext = DuckUnderDialogViewModel.For(bed, vm.Lanes),
        };
        await dialog.ShowDialog<bool?>(this); // Apply writes the envelope through the dialog VM
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as TimelineEditorWindowViewModel)?.Dispose();
    }
}
