using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Media;
using S.Media.NDI;
using SPlayer.Core.Models;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

/// <summary>UI-friendly wrapper around <see cref="PixelFormat"/> that pairs the
/// raw enum with a human-readable label and a short hint string. Used to drive
/// the pixel-format dropdown on the Add NDI Output dialog.</summary>
public sealed record NdiPixelFormatChoice(PixelFormat Value, string Label, string Hint)
{
    public override string ToString() => Label;
}

public partial class AddNDIEndpointViewModel : ViewModelBase
{
    /// <summary>0 = audio+video, 1 = audio only, 2 = video only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoEnabled))]
    private int _streamModeIndex;

    /// <summary>True when the chosen stream mode includes a video track, so
    /// the pixel-format dropdown is meaningful. Used to disable the format
    /// row in audio-only mode without hiding it entirely (preserves layout).</summary>
    public bool IsVideoEnabled => StreamModeIndex != 1;

    [ObservableProperty]
    private string _senderName = "MFPlayer Output";

    [ObservableProperty]
    private NDIEndpointPreset _selectedPreset = NDIEndpointPreset.Balanced;

    /// <summary>
    /// Send-side pixel format. NDI's send pipeline supports BGRA, RGBA, UYVY,
    /// NV12 and I420 (Yuv420p) on the wire; pick UYVY/NV12/I420 to lower
    /// bandwidth on HD content, or RGBA/BGRA for compatibility / 32-bit
    /// alpha. NDIAVEndpoint internally falls back to RGBA on UHD anyway
    /// (color-space safety, see NDI SDK §21.1).
    /// </summary>
    [ObservableProperty]
    private NdiPixelFormatChoice _selectedPixelFormat;

    public ObservableCollection<NDIEndpointPreset> Presets { get; } = new([
        NDIEndpointPreset.Balanced,
        NDIEndpointPreset.LowLatency,
        NDIEndpointPreset.UltraLowLatency,
        NDIEndpointPreset.Safe
    ]);

    public ObservableCollection<NdiPixelFormatChoice> PixelFormats { get; } = new([
        // §Auto-pixel-format — sentinel value PixelFormat.Unknown is interpreted
        // by NDIAVEndpoint as "passthrough whatever the source produces". This
        // is the recommended choice for NDI source → NDI output (no conversion,
        // no color-space mis-tagging) and works fine for FFmpeg sources too
        // (the supported wire formats RGBA/BGRA/UYVY/NV12/I420 cover most cases;
        // the endpoint converts only for 10-bit / exotic source formats).
        new NdiPixelFormatChoice(PixelFormat.Unknown, "Auto",          "Recommended. Sends whatever the source produces (passthrough)."),
        new NdiPixelFormatChoice(PixelFormat.Rgba32,  "RGBA (32-bit)", "Force RGBA. Compatible with every receiver."),
        new NdiPixelFormatChoice(PixelFormat.Bgra32,  "BGRA (32-bit)", "Same bandwidth as RGBA; useful when source is BGRA."),
        new NdiPixelFormatChoice(PixelFormat.Uyvy422, "UYVY (4:2:2)",  "16 bpp; lower bandwidth, no alpha."),
        new NdiPixelFormatChoice(PixelFormat.Nv12,    "NV12 (4:2:0)",  "12 bpp; lowest bandwidth packed YUV."),
        new NdiPixelFormatChoice(PixelFormat.Yuv420p, "I420 (4:2:0)",  "12 bpp planar; matches many software encoders."),
    ]);

    public NdiOutputConfig? CreatedConfig { get; private set; }

    public AddNDIEndpointViewModel()
    {
        // Default to Auto — passthrough is the safest choice for live sources
        // (NDI input, screen capture, OBS) and a no-op overhead for already-
        // RGBA content. Users can still pick a specific format from the list.
        _selectedPixelFormat = PixelFormats[0];
    }

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
        // The pixel format IS authoritative: NDIAVEndpoint locks the on-wire
        // FourCC to this value (with a UHD-safety fallback to RGBA inside the
        // endpoint when the source is >1920×1080).
        var pixelFormat = SelectedPixelFormat?.Value ?? PixelFormat.Rgba32;
        VideoFormat? videoFormat = includeVideo
            ? VideoFormat.Create(1920, 1080, pixelFormat, 30.0)
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
