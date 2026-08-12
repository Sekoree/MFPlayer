using Avalonia.Controls;
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

    /// <summary>Opens on a real curve — the route every "✎" beside a curve picker takes.</summary>
    public CurveEditorWindow(CurveEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCurveGesture(object? sender, CurveGesture e) => Editor?.Apply(e);

    private void OnCurveGestureCompleted(object? sender, EventArgs e) => Editor?.EndGesture();

    private void OnHoldToggled(object? sender, int index) => Editor?.ToggleHold(index);

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
