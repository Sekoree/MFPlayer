using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.Controls;
using HaCue2.Core.Serialization;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class CurveEditorWindow : Window
{
    public CurveEditorWindow()
    {
        InitializeComponent();
        DataContext = new CurveEditorViewModel();
    }

    /// <summary>Opens on a real curve - the route every "✎" beside a curve picker takes.</summary>
    public CurveEditorWindow(CurveEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCurveGesture(object? sender, CurveGesture e) => Editor?.Apply(e);

    private void OnCurveGestureCompleted(object? sender, EventArgs e) => Editor?.EndGesture();

    private void OnHoldToggled(object? sender, int index) => Editor?.ToggleHold(index);

    /// <summary>
    /// The canvas's own Ctrl+A / Ctrl+C / Ctrl+V, and the buttons beside it.
    /// </summary>
    /// <remarks>
    /// Wired here as well as on the timeline sheet so ONE control behaves the same in both hosts -
    /// the canvas raised these events in this window too, and nothing was listening.
    /// </remarks>
    private void OnSelectAllPoints(object? sender, EventArgs e) => Editor?.SelectAll();

    private void OnSelectAllPointsClicked(object? sender, RoutedEventArgs e) => Editor?.SelectAll();

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
            // Clipboard ownership is best-effort; the curve and the selection are untouched.
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

    private void OnSavePreset(object? sender, RoutedEventArgs e) => Editor?.SavePreset();

    private void OnRenamePreset(object? sender, RoutedEventArgs e) => Editor?.RenamePreset();

    private void OnDeletePreset(object? sender, RoutedEventArgs e) => Editor?.DeletePreset();

    private async void OnImportPresets(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import curve presets from project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaCue2 project")
                    { Patterns = [$"*{HaCueProjectFile.Extension}"] },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        try
        {
            editor.ImportPresets(await HaCueProjectFile.LoadAsync(path).ConfigureAwait(true));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or HaCueProjectFormatException)
        {
            editor.ReportPresetError($"import failed · {failure.Message}");
        }
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    private CurveEditorViewModel? Editor => DataContext as CurveEditorViewModel;
}
