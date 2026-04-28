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

    /// <summary>Pressing Enter inside the bottom-bar Seek-to TextBox triggers
    /// the SeekToTimestamp command so the user doesn't have to click "Go".</summary>
    private void SeekToInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not PlayerViewModel vm) return;
        if (vm.SeekToTimestampCommand.CanExecute(null))
        {
            vm.SeekToTimestampCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keyboard shortcuts on the playlist list:
    ///   Delete     → remove the selected entry
    ///   Enter      → play the selected entry
    ///   Alt+Up     → move selected up
    ///   Alt+Down   → move selected down
    /// </summary>
    private void PlaylistList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.DataContext is not PlaylistDocumentViewModel doc) return;

        var alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt;

        switch (e.Key)
        {
            case Key.Delete:
                if (doc.RemoveSelectedCommand.CanExecute(null))
                {
                    doc.RemoveSelectedCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (DataContext is PlayerViewModel vm && vm.PlayCommand.CanExecute(null))
                {
                    vm.PlayCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Up when alt:
                if (doc.SelectedEntry is { } upE && doc.MoveEntryUpCommand.CanExecute(upE))
                {
                    doc.MoveEntryUpCommand.Execute(upE);
                    e.Handled = true;
                }
                break;

            case Key.Down when alt:
                if (doc.SelectedEntry is { } dnE && doc.MoveEntryDownCommand.CanExecute(dnE))
                {
                    doc.MoveEntryDownCommand.Execute(dnE);
                    e.Handled = true;
                }
                break;
        }
    }
}
