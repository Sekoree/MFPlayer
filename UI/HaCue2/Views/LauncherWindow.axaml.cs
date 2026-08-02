using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class LauncherWindow : Window
{
    public LauncherWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opening a project is a window-lifetime action, so it stays in the code-behind rather
    /// than putting window construction inside a view-model command.</summary>
    private void OnRecentActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LauncherViewModel vm)
            vm.Open(vm.SelectedRecent);
    }

    private void OnOpenExisting(object? sender, RoutedEventArgs e)
    {
        // No file dialog yet: this shell has nothing to open. It goes straight to the sample show so
        // the rest of the screens are reachable.
        if (DataContext is LauncherViewModel vm)
            vm.Open(null);
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
        => new SettingsWindow { DataContext = new SettingsViewModel() }.Show(this);
}
