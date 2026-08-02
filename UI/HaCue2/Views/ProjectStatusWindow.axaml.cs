using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class ProjectStatusWindow : Window
{
    public ProjectStatusWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Relinks everything missing by searching a new root.</summary>
    private void OnRelinkRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectStatusViewModel status)
            PromptWindow.Show(this, status.RelinkUnderRoot());
    }

    /// <summary>
    /// Points ONE missing reference at a file the operator chose.
    /// </summary>
    /// <remarks>
    /// The manual escape hatch, for the file a search cannot find because it was renamed. A file
    /// picker rather than a typed path: the operator is looking at the file, not remembering it.
    /// </remarks>
    private async void OnRelinkOne(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectStatusViewModel status || status.MissingPath.Length == 0)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replacement for {System.IO.Path.GetFileName(status.MissingPath)}",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } chosen)
            status.RelinkOne(status.MissingPath, chosen);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
