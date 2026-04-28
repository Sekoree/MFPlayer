using Avalonia.Controls;
using Avalonia.Interactivity;
using SPlayer.Core.Dialogs.DialogModels;

namespace SPlayer.Core.Dialogs;

public partial class PlayNDISourceDialog : Window
{
    public PlayNDISourceDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlayNDISourceViewModel vm)
            vm.StartDiscovery();
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is PlayNDISourceViewModel vm)
            vm.Dispose();
    }
}
