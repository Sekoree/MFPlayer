using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media.Endpoints;

namespace SPlayer.Core.Models;

public partial class AudioEndpointModel : ObservableObject
{
    private readonly IAudioEndpoint _audioEndpoint;

    public string Name => _audioEndpoint.Name;
    public int? Channels => _audioEndpoint.NegotiatedFormat?.Channels;
    public int? SampleRate => _audioEndpoint.NegotiatedFormat?.SampleRate;
    public bool Open => _audioEndpoint.IsRunning;
    public string Info => SampleRate.HasValue && Channels.HasValue
        ? $"{SampleRate} Hz · {Channels} ch"
        : "Not started";

    public Action? RemoveRequestedAction { get; set; }

    public AudioEndpointModel(IAudioEndpoint audioEndpoint)
    {
        _audioEndpoint = audioEndpoint;
    }

    [RelayCommand]
    private async Task Start()
    {
        await _audioEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(Info));
    }

    [RelayCommand]
    private async Task Stop()
    {
        await _audioEndpoint.StopAsync();
        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Info));
    }

    [RelayCommand]
    private async Task Restart()
    {
        await _audioEndpoint.StopAsync();
        await _audioEndpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SampleRate));
        OnPropertyChanged(nameof(Info));
    }

    [RelayCommand]
    private void Remove() => RemoveRequestedAction?.Invoke();
}
