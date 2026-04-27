using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using SPlayer.Core.Models;
using SPlayer.Core.Services;

namespace SPlayer.Core.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger SLog = MediaCoreLogging.GetLogger("SPlayer.PlayerViewModel");
    private static int _ffmpegInitialized;

    /// <summary>Matches the working MFPlayer.AvaloniaVideoPlayer path: full A/V, native video pixel format for the GPU path.</summary>
    private static readonly FFmpegDecoderOptions DecoderOptionsForUiPlayback = new()
    {
        EnableAudio = true,
        EnableVideo = true,
        VideoTargetPixelFormat = null,
        VideoBufferDepth = 4,
        PreferHardwareDecoding = true,
        DecoderThreadCount = 0
    };

    private readonly OutputViewModel _outputs;
    private readonly MediaPlayer _player = new();
    private readonly List<IMediaEndpoint> _attachedEndpoints = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly Dictionary<string, bool> _selectionMemory = new();
    private readonly Dictionary<string, AveStreamSelection> _aveToPlayerMemory = new();

    private bool _isScrubbing;
    private bool _eventsHooked;
    public ObservableCollection<PlayerOutputRowViewModel> OutputRows { get; } = new();
    public ObservableCollection<PlaylistDocumentViewModel> Playlists { get; } = new();

    [ObservableProperty]
    private PlaylistDocumentViewModel? _selectedPlaylist;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private double _seekNormalized;

    [ObservableProperty]
    private bool _isTransportBusy;

    [ObservableProperty]
    private bool _canPlay = true;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private bool _canStop;

    [ObservableProperty]
    private double _volumePercent = 100;

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    private bool _autoAdvance = true;

    [ObservableProperty]
    private bool _outputRoutingLocked;

    public bool IsScrubbing
    {
        get => _isScrubbing;
        set
        {
            if (_isScrubbing == value) return;
            _isScrubbing = value;
            OnPropertyChanged(nameof(IsScrubbing));
        }
    }

    public string StateLabel => _player.State.ToString();

    public PlayerViewModel(OutputViewModel outputs)
    {
        _outputs = outputs;
        _outputs.AudioEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.VideoEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.NdiEndpointModels.CollectionChanged += OnOutputPoolChanged;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _positionTimer.Tick += OnPositionTick;

        RebuildOutputRows();
        AddEmptyPlaylistTab("Playlist 1");
        SelectedPlaylist = Playlists[0];

        HookPlayerEvents();
        SyncTransportFlags();
    }

    private void EnsureFfmpeg()
    {
        if (Interlocked.Exchange(ref _ffmpegInitialized, 1) != 0) return;
        ffmpeg.RootPath = FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";
    }

    private void OnOutputRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerOutputRowViewModel.AveToPlayer)) return;
        if (sender is not PlayerOutputRowViewModel row) return;
        if (row is not { Kind: PlayerOutputKind.Ndi }) return;
        _aveToPlayerMemory[row.RowKey] = row.AveToPlayer;
        TryApplyAveStreamSelection(row);
    }

    private void OnOutputPoolChanged(object? s, NotifyCollectionChangedEventArgs e) => RebuildOutputRows();

    private void RebuildOutputRows()
    {
        foreach (var r in OutputRows)
        {
            _selectionMemory[r.RowKey] = r.IsSelected;
            if (r is { Kind: PlayerOutputKind.Ndi })
                _aveToPlayerMemory[r.RowKey] = r.AveToPlayer;
        }

        foreach (var row in OutputRows)
        {
            row.PropertyChanged -= OnOutputRowPropertyChanged;
            row.Dispose();
        }

        OutputRows.Clear();

        void AddAudio(AudioEndpointModel m)
        {
            var key = RowKey(PlayerOutputKind.Audio, m.Name);
            var sel = _selectionMemory.TryGetValue(key, out var b) && b;
            var row = new PlayerOutputRowViewModel(m, key, sel);
            row.PropertyChanged += OnOutputRowPropertyChanged;
            OutputRows.Add(row);
        }

        void AddVideo(VideoEndpointModel m)
        {
            var key = RowKey(PlayerOutputKind.Video, m.Name);
            var sel = _selectionMemory.TryGetValue(key, out var b) && b;
            var row = new PlayerOutputRowViewModel(m, key, sel);
            row.PropertyChanged += OnOutputRowPropertyChanged;
            OutputRows.Add(row);
        }

        void AddNdi(NDIEndpointModel m)
        {
            var key = RowKey(PlayerOutputKind.Ndi, m.Name);
            var sel = _selectionMemory.TryGetValue(key, out var b) && b;
            var ave = _aveToPlayerMemory.TryGetValue(key, out var ap) ? ap : AveStreamSelection.Both;
            var row = new PlayerOutputRowViewModel(m, key, sel, ave);
            row.PropertyChanged += OnOutputRowPropertyChanged;
            OutputRows.Add(row);
        }

        foreach (var a in _outputs.AudioEndpointModels) AddAudio(a);
        foreach (var v in _outputs.VideoEndpointModels) AddVideo(v);
        foreach (var n in _outputs.NdiEndpointModels) AddNdi(n);

        UpdateRoutingLockUi();
    }

    private static string RowKey(PlayerOutputKind kind, string name) => $"{kind}:{name}";

    private void HookPlayerEvents()
    {
        if (_eventsHooked) return;
        _eventsHooked = true;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.PlaybackCompleted += OnPlaybackCompleted;
        _player.PlaybackFailed += OnPlaybackFailed;
    }

    private void OnPlaybackFailed(object? sender, PlaybackFailedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = e.Exception.Message;
            SyncTransportFlags();
        });
    }

    private void OnPlaybackCompleted(object? sender, PlaybackCompletedEventArgs e)
    {
        if (e.Reason != PlaybackCompletedReason.SourceEnded) return;
        if (!AutoAdvance) return;
        Dispatcher.UIThread.Post(() => _ = AdvanceAfterTrackEndedAsync());
    }

    private async Task AdvanceAfterTrackEndedAsync()
    {
        var pl = SelectedPlaylist;
        if (pl is null || pl.Entries.Count == 0) return;
        var idx = pl.CurrentIndex;
        if (idx < 0) return;
        if (idx >= pl.Entries.Count - 1) return;
        pl.CurrentIndex = idx + 1;
        pl.SelectedEntry = pl.Entries[pl.CurrentIndex];
        await PlayCurrentSelectionAsync();
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(StateLabel));
            SyncTransportFlags();
            UpdateRoutingLockUi();
        });
    }

    private void OnPositionTick(object? s, EventArgs e)
    {
        if (IsScrubbing) return;
        var d = _player.Duration;
        var p = _player.Position;
        PositionText = FormatTime(p);
        DurationText = d.HasValue ? FormatTime(d.Value) : "—";
        if (d is { TotalSeconds: > 0 } dur)
            SeekNormalized = Math.Clamp(p.TotalSeconds / dur.TotalSeconds, 0, 1);
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.Days * 24 + t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    partial void OnVolumePercentChanged(double value)
    {
        _player.Volume = (float)Math.Clamp(value / 100.0, 0, 2);
    }

    partial void OnLoopChanged(bool value) => _player.IsLooping = value;

    [RelayCommand]
    public void CommitSeek()
    {
        var d = _player.Duration;
        if (d is null || d.Value <= TimeSpan.Zero) return;
        _ = SeekInternalAsync(TimeSpan.FromSeconds(SeekNormalized * d.Value.TotalSeconds));
    }

    public async Task CommitSeekAsync()
    {
        var d = _player.Duration;
        if (d is null || d.Value <= TimeSpan.Zero) return;
        await SeekInternalAsync(TimeSpan.FromSeconds(SeekNormalized * d.Value.TotalSeconds));
    }

    private async Task SeekInternalAsync(TimeSpan pos)
    {
        try
        {
            await _player.SeekAsync(pos);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void SyncTransportFlags()
    {
        var st = _player.State;
        IsTransportBusy = st is PlaybackState.Opening or PlaybackState.Stopping;
        CanPlay = st is PlaybackState.Ready or PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Idle;
        CanPause = st == PlaybackState.Playing;
        CanStop = st is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ready;
        if (st is PlaybackState.Stopped or PlaybackState.Idle)
        {
            _positionTimer.Stop();
            PositionText = "0:00";
            DurationText = "0:00";
            SeekNormalized = 0;
        }
        else
        {
            _positionTimer.Start();
        }
    }

    private void UpdateRoutingLockUi()
    {
        var locked = _player.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Opening;
        OutputRoutingLocked = locked;
        foreach (var r in OutputRows)
            r.SetSelectionReadOnly(locked);
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        await PlayCurrentSelectionAsync();
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        try
        {
            await _player.PauseAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            // Endpoints are managed in the Outputs tab; do not call IMediaEndpoint.Stop on them.
            await _player.StopAsync(stopRegisteredEndpoints: false);
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PlayNextInPlaylistAsync()
    {
        var pl = SelectedPlaylist;
        if (pl is null || pl.Entries.Count == 0) return;
        if (pl.CurrentIndex < pl.Entries.Count - 1)
        {
            pl.CurrentIndex++;
            pl.SelectedEntry = pl.Entries[pl.CurrentIndex];
        }
        await PlayCurrentSelectionAsync();
    }

    [RelayCommand]
    private async Task PlayPreviousInPlaylistAsync()
    {
        var pl = SelectedPlaylist;
        if (pl is null || pl.Entries.Count == 0) return;
        if (pl.CurrentIndex > 0)
        {
            pl.CurrentIndex--;
            pl.SelectedEntry = pl.Entries[pl.CurrentIndex];
        }
        await PlayCurrentSelectionAsync();
    }

    private async Task PlayCurrentSelectionAsync()
    {
        var pl = SelectedPlaylist;
        var entry = pl?.SelectedEntry ?? pl?.Entries.FirstOrDefault();
        if (entry is null)
        {
            StatusMessage = "Nothing to play — add items to the playlist.";
            return;
        }

        pl!.CurrentIndex = pl.IndexOf(entry);
        if (pl.CurrentIndex < 0) pl.CurrentIndex = 0;

        if (!File.Exists(entry.FilePath) && !entry.FilePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"File not found: {entry.FilePath}";
            return;
        }

        await OpenAndPlayPathAsync(entry.FilePath);
    }

    private async Task OpenAndPlayPathAsync(string path)
    {
        EnsureFfmpeg();
        await ReconcileEndpointsAsync();

        if (_attachedEndpoints.Count == 0)
        {
            StatusMessage = "Select at least one started output (Audio / Video / NDI) in the list above.";
            return;
        }

        SLog.LogInformation("OpenAndPlay: attached endpoints ({N}): {List}",
            _attachedEndpoints.Count,
            string.Join(" | ", _attachedEndpoints.Select(e => $"{e.GetType().Name}:'{e.Name}'")));

        try
        {
            StatusMessage = Path.GetFileName(path);
            await _player.OpenAsync(path, GetDecoderOptions());
            ApplyDisplayClockToRouter();
            ApplyAveToAllNdiRows();
            if (_player.VideoChannel is null && WantsVideoRouting())
                StatusMessage = $"{Path.GetFileName(path)} — no video track decoded (try another file, or check decoder logs).";
            await _player.PlayAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// Differentially reconciles registered endpoints with the current selection.
    /// Only stale endpoints are removed (and stopped), only new endpoints are added.
    /// Stable (same-reference) endpoints are untouched — no stop/restart across track changes.
    /// </summary>
    private async Task ReconcileEndpointsAsync()
    {
        var desired = new List<IMediaEndpoint>();
        foreach (var row in OutputRows)
            if (row.TryGetSelectedEndpoint(out var ep))
                desired.Add(ep);

        var stale = _attachedEndpoints
            .Where(a => !desired.Any(d => ReferenceEquals(d, a))).ToList();
        var hasNovel = desired
            .Any(d => !_attachedEndpoints.Any(a => ReferenceEquals(a, d)));

        if (stale.Count == 0 && !hasNovel)
            return;

        if (_player.State is not (PlaybackState.Idle or PlaybackState.Stopped))
            await _player.StopAsync(stopRegisteredEndpoints: false);

        foreach (var ep in stale)
        {
            try { _player.RemoveEndpoint(ep); }
            catch (Exception ex) { SLog.LogDebug(ex, "ReconcileEndpoints: error removing stale endpoint (may be disposed)."); }
            _attachedEndpoints.Remove(ep);
        }

        foreach (var row in OutputRows)
        {
            if (!row.TryGetSelectedEndpoint(out var ep)) continue;
            if (_attachedEndpoints.Any(a => ReferenceEquals(a, ep))) continue;
            row.TryAddToPlayer(_player, _attachedEndpoints);
        }
    }

    private FFmpegDecoderOptions GetDecoderOptions()
    {
        // When NDI video output is active, force a CPU-accessible pixel format.
        // Hardware decoding with VideoTargetPixelFormat=null produces GPU-resident frames
        // (Data.Length == 0 on CPU) that NDIAVEndpoint's capacity check silently drops.
        foreach (var row in OutputRows)
        {
            if (!row.IsSelected || !row.IsRowAvailable) continue;
            if (row.Kind == PlayerOutputKind.Ndi &&
                row.NdiIncludesVideo &&
                row.AveToPlayer != AveStreamSelection.AudioOnly)
            {
                // Rgba32 matches the NDI target format → passthrough in VideoWriteLoop,
                // no per-frame pixel conversion. Avalonia GL also handles Rgba32 natively.
                return new FFmpegDecoderOptions
                {
                    EnableAudio = DecoderOptionsForUiPlayback.EnableAudio,
                    EnableVideo = DecoderOptionsForUiPlayback.EnableVideo,
                    VideoTargetPixelFormat = PixelFormat.Rgba32,
                    VideoBufferDepth = DecoderOptionsForUiPlayback.VideoBufferDepth,
                    PreferHardwareDecoding = DecoderOptionsForUiPlayback.PreferHardwareDecoding,
                    DecoderThreadCount = DecoderOptionsForUiPlayback.DecoderThreadCount
                };
            }
        }
        return DecoderOptionsForUiPlayback;
    }

    private bool WantsVideoRouting()
    {
        foreach (var row in OutputRows)
        {
            if (!row.IsSelected) continue;
            if (row is { Kind: PlayerOutputKind.Video })
                return true;
            if (row is { Kind: PlayerOutputKind.Ndi, NdiIncludesVideo: true, AveToPlayer: not AveStreamSelection.AudioOnly })
                return true;
        }
        return false;
    }

    private void ApplyAveToAllNdiRows()
    {
        foreach (var row in OutputRows)
        {
            if (row is { Kind: PlayerOutputKind.Ndi })
                TryApplyAveStreamSelection(row);
        }
    }

    private void TryApplyAveStreamSelection(PlayerOutputRowViewModel row)
    {
        if (row is not { Kind: PlayerOutputKind.Ndi }) return;
        if (!row.IsSelected || !row.IsRowAvailable) return;
        if (!row.TryGetSelectedEndpoint(out var ep) || ep is not IAVEndpoint av) return;
        try
        {
            _player.SetAveStreamSelection(av, row.AveToPlayer);
            SLog.LogDebug("TryApplyAve: row '{Name}' mode={Mode}", row.Name, row.AveToPlayer);
        }
        catch (Exception ex)
        {
            SLog.LogDebug(ex, "TryApplyAve: could not set mode for NDI row '{Name}' (expected if NDI is stopped or not yet registered).", row.Name);
        }
    }

    /// <summary>
    /// Same idea as <c>AVRouter.SetClock(_videoOutput.Clock)</c> in MFPlayer.AvaloniaVideoPlayer: use the
    /// display endpoint clock when video is being decoded. Clears a prior override when the file has no video.
    /// </summary>
    private void ApplyDisplayClockToRouter()
    {
        if (_player.VideoChannel is null)
        {
            SLog.LogDebug("ApplyDisplayClock: no decoded video — SetClock(null).");
            _player.Router.SetClock(null);
            return;
        }
        foreach (var ep in _attachedEndpoints)
        {
            if (ep is IClockCapableEndpoint clocked and IVideoEndpoint)
            {
                SLog.LogInformation(
                    "ApplyDisplayClock: using {Type} '{Name}' clock (VideoPts) for pull + push gating",
                    ep.GetType().Name, ep.Name);
                _player.Router.SetClock(clocked.Clock);
                return;
            }
        }
        SLog.LogWarning(
            "ApplyDisplayClock: no IClockCapable+IVideo in {Count} attached endpoint(s) — override not set; push video may use wrong timebase. Endpoints: {List}",
            _attachedEndpoints.Count,
            string.Join(" | ", _attachedEndpoints.Select(e => $"{e.GetType().Name}:'{e.Name}'")));
    }

    [RelayCommand]
    private async Task LoadM3uAsync(Window window)
    {
        if (window.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open M3U playlist",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("M3U playlist") { Patterns = ["*.m3u", "*.m3u8"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.Path.LocalPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var parsed = PlaylistIO.ReadM3u(path);
            var vm = new PlaylistDocumentViewModel { Title = parsed.Title };
            foreach (var line in parsed.Entries)
                vm.Entries.Add(new PlaylistEntry(line.FilePath, line.Title));
            Playlists.Add(vm);
            SelectedPlaylist = vm;
            StatusMessage = $"Loaded {vm.Entries.Count} item(s) from {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadM3uBatchAsync(Window window)
    {
        if (window.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open batch list (one M3U path per line)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Batch list") { Patterns = ["*.m3ubatch", "*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        var f = files.FirstOrDefault();
        if (f is null) return;
        var batchPath = f.Path.LocalPath;
        if (string.IsNullOrEmpty(batchPath)) return;
        try
        {
            var paths = PlaylistIO.ReadM3uBatchList(batchPath);
            var loaded = 0;
            foreach (var m3uPath in paths)
            {
                if (!File.Exists(m3uPath)) continue;
                var parsed = PlaylistIO.ReadM3u(m3uPath);
                var vm = new PlaylistDocumentViewModel { Title = parsed.Title };
                foreach (var line in parsed.Entries)
                    vm.Entries.Add(new PlaylistEntry(line.FilePath, line.Title));
                Playlists.Add(vm);
                loaded++;
            }
            if (loaded > 0)
                SelectedPlaylist = Playlists[^1];
            StatusMessage = $"Batch: opened {loaded} playlist(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddMediaFilesAsync(Window window)
    {
        if (window.StorageProvider is not { } sp) return;
        var pl = SelectedPlaylist;
        if (pl is null) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add media files to this playlist",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Media")
                {
                    Patterns =
                    [
                        "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg", "*.opus",
                        "*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov", "*.m4v", "*.ts", "*.m2ts"
                    ]
                },
                new FilePickerFileType("All files") { Patterns = [ "*" ] }
            ]
        });
        if (files.Count == 0) return;
        var n = 0;
        foreach (var f in files)
        {
            var path = f.Path.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;
            n++;
            pl.Entries.Add(new PlaylistEntry(path, Path.GetFileNameWithoutExtension(path)));
        }
        if (n > 0)
            StatusMessage = $"Added {n} file(s) to {pl.Title}.";
    }

    [RelayCommand]
    private async Task SaveCurrentPlaylistAsAsync(Window window)
    {
        if (window.StorageProvider is not { } sp) return;
        var pl = SelectedPlaylist;
        if (pl is null) return;
        if (pl.Entries.Count == 0)
        {
            StatusMessage = "This playlist is empty; nothing to save.";
            return;
        }
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save playlist as M3U",
            SuggestedFileName = PlaylistIO.ToSafeFileName(pl.Title) + ".m3u",
            DefaultExtension = "m3u",
            FileTypeChoices = [ new FilePickerFileType("M3U") { Patterns = [ "*.m3u" ] } ],
            ShowOverwritePrompt = true
        });
        if (file is null) return;
        try
        {
            var lines = ToLines(pl);
            var local = file.Path.LocalPath;
            if (!string.IsNullOrEmpty(local))
            {
                PlaylistIO.WriteM3u(local, lines, pl.Title);
            }
            else
            {
                await using var s = await file.OpenWriteAsync();
                PlaylistIO.WriteM3u(s, lines, pl.Title, null, leaveStreamOpen: false);
            }
            StatusMessage = $"Saved {pl.Entries.Count} item(s) to {file.Name}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveAllPlaylistsToFolderAsync(Window window)
    {
        if (window.StorageProvider is not { } sp) return;
        if (Playlists.Count == 0) return;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose folder to save all playlists (M3U + index file)",
            AllowMultiple = false
        });
        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;
        var dir = folder.Path.LocalPath;
        if (string.IsNullOrEmpty(dir))
        {
            StatusMessage = "That folder has no local path; cannot save.";
            return;
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var writtenM3U = new List<string>();
            foreach (var pl in Playlists)
            {
                if (pl.Entries.Count == 0) continue;
                var path = NextUniqueM3UPath(dir, pl.Title, used);
                used.Add(path);
                PlaylistIO.WriteM3u(path, ToLines(pl), pl.Title);
                writtenM3U.Add(path);
            }
            if (writtenM3U.Count == 0)
            {
                StatusMessage = "All tabs are empty; nothing was written.";
                return;
            }
            var indexPath = Path.Combine(dir, "splayer-playlists.m3ubatch");
            var rel = writtenM3U.Select(p => Path.GetRelativePath(dir, Path.GetFullPath(p))).ToList();
            PlaylistIO.WriteM3uBatchList(indexPath, rel, "SPlayer — index: open this file with Load batch to restore every saved playlist as a tab.");
            StatusMessage = $"Saved {writtenM3U.Count} M3U file(s) and splayer-playlists.m3ubatch in: {dir}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static IReadOnlyList<PlaylistIO.PlaylistLine> ToLines(PlaylistDocumentViewModel pl) =>
        pl.Entries.Select(e => new PlaylistIO.PlaylistLine
        {
            FilePath = e.FilePath,
            Title = e.Title
        }).ToList();

    private static string NextUniqueM3UPath(string directory, string title, ISet<string> usedThisSession)
    {
        var baseName = PlaylistIO.ToSafeFileName(title);
        var path = Path.Combine(directory, baseName + ".m3u");
        if (IsFree(path, usedThisSession)) return path;
        for (var i = 2; i < 10_000; i++)
        {
            path = Path.Combine(directory, baseName + "_" + i + ".m3u");
            if (IsFree(path, usedThisSession)) return path;
        }
        path = Path.Combine(directory, "playlist_" + Guid.NewGuid().ToString("N")[..8] + ".m3u");
        return path;

        bool IsFree(string p, ISet<string> u) =>
            !u.Contains(p) && !File.Exists(p);
    }

    [RelayCommand]
    private void AddPlaylistTab()
    {
        AddEmptyPlaylistTab($"Playlist {Playlists.Count + 1}");
        SelectedPlaylist = Playlists[^1];
    }

    private void AddEmptyPlaylistTab(string title)
    {
        var vm = new PlaylistDocumentViewModel { Title = title };
        Playlists.Add(vm);
    }

    [RelayCommand]
    private void RemoveSelectedPlaylist()
    {
        if (Playlists.Count <= 1) return;
        var cur = SelectedPlaylist;
        if (cur is null) return;
        var ix = Playlists.IndexOf(cur);
        Playlists.Remove(cur);
        SelectedPlaylist = Playlists[Math.Max(0, ix - 1)];
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _outputs.AudioEndpointModels.CollectionChanged -= OnOutputPoolChanged;
        _outputs.VideoEndpointModels.CollectionChanged -= OnOutputPoolChanged;
        _outputs.NdiEndpointModels.CollectionChanged -= OnOutputPoolChanged;

        foreach (var row in OutputRows)
        {
            row.PropertyChanged -= OnOutputRowPropertyChanged;
            row.Dispose();
        }
        OutputRows.Clear();

        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.PlaybackCompleted -= OnPlaybackCompleted;
        _player.PlaybackFailed -= OnPlaybackFailed;
        _player.Dispose();
    }
}
