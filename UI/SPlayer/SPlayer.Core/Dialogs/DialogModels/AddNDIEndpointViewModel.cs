using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media;
using S.Media.NDI;
using SPlayer.Core.Models;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

public partial class AddNDIEndpointViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _senderName = "MFPlayer Output";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _hasAudio = true;

    [ObservableProperty]
    private int _audioSampleRate = 48_000;

    [ObservableProperty]
    private int _audioChannels = 2;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _hasVideo = false;

    [ObservableProperty]
    private int _videoWidth = 1920;

    [ObservableProperty]
    private int _videoHeight = 1080;

    [ObservableProperty]
    private double _videoFrameRate = 60.0;

    [ObservableProperty]
    private NDIEndpointPreset _selectedPreset = NDIEndpointPreset.Balanced;

    public ObservableCollection<NDIEndpointPreset> Presets { get; } = new([
        NDIEndpointPreset.Balanced,
        NDIEndpointPreset.LowLatency,
        NDIEndpointPreset.UltraLowLatency,
        NDIEndpointPreset.Safe
    ]);

    public NdiOutputConfig? CreatedConfig { get; private set; }

    private bool CanAdd() => HasAudio || HasVideo;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add(Window dialog)
    {
        VideoFormat? videoFormat = HasVideo
            ? VideoFormat.Create(VideoWidth, VideoHeight, PixelFormat.Uyvy422, VideoFrameRate)
            : null;

        AudioFormat? audioFormat = HasAudio
            ? new AudioFormat(AudioSampleRate, AudioChannels)
            : null;

        CreatedConfig = new NdiOutputConfig(SenderName, audioFormat, videoFormat, SelectedPreset);
        dialog.Close(true);
    }

    [RelayCommand]
    private void Cancel(Window dialog) => dialog.Close(false);
}
