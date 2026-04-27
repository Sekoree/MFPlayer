using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using S.Media.Core.Media;
using S.Media.Playback;
using SPlayer.Core.Models;

namespace SPlayer.Core.ViewModels;

public enum PlayerOutputKind
{
    Audio,
    Video,
    Ndi
}

/// <summary>One row in the player output-routing list (mirrors a row from the Outputs tab).</summary>
public sealed partial class PlayerOutputRowViewModel : ObservableObject, IDisposable
{
    private readonly AudioEndpointModel? _audio;
    private readonly VideoEndpointModel? _video;
    private readonly NDIEndpointModel? _ndi;

    public PlayerOutputKind Kind { get; }
    public string Name { get; }
    /// <summary>Short label: Audio, Video, or NDI.</summary>
    public string Subtitle { get; }

    [ObservableProperty]
    private bool _isSelected;

    public bool IsSelectedEnabled { get; private set; } = true;

    public string RowKey { get; }

    public bool IsRowAvailable
    {
        get
        {
            if (_audio is not null) return _audio.Open;
            if (_video is not null) return _video.Open;
            if (_ndi is not null) return _ndi is { Open: true, AveEndpoint: not null };
            return false;
        }
    }

    public bool IsOffline => !IsRowAvailable;

    /// <summary>NDI only: the sender was configured with a video format (so we may expect decoded video).</summary>
    public bool NdiIncludesVideo => _ndi is not null && _ndi.ConfigIncludesVideo;

    public bool ShowAveStreamPicker => Kind == PlayerOutputKind.Ndi;

    /// <summary>NDI only: per-playback <see cref="AveStreamSelection"/> for this NDI sender in <see cref="MediaPlayer"/>.</summary>
    [ObservableProperty]
    private AveStreamSelection _aveToPlayer = AveStreamSelection.Both;

    partial void OnAveToPlayerChanged(AveStreamSelection value) => OnPropertyChanged(nameof(NdiAveToPlayerIndex));

    /// <summary>0 = both, 1 = audio only, 2 = video only. NDI rows only; other kinds return 0 and ignore set.</summary>
    public int NdiAveToPlayerIndex
    {
        get
        {
            if (Kind != PlayerOutputKind.Ndi) return 0;
            return AveToPlayer switch
            {
                AveStreamSelection.Both => 0,
                AveStreamSelection.AudioOnly => 1,
                AveStreamSelection.VideoOnly => 2,
                _ => 0
            };
        }
        set
        {
            if (Kind != PlayerOutputKind.Ndi) return;
            var v = value switch
            {
                0 => AveStreamSelection.Both,
                1 => AveStreamSelection.AudioOnly,
                2 => AveStreamSelection.VideoOnly,
                _ => AveStreamSelection.Both
            };
            if (AveToPlayer == v) return;
            AveToPlayer = v;
        }
    }

    public PlayerOutputRowViewModel(AudioEndpointModel m, string preservedKey, bool initialSelected)
    {
        _audio = m;
        Kind = PlayerOutputKind.Audio;
        Name = m.Name;
        Subtitle = "Audio";
        RowKey = preservedKey;
        _isSelected = initialSelected;
        m.PropertyChanged += OnModelPropertyChanged;
    }

    public PlayerOutputRowViewModel(VideoEndpointModel m, string preservedKey, bool initialSelected)
    {
        _video = m;
        Kind = PlayerOutputKind.Video;
        Name = m.Name;
        Subtitle = "Video";
        RowKey = preservedKey;
        _isSelected = initialSelected;
        m.PropertyChanged += OnModelPropertyChanged;
    }

    public PlayerOutputRowViewModel(NDIEndpointModel m, string preservedKey, bool initialSelected, AveStreamSelection initialAveToPlayer = AveStreamSelection.Both)
    {
        _ndi = m;
        Kind = PlayerOutputKind.Ndi;
        Name = m.Name;
        Subtitle = "NDI";
        RowKey = preservedKey;
        _isSelected = initialSelected;
        AveToPlayer = initialAveToPlayer;
        m.PropertyChanged += OnModelPropertyChanged;
    }

    public void SetSelectionReadOnly(bool locked)
    {
        IsSelectedEnabled = !locked;
        OnPropertyChanged(nameof(IsSelectedEnabled));
    }

    /// <summary>Resolves the media endpoint for this row when it is selected and available; does not register with <see cref="MediaPlayer"/>.</summary>
    public bool TryGetSelectedEndpoint(out IMediaEndpoint endpoint)
    {
        endpoint = null!;
        if (!IsSelected || !IsRowAvailable) return false;
        if (_audio is not null) { endpoint = _audio.Endpoint; return true; }
        if (_video is not null) { endpoint = _video.Endpoint; return true; }
        if (_ndi?.AveEndpoint is { } av) { endpoint = av; return true; }
        return false;
    }

    public bool TryAddToPlayer(MediaPlayer player, System.Collections.Generic.List<IMediaEndpoint> tracking)
    {
        if (!TryGetSelectedEndpoint(out var ep)) return false;
        if (_audio is not null) player.AddEndpoint(_audio.Endpoint);
        else if (_video is not null) player.AddEndpoint(_video.Endpoint);
        else if (_ndi?.AveEndpoint is { } av) player.AddEndpoint(av);
        tracking.Add(ep);
        return true;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null and not "Open")
            return;
        OnPropertyChanged(nameof(IsRowAvailable));
        OnPropertyChanged(nameof(IsOffline));
    }

    public void Dispose()
    {
        if (_audio is not null) _audio.PropertyChanged -= OnModelPropertyChanged;
        if (_video is not null) _video.PropertyChanged -= OnModelPropertyChanged;
        if (_ndi is not null) _ndi.PropertyChanged -= OnModelPropertyChanged;
    }
}
