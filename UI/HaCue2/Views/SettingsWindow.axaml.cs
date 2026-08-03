using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Drops one project override, so the machine's value applies again.
    /// </summary>
    /// <remarks>
    /// Journaled by the view-model: removing an override changes what the show DOES, and a project
    /// that had pinned a 150 ms panic fade and now inherits 250 ms behaves differently.
    /// </remarks>
    private void OnRevertOverride(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings && (sender as Control)?.Tag is string which)
            settings.RevertOverride(which);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
