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
    /// <summary>0 = audio+video, 1 = audio only, 2 = video only.</summary>
    [ObservableProperty]
    private int _streamModeIndex;

    [ObservableProperty]
    private string _senderName = "MFPlayer Output";

    [ObservableProperty]
    private NDIEndpointPreset _selectedPreset = NDIEndpointPreset.Balanced;

    public ObservableCollection<NDIEndpointPreset> Presets { get; } = new([
        NDIEndpointPreset.Balanced,
        NDIEndpointPreset.LowLatency,
        NDIEndpointPreset.UltraLowLatency,
        NDIEndpointPreset.Safe
    ]);

    public NdiOutputConfig? CreatedConfig { get; private set; }

    [RelayCommand]
    private void Add(Window dialog)
    {
        var (includeAudio, includeVideo) = StreamModeIndex switch
        {
            1 => (true, false),  // audio only
            2 => (false, true),  // video only
            _ => (true, true)    // 0: audio + video
        };

        // Use a placeholder video format — actual frame dimensions and fps adapt
        // per-frame from incoming content (VideoWriteLoop PTS-delta tracking).
        VideoFormat? videoFormat = includeVideo
            ? VideoFormat.Create(1920, 1080, PixelFormat.Rgba32, 30.0)
            : null;

        // 48 kHz stereo covers the vast majority of content; NDI audio format
        // is declared once at start and the router resamples if needed.
        AudioFormat? audioFormat = includeAudio
            ? new AudioFormat(48_000, 2)
            : null;

        CreatedConfig = new NdiOutputConfig(SenderName, audioFormat, videoFormat, SelectedPreset);
        dialog.Close(true);
    }

    [RelayCommand]
    private void Cancel(Window dialog) => dialog.Close(false);
}
