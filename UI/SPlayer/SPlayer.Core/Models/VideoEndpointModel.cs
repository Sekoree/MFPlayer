using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media.Endpoints;
using SPlayer.Core.Views;

namespace SPlayer.Core.Models;

public partial class VideoEndpointModel : ObservableObject
{
    private readonly VideoOutputWindow _window;

    public string Name { get; }
    public string Info { get; }
    public bool Open => _window.VideoEndpoint.IsRunning;

    public Action? RemoveRequestedAction { get; set; }

    /// <summary>Used by the player view to route decoded video to this output.</summary>
    public IVideoEndpoint Endpoint => _window.VideoEndpoint;

    public VideoEndpointModel(string name, string info, VideoOutputWindow window)
    {
        Name = name;
        Info = info;
        _window = window;
        _window.Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window.Closed -= OnWindowClosed;
        RemoveRequestedAction?.Invoke();
    }

    [RelayCommand]
    private async Task Start()
    {
        await _window.VideoEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Stop()
    {
        await _window.VideoEndpoint.StopAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Restart()
    {
        await _window.VideoEndpoint.StopAsync();
        await _window.VideoEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private void Remove() => _window.Close();
}
