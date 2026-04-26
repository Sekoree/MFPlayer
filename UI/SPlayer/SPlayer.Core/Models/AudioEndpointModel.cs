using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media.Endpoints;

namespace SPlayer.Core.Models;

public partial class AudioEndpointModel :ObservableObject
{
    private readonly IAudioEndpoint _audioEndpoint;
    
    public string Name => _audioEndpoint.Name;
    
    public int? Channels => _audioEndpoint.NegotiatedFormat?.Channels;
    
    public int? SampleRate => _audioEndpoint.NegotiatedFormat?.SampleRate;
    
    public bool Open => _audioEndpoint.IsRunning;

    public AudioEndpointModel(IAudioEndpoint audioEndpoint)
    {
        _audioEndpoint = audioEndpoint;
    }

    [RelayCommand]
    private async Task Start()
    {
        await this._audioEndpoint.StartAsync();
        this.OnPropertyChanged(nameof(Open));
        this.OnPropertyChanged(nameof(Channels));
        this.OnPropertyChanged(nameof(SampleRate));
    }
    
    [RelayCommand]
    private async Task Stop()
    {
        await this._audioEndpoint.StopAsync();
        this.OnPropertyChanged(nameof(Open));
    }
}