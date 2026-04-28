using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media.Endpoints;

namespace SPlayer.Core.Models;

public partial class VideoEndpointModel : ObservableObject
{
    private readonly IVideoOutputHost _host;

    public string Name { get; }
    public string Info { get; }
    public bool Open => _host.VideoEndpoint.IsRunning;

    /// <summary>Backend (Avalonia or SDL3) — surfaced for diagnostics and the row label.</summary>
    public string Backend => _host.BackendName;

    public bool DebugHUDEnabled
    {
        get => _host.ShowHud;
        set => _host.ShowHud = value;
    }

    /// <summary>
    /// §heavy-media-fixes phase 2 — proxies the underlying endpoint's
    /// <c>LimitRenderToInputFps</c> so the SPlayer settings layer can flip
    /// every video output without reaching across the VM/View boundary.
    /// </summary>
    public bool LimitRenderToInputFps
    {
        get => _host.LimitRenderToInputFps;
        set
        {
            if (_host.LimitRenderToInputFps == value) return;
            _host.LimitRenderToInputFps = value;
            OnPropertyChanged();
        }
    }

    public Action? RemoveRequestedAction { get; set; }

    /// <summary>Used by the player view to route decoded video to this output.</summary>
    public IVideoEndpoint Endpoint => _host.VideoEndpoint;

    public VideoEndpointModel(string name, string info, IVideoOutputHost host)
    {
        Name = name;
        Info = info;
        _host = host;
        _host.Closed += OnHostClosed;
    }

    private void OnHostClosed(object? sender, EventArgs e)
    {
        _host.Closed -= OnHostClosed;
        RemoveRequestedAction?.Invoke();
    }

    [RelayCommand]
    private async Task Start()
    {
        await _host.VideoEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Stop()
    {
        await _host.VideoEndpoint.StopAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Restart()
    {
        await _host.VideoEndpoint.StopAsync();
        await _host.VideoEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private void Remove() => _host.Close();
}
