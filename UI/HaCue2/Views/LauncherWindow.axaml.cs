using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class LauncherWindow : Window
{
    public LauncherWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opening a project is a window-lifetime action, so it stays in the code-behind rather
    /// than putting window construction inside a view-model command.</summary>
    private async void OnRecentActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LauncherViewModel vm)
            await vm.OpenAsync(vm.SelectedRecent);
    }

    /// <summary>Opens the autosave instead of the file.</summary>
    private async void OnRecover(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LauncherViewModel vm)
            await vm.RecoverAsync();
    }

    private void OnDiscardRecovery(object? sender, RoutedEventArgs e) =>
        (DataContext as LauncherViewModel)?.DiscardRecovery();

    /// <summary>Creates a project from the New-project prompt and hands it to the shell.</summary>
    private void OnNewProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LauncherViewModel vm)
            PromptWindow.Show(this, vm.NewProject());
    }

    /// <summary>Opens a project file the operator picked.</summary>
    private async void OnOpenExisting(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LauncherViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaCue2 project") { Patterns = [$"*{ProjectFiles.Extension}"] },
            ],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        var (project, result) = await ProjectFiles.OpenAsync(path);

        // A file that will not open is reported ON the launcher rather than thrown: the operator has
        // to be able to pick a different one.
        if (project is null)
            vm.OpenFailure = result.Message;
        else
            vm.Adopt(project, result.Path);
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
        => new SettingsWindow { DataContext = new SettingsViewModel() }.Show(this);
}
