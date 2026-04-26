using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.PortAudio;
using SPlayer.Core.Dialogs;
using SPlayer.Core.Dialogs.DialogModels;
using SPlayer.Core.Models;

namespace SPlayer.Core.ViewModels;

public partial class OutputViewModel : ObservableObject 
{
    [ObservableProperty]
    private ObservableCollection<AudioEndpointModel> _audioEndpointModels = new();

    private readonly PortAudioEngine _paEngine;

    public OutputViewModel()
    {
        _paEngine = new PortAudioEngine();
        _paEngine.Initialize();
    }

    [RelayCommand]
    private async Task AddAudioEndpoint(Window parent)
    {
        var dialog = new AddAudioEndpointDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = new AddAudioEndpointViewModel(_paEngine)
        };
        var result = await dialog.ShowDialog<PortAudioEndpoint?>(parent);
        if (result is not null)
            AudioEndpointModels.Add(new AudioEndpointModel(result));;
    }
}