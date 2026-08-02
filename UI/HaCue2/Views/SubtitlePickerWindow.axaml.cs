using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class SubtitlePickerWindow : Window
{
    public SubtitlePickerWindow()
    {
        InitializeComponent();
        DataContext = new SubtitlePickerViewModel();
    }

    public SubtitlePickerWindow(SubtitlePickerViewModel picker)
    {
        InitializeComponent();
        DataContext = picker;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Adds a subtitle file from disk.
    /// </summary>
    /// <remarks>
    /// A sidecar is how a show runs a corrected translation over a file whose embedded track is wrong,
    /// which is common enough that leaving it out would send the operator back to a text editor.
    /// </remarks>
    private async void OnAddSidecar(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubtitlePickerViewModel picker)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Subtitle file",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt"] },
            ],
        });

        foreach (var file in files)
            picker.AddSidecar(file.TryGetLocalPath() ?? "");
    }

    /// <summary>Writes the selection. Cancel closes without committing, so nothing was an edit.</summary>
    private void OnDone(object? sender, RoutedEventArgs e)
    {
        (DataContext as SubtitlePickerViewModel)?.Commit();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
