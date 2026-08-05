using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;
using HaCue2.Engine;

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

    /// <summary>
    /// Mints a new remote token, invalidating every client using the old one.
    /// </summary>
    /// <remarks>
    /// The token is the whole of the remote API's authentication, so a laptop that left the building
    /// is a reason to make the old one stop working — and there was no way to say so.
    /// </remarks>
    private void OnRotateToken(object? sender, RoutedEventArgs e) =>
        (DataContext as SettingsViewModel)?.RotateRemoteToken();

    private void OnResetHotkeys(object? sender, RoutedEventArgs e) =>
        (DataContext as SettingsViewModel)?.ResetHotkeys();

    /// <summary>Clears one part of the media cache. Everything here re-derives from the media.</summary>
    private void OnClearCache(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || (sender as Control)?.Tag is not string kind)
            return;

        if (kind == "youtube")
        {
            if (YouTubeRuntime.Downloads.Snapshot().HasWork)
            {
                settings.CacheNote = "YouTube download in progress — wait for it to finish before clearing the cache";
                return;
            }
            settings.ClearYouTubeCache();
            YouTubeRuntime.Downloads.NotifyCacheChanged();
        }
        else
            settings.ClearWaveformCache();
    }

    private void OnOpenLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
            settings.CacheNote = settings.OpenLogFolder();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
