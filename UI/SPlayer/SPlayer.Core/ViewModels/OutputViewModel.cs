using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.PortAudio;
using SPlayer.Core.Dialogs;
using SPlayer.Core.Dialogs.DialogModels;
using SPlayer.Core.Models;
using SPlayer.Core.Views;

namespace SPlayer.Core.ViewModels;

public partial class OutputViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<AudioEndpointModel> _audioEndpointModels = new();

    [ObservableProperty]
    private ObservableCollection<VideoEndpointModel> _videoEndpointModels = new();

    [ObservableProperty]
    private ObservableCollection<NDIEndpointModel> _ndiEndpointModels = new();

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
        if (result is null) return;

        var model = new AudioEndpointModel(result);
        model.RemoveRequestedAction = () => AudioEndpointModels.Remove(model);
        AudioEndpointModels.Add(model);
        await model.StartCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task AddVideoEndpoint(Window parent)
    {
        var vm = new AddVideoEndpointViewModel(parent.Screens.All);
        var dialog = new AddVideoEndpointDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = vm
        };
        var success = await dialog.ShowDialog<bool>(parent);
        if (!success) return;

        var screen = vm.SelectedScreen?.Screen;
        var info = screen != null
            ? $"{screen.Bounds.Width} × {screen.Bounds.Height}"
            : "Fullscreen";

        var outputWindow = new VideoOutputWindow(vm.Title, screen);
        outputWindow.Show();

        var model = new VideoEndpointModel(vm.Title, info, outputWindow);
        model.RemoveRequestedAction = () => VideoEndpointModels.Remove(model);
        VideoEndpointModels.Add(model);
        await model.StartCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task AddNdiEndpoint(Window parent)
    {
        var vm = new AddNDIEndpointViewModel();
        var dialog = new AddNDIEndpointDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = vm
        };
        var success = await dialog.ShowDialog<bool>(parent);
        if (!success || vm.CreatedConfig is null) return;

        var model = new NDIEndpointModel(vm.CreatedConfig);
        model.RemoveRequestedAction = () => NdiEndpointModels.Remove(model);
        NdiEndpointModels.Add(model);
        await model.StartCommand.ExecuteAsync(null);
    }
}
