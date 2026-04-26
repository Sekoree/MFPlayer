using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NDILib;
using S.Media.Core.Media;
using S.Media.NDI;

namespace SPlayer.Core.Models;

public record NdiOutputConfig(
    string SenderName,
    AudioFormat? AudioFormat,
    VideoFormat? VideoFormat,
    NDIEndpointPreset Preset);

public partial class NDIEndpointModel : ObservableObject
{
    private readonly NdiOutputConfig _config;
    private NDISender? _sender;
    private NDIAVEndpoint? _endpoint;

    public string Name => _config.SenderName;
    public string Info { get; }
    public bool Open => _endpoint?.IsRunning ?? false;

    public Action? RemoveRequestedAction { get; set; }

    public NDIEndpointModel(NdiOutputConfig config)
    {
        _config = config;
        Info = (config.AudioFormat.HasValue, config.VideoFormat.HasValue) switch
        {
            (true, true)  => "Audio + Video",
            (true, false) => "Audio only",
            (false, true) => "Video only",
            _             => "—"
        };
    }

    [RelayCommand]
    private async Task Start()
    {
        // Clean up any prior partial state before creating a new sender.
        await CleanupAsync();

        var result = NDISender.Create(out _sender, _config.SenderName);
        if (result != 0 || _sender is null)
        {
            OnPropertyChanged(nameof(Open));
            return;
        }

        _endpoint = NDIAVEndpoint.Create(
            _sender,
            _config.VideoFormat,
            _config.AudioFormat,
            _config.Preset,
            _config.SenderName);

        await _endpoint.StartAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Stop()
    {
        await CleanupAsync();
        OnPropertyChanged(nameof(Open));
    }

    [RelayCommand]
    private async Task Restart()
    {
        await Stop();
        await Start();
    }

    [RelayCommand]
    private async Task Remove()
    {
        await CleanupAsync();
        RemoveRequestedAction?.Invoke();
    }

    private async Task CleanupAsync()
    {
        if (_endpoint != null)
        {
            if (_endpoint.IsRunning)
                await _endpoint.StopAsync();
            _endpoint.Dispose();
            _endpoint = null;
        }

        _sender?.Dispose();
        _sender = null;
    }
}
