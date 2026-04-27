using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Views;

public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SeekSlider.PointerPressed += OnSeekPointerPressed;
        SeekSlider.PointerReleased += OnSeekPointerReleased;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SeekSlider.PointerPressed -= OnSeekPointerPressed;
        SeekSlider.PointerReleased -= OnSeekPointerReleased;
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerViewModel v)
            v.IsScrubbing = true;
    }

    private async void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not PlayerViewModel v) return;
        v.IsScrubbing = false;
        await v.CommitSeekAsync();
    }

    private void PlaylistEntry_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        if (vm.PlayCommand.CanExecute(null))
            vm.PlayCommand.Execute(null);
    }
}
