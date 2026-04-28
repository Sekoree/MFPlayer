using System;
using S.Media.Core.Media.Endpoints;
using SPlayer.Core.Views;

namespace SPlayer.Core.Models;

/// <summary>
/// Hosts an <see cref="S.Media.Avalonia.AvaloniaOpenGlVideoEndpoint"/> inside
/// an Avalonia <see cref="VideoOutputWindow"/>. The window's Closed event
/// is forwarded as <see cref="Closed"/> so <see cref="VideoEndpointModel"/>
/// can drop the model from the outputs collection without knowing which
/// backend produced the close.
/// </summary>
public sealed class AvaloniaVideoOutputHost : IVideoOutputHost
{
    private readonly VideoOutputWindow _window;
    private bool _closeNotified;

    public AvaloniaVideoOutputHost(VideoOutputWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.Closed += OnWindowClosed;
    }

    public IVideoEndpoint VideoEndpoint => _window.VideoEndpoint;

    public bool ShowHud
    {
        get => _window.VideoEndpoint.ShowHud;
        set => _window.VideoEndpoint.ShowHud = value;
    }

    public bool LimitRenderToInputFps
    {
        get => _window.VideoEndpoint.LimitRenderToInputFps;
        set => _window.VideoEndpoint.LimitRenderToInputFps = value;
    }

    public string BackendName => "Avalonia";

    public void Close() => _window.Close();

    public event EventHandler? Closed;

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_closeNotified) return;
        _closeNotified = true;
        _window.Closed -= OnWindowClosed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
