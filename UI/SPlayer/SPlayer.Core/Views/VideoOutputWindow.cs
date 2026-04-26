using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using S.Media.Avalonia;
using S.Media.Core.Media;
using MediaPixelFormat = S.Media.Core.Media.PixelFormat;

namespace SPlayer.Core.Views;

public sealed class VideoOutputWindow : Window
{
    public AvaloniaOpenGlVideoEndpoint VideoEndpoint { get; }

    private readonly int _hintWidth;
    private readonly int _hintHeight;
    private bool _opened;
    private bool _closed;

    public VideoOutputWindow(string name, Screen? targetScreen)
    {
        Title = name;
        Background = Brushes.Black;

        VideoEndpoint = new AvaloniaOpenGlVideoEndpoint();
        Content = VideoEndpoint;

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (targetScreen != null)
            Position = new PixelPoint(targetScreen.Bounds.X, targetScreen.Bounds.Y);

        _hintWidth  = targetScreen?.Bounds.Width  ?? 1920;
        _hintHeight = targetScreen?.Bounds.Height ?? 1080;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_opened) return;
        _opened = true;

        VideoEndpoint.Open(
            title:  string.Empty,
            width:  _hintWidth,
            height: _hintHeight,
            format: VideoFormat.Create(_hintWidth, _hintHeight, MediaPixelFormat.Bgra32, 30));

        // Fullscreen after the window has landed on the target screen.
        WindowState = WindowState.FullScreen;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;

        try
        {
            if (VideoEndpoint.IsRunning)
                await VideoEndpoint.StopAsync();
        }
        finally
        {
            // §3.40j — the window is now closed, control detached from visual tree.
            VideoEndpoint.Dispose();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
