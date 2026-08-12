using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class AutomationEditorWindow : Window
{
    public AutomationEditorWindow()
    {
        InitializeComponent();
        DataContext = new AutomationEditorViewModel();
    }

    public AutomationEditorWindow(AutomationEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private AutomationEditorViewModel? Editor => DataContext as AutomationEditorViewModel;

    private void OnGesture(object? sender, CurveGesture gesture) => Editor?.Apply(gesture);
    private void OnGestureCompleted(object? sender, EventArgs e) => Editor?.EndGesture();
    private void OnDelete(object? sender, RoutedEventArgs e) => Editor?.DeleteSelection();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => Editor?.Zoom(0.5);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Editor?.Zoom(2);
    private void OnPanLeft(object? sender, RoutedEventArgs e) => Editor?.Pan(-0.8);
    private void OnPanRight(object? sender, RoutedEventArgs e) => Editor?.Pan(0.8);
    private void OnFit(object? sender, RoutedEventArgs e) => Editor?.Fit();
    private void OnExtend(object? sender, RoutedEventArgs e) => Editor?.Extend();
    private void OnPreviousKey(object? sender, RoutedEventArgs e) => Editor?.JumpKey(-1);
    private void OnNextKey(object? sender, RoutedEventArgs e) => Editor?.JumpKey(1);
    private void OnAddKey(object? sender, RoutedEventArgs e) => Editor?.AddKeyAtCursor();
    private void OnSelectAllPoints(object? sender, EventArgs e) => Editor?.SelectAll();
    private async void OnCopyPoints(object? sender, EventArgs e) => await CopyPoints();
    private async void OnCopyPointsClicked(object? sender, RoutedEventArgs e) => await CopyPoints();
    private async Task CopyPoints()
    {
        if (Editor?.Copy() is not { Length: > 0 } text || Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Clipboard ownership is best-effort; the automation and selection stay untouched.
        }
    }
    private async void OnPastePoints(object? sender, EventArgs e) => await PastePoints();
    private async void OnPastePointsClicked(object? sender, RoutedEventArgs e) => await PastePoints();
    private async Task PastePoints()
    {
        if (Editor is not { } editor || Clipboard is not { } clipboard)
            return;
        try
        {
            editor.Paste(await clipboard.TryGetTextAsync());
        }
        catch (Exception)
        {
            // Another process can temporarily own the clipboard. Treat that as an empty paste.
        }
    }
    private async void OnSeek(object? sender, RoutedEventArgs e)
    {
        if (Editor is { } editor)
            await editor.SeekAsync();
    }
    private void OnPointTimeCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            Editor?.CommitPointTime(box.Text ?? "");
    }
    private void OnPointValueCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            Editor?.CommitPointValue(box.Text ?? "");
    }
    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
