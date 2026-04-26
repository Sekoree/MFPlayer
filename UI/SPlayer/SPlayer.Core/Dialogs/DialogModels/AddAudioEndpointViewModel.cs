using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.PortAudio;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

public partial class AddAudioEndpointViewModel : ViewModelBase
{
    private readonly IAudioEngine _audioEngine;
    
    [ObservableProperty]
    private AudioHostApiInfo? _selectedHostApi;
    
    [ObservableProperty]
    private ObservableCollection<AudioHostApiInfo> _hostApis;
    
    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;
    
    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _devices;
    
    [ObservableProperty]
    private int _preferredSampleRate = 48_000;

    public AddAudioEndpointViewModel()
    {
        _audioEngine = new PortAudioEngine();
        _hostApis = new ObservableCollection<AudioHostApiInfo>(_audioEngine.GetHostApis().Where(x => x.DeviceCount > 0));
        _selectedHostApi = _hostApis.FirstOrDefault();
        _devices = new ObservableCollection<AudioDeviceInfo>(_audioEngine.GetDevices().Where(x => x.HostApiIndex == _selectedHostApi?.Index));
        _selectedDevice = _devices.FirstOrDefault();
    }
    
    public AddAudioEndpointViewModel(IAudioEngine audioEngine)
    {
        _audioEngine = audioEngine;
        _hostApis = new ObservableCollection<AudioHostApiInfo>(_audioEngine.GetHostApis().Where(x => x.DeviceCount > 0));
        _selectedHostApi = _hostApis.FirstOrDefault();
        _devices = new ObservableCollection<AudioDeviceInfo>(_audioEngine.GetDevices().Where(x => x.HostApiIndex == _selectedHostApi?.Index));
        _selectedDevice = _devices.FirstOrDefault();
    }

    partial void OnSelectedHostApiChanged(AudioHostApiInfo? value)
    {
        Devices = new ObservableCollection<AudioDeviceInfo>(_audioEngine.GetDevices().Where(x => x.HostApiIndex == value?.Index));
        SelectedDevice = Devices.FirstOrDefault();
    }

    [RelayCommand]
    private void AddDevice(Window dialog)
    {
        if (SelectedDevice == null)
            return;
        var paEndpoint = PortAudioEndpoint.Create(SelectedDevice, new AudioFormat(PreferredSampleRate, SelectedDevice.MaxOutputChannels));
        dialog.Close(paEndpoint);
    }
    
    [RelayCommand]
    private void Cancel(Window dialog)
    {
        dialog.Close(null);
    }
}