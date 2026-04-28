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
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.NDI;
using SPlayer.Core.Dialogs;
using SPlayer.Core.Dialogs.DialogModels;
using SPlayer.Core.Models;
using SPlayer.Core.Services;

namespace SPlayer.Core.ViewModels;

public sealed class ClockChoiceItem
{
    public string Key { get; }
    public string Label { get; }

    public ClockChoiceItem(string key, string label)
    {
        Key = key;
        Label = label;
    }
}

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

    /// <summary>
    /// Tracks which kind of source the live <see cref="MediaPlayer"/> instance
    /// was constructed for. The instance is rebuilt when the user crosses
    /// between modes — file→NDI or NDI→file — because the NDI lifecycle hook
    /// added at builder time cannot be removed afterwards (and we don't want
    /// it firing for file playback).
    /// </summary>
    private enum SourceMode { File, Ndi }

    private readonly OutputViewModel _outputs;
    private readonly SettingsViewModel? _settings;
    private readonly Services.AppSettingsService? _settingsStore;
    private MediaPlayer _player = null!; // initialised in ctor via BuildFilePlayer
    private SourceMode _sourceMode = SourceMode.File;
    private string? _activeNdiSourceLabel;
    private readonly List<IMediaEndpoint> _attachedEndpoints = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly Dictionary<string, bool> _selectionMemory = new();
    private readonly Dictionary<string, AveStreamSelection> _aveToPlayerMemory = new();
    private readonly Dictionary<string, IMediaClock> _clockChoiceByKey = new(StringComparer.Ordinal);
    private bool _suppressClockChoiceChanged;
    private bool _suppressSelectionPersistence;
    private bool _hasAppliedDefaultsThisLaunch;

    private bool _isScrubbing;
    private bool _eventsHooked;
    public ObservableCollection<PlayerOutputRowViewModel> OutputRows { get; } = new();
    public ObservableCollection<PlaylistDocumentViewModel> Playlists { get; } = new();
    public ObservableCollection<ClockChoiceItem> ClockChoices { get; } = new();

    [ObservableProperty]
    private PlaylistDocumentViewModel? _selectedPlaylist;

    partial void OnSelectedPlaylistChanged(PlaylistDocumentViewModel? oldValue, PlaylistDocumentViewModel? newValue)
    {
        // Routing is locked while a session is active; avoid flipping selections
        // mid-track. The next stop/play will pick up the new playlist's overrides.
        if (_player.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Opening)
            return;
        ApplyOutputSelectionForCurrentPlaylist(forceFromDefaults: false);
        // Drop the bypass-clock state if the previous playlist forced a specific
        // clock — RefreshClockChoices reconciles after the new selection is applied.
        RefreshClockChoices();
    }

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    /// <summary>Remaining time, formatted with a leading "−" so the bottom
    /// transport bar can show <c>0:42 / 3:15 (−2:33)</c> at a glance.</summary>
    [ObservableProperty]
    private string _remainingText = "−0:00";

    /// <summary>
    /// Free-form timestamp the user can type into the seek-to input. Accepts
    /// <c>HH:MM:SS</c>, <c>MM:SS</c>, plain seconds, or a value with a single
    /// decimal point. Parsed by <see cref="SeekToTimestampCommand"/>.
    /// </summary>
    [ObservableProperty]
    private string _seekToTimestampInput = "";

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

    [ObservableProperty]
    private ClockChoiceItem? _selectedClockChoice;

    partial void OnSelectedClockChoiceChanged(ClockChoiceItem? value)
    {
        if (_suppressClockChoiceChanged) return;
        ApplyClockSelectionToRouter();
    }

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

    /// <summary>Convenience constructor for design-time / unit-test scenarios with no settings store.</summary>
    public PlayerViewModel(OutputViewModel outputs) : this(outputs, settings: null, settingsStore: null) { }

    public PlayerViewModel(OutputViewModel outputs, SettingsViewModel? settings, Services.AppSettingsService? settingsStore)
    {
        _outputs = outputs;
        _settings = settings;
        _settingsStore = settingsStore;

        _outputs.AudioEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.VideoEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.NdiEndpointModels.CollectionChanged += OnOutputPoolChanged;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _positionTimer.Tick += OnPositionTick;

        if (settings is not null)
        {
            // Hydrate transport-side defaults from settings.
            _autoAdvance = settings.AutoAdvance;
            _loop = settings.Loop;
            _volumePercent = settings.VolumePercent;
            // React to settings changes after the user adjusts them.
            settings.SettingsApplied += OnSettingsApplied;
        }

        // Build the initial file-mode player. Rebuilding for NDI happens lazily
        // in PlayNdiSourceAsync and is mirrored by a rebuild back to file mode
        // when the user starts a file again.
        BuildFilePlayer();

        RebuildOutputRows();
        AddEmptyPlaylistTab("Playlist 1");
        SelectedPlaylist = Playlists[0];

        SyncTransportFlags();
    }

    /// <summary>
    /// Returns the drift-correction options selected by settings (or a sensible
    /// default if no settings store is wired). Pulled out so the file-mode
    /// player and NDI-mode player builders share one source of truth.
    /// </summary>
    private AvDriftCorrectionOptions GetDriftOptions() =>
        _settings is not null
            ? _settings.ToAppSettings().AvDrift.ToOptions()
            : new AvDriftCorrectionOptions
            {
                InitialDelay = TimeSpan.FromSeconds(10),
                Interval = TimeSpan.FromSeconds(5),
                MinDriftMs = 8,
                IgnoreOutlierDriftMs = 250,
                OutlierConsecutiveSamples = 3,
                CorrectionGain = 0.15,
                MaxStepMs = 5,
                MaxAbsOffsetMs = 250
            };

    /// <summary>
    /// Constructs a <see cref="MediaPlayer"/> ready for FFmpeg-decoded file
    /// playback. The instance is owned by <see cref="_player"/> until the next
    /// rebuild. Endpoints are NOT re-attached here — that happens in
    /// <see cref="ReconcileEndpointsAsync"/> on the next play.
    /// </summary>
    private void BuildFilePlayer()
    {
        UnhookPlayerEvents();
        _attachedEndpoints.Clear();

        _player = new MediaPlayer();
        _player.ConfigureAutoAvDriftCorrection(GetDriftOptions());
        _player.IsLooping = Loop;
        _player.Volume = (float)Math.Clamp(VolumePercent / 100.0, 0, 2);
        _sourceMode = SourceMode.File;
        _activeNdiSourceLabel = null;
        HookPlayerEvents();
        OnPropertyChanged(nameof(StateLabel));
    }

    /// <summary>
    /// Builds an NDI-mode <see cref="MediaPlayer"/> via
    /// <see cref="MediaPlayer.Create"/> + <c>WithNDIInput(name, preset)</c>.
    /// The lifecycle hook installed by the extension owns the source's open /
    /// start / stop calls; this VM only needs to hand it a name.
    /// </summary>
    private void BuildNdiPlayer(string sourceName, NDIEndpointPreset preset)
    {
        UnhookPlayerEvents();
        _attachedEndpoints.Clear();

        _player = MediaPlayer.Create()
            .WithNDIInput(sourceName, preset)
            .Build();
        _player.ConfigureAutoAvDriftCorrection(GetDriftOptions());
        _player.IsLooping = Loop;
        _player.Volume = (float)Math.Clamp(VolumePercent / 100.0, 0, 2);
        _sourceMode = SourceMode.Ndi;
        _activeNdiSourceLabel = sourceName;
        HookPlayerEvents();
        OnPropertyChanged(nameof(StateLabel));
    }

    /// <summary>
    /// Stops the current player (if running), unhooks events, disposes it,
    /// and runs <paramref name="build"/> to construct the replacement. The
    /// caller is responsible for any post-build endpoint re-registration —
    /// usually delegated to <see cref="ReconcileEndpointsAsync"/>.
    /// </summary>
    private async Task SwitchPlayerAsync(Action build)
    {
        try
        {
            if (_player.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ready)
            {
                await _player.StopAsync(stopRegisteredEndpoints: false);
            }
        }
        catch (Exception ex)
        {
            SLog.LogDebug(ex, "SwitchPlayer: stop on previous player failed (continuing).");
        }

        try { await _player.DisposeAsync(); }
        catch (Exception ex) { SLog.LogDebug(ex, "SwitchPlayer: dispose on previous player failed (continuing)."); }

        build();
    }

    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        if (_settings is null) return;
        // Refresh drift options live so the next corrector cycle picks them up.
        _player.ConfigureAutoAvDriftCorrection(_settings.ToAppSettings().AvDrift.ToOptions());
        // Re-evaluate which outputs should be ticked given the (possibly changed)
        // default-output set. Only do this when no playback session is active so
        // routes do not flap mid-track.
        if (_player.State is PlaybackState.Idle or PlaybackState.Stopped)
            ApplyOutputSelectionForCurrentPlaylist(forceFromDefaults: false);
    }

    private void EnsureFfmpeg()
    {
        if (Interlocked.Exchange(ref _ffmpegInitialized, 1) != 0) return;
        ffmpeg.RootPath = FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";
    }

    private void OnOutputRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlayerOutputRowViewModel row) return;
        if (e.PropertyName == nameof(PlayerOutputRowViewModel.AveToPlayer) &&
            row is { Kind: PlayerOutputKind.Ndi })
        {
            _aveToPlayerMemory[row.RowKey] = row.AveToPlayer;
            TryApplyAveStreamSelection(row);
            return;
        }
        if (e.PropertyName is nameof(PlayerOutputRowViewModel.IsSelected)
            or nameof(PlayerOutputRowViewModel.IsRowAvailable))
        {
            // Persist the user's selection back to the active playlist's overrides
            // (when playlist overrides are in use) so reopening the app or switching
            // tabs reproduces the routing they just set up.
            PersistSelectionToActivePlaylistIfOverriding();
            RefreshClockChoices();
        }
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

        // Apply the active playlist's override set (or fall back to defaults from
        // SettingsViewModel) to the freshly built row list. This is the entry-point
        // for "auto-tick the outputs the user told us they care about" — both at
        // first launch and after the user adds another endpoint on the Outputs tab.
        ApplyOutputSelectionForCurrentPlaylist(forceFromDefaults: !_hasAppliedDefaultsThisLaunch);
        _hasAppliedDefaultsThisLaunch = true;

        UpdateRoutingLockUi();
        RefreshClockChoices();
    }

    /// <summary>
    /// Sets <c>IsSelected</c> on each output row to match either:
    /// <list type="bullet">
    ///   <item><description>the active playlist's <c>OutputOverrideKeys</c> when non-empty;</description></item>
    ///   <item><description>otherwise the global default-output set from settings;</description></item>
    ///   <item><description>otherwise the row's prior in-memory selection (no change).</description></item>
    /// </list>
    /// Suppresses persistence callbacks while toggling.
    /// </summary>
    private void ApplyOutputSelectionForCurrentPlaylist(bool forceFromDefaults)
    {
        if (OutputRows.Count == 0) return;

        HashSet<string>? selectionKeys = null;
        Dictionary<string, int>? ndiAveByKey = null;
        bool fromOverrides = false;

        var playlist = SelectedPlaylist;
        if (playlist?.OutputOverrideKeys is { Count: > 0 } overrides)
        {
            selectionKeys = new HashSet<string>(overrides, StringComparer.Ordinal);
            fromOverrides = true;
        }
        else if (forceFromDefaults && _settings is not null)
        {
            var s = _settings.ToAppSettings();
            if (s.DefaultOutputs.Count > 0)
            {
                selectionKeys = new HashSet<string>(s.DefaultOutputs, StringComparer.Ordinal);
                ndiAveByKey = s.NdiAveDefaults;
            }
        }

        if (selectionKeys is null) return; // honour existing in-memory selection.

        _suppressSelectionPersistence = true;
        try
        {
            foreach (var row in OutputRows)
            {
                bool wantSelected = selectionKeys.Contains(SettingsRowKey(row));
                if (row.IsSelected != wantSelected)
                    row.IsSelected = wantSelected;
                if (row.Kind == PlayerOutputKind.Ndi
                    && ndiAveByKey is not null
                    && ndiAveByKey.TryGetValue(SettingsRowKey(row), out var aveIdx)
                    && row.NdiAveToPlayerIndex != aveIdx)
                {
                    row.NdiAveToPlayerIndex = aveIdx;
                }
            }
        }
        finally
        {
            _suppressSelectionPersistence = false;
        }

        SLog.LogDebug("Applied output selection: source={Source}, count={Count}.",
            fromOverrides ? "playlist-override" : "default", selectionKeys.Count);
    }

    /// <summary>
    /// Maps a <see cref="PlayerOutputRowViewModel"/> to the canonical settings
    /// row key (<c>{Kind}:{Name}</c>). The row's own <c>RowKey</c> happens to use
    /// the same shape (<see cref="RowKey"/>), so this is straight pass-through —
    /// kept as a helper so the contract is explicit.
    /// </summary>
    private static string SettingsRowKey(PlayerOutputRowViewModel row) => row.RowKey;

    /// <summary>
    /// Saves the current row selection to the active playlist's override set
    /// (only when overrides are already enabled for that playlist, or when
    /// <see cref="SettingsViewModel.RememberPlaylistOverrides"/> says so).
    /// </summary>
    private void PersistSelectionToActivePlaylistIfOverriding()
    {
        if (_suppressSelectionPersistence) return;
        var playlist = SelectedPlaylist;
        if (playlist is null) return;
        // Only update an OVERRIDE that already exists; don't promote a default
        // selection to a per-playlist override unless the user explicitly enabled
        // overrides for this playlist via UseOutputOverridesCommand.
        if (!playlist.HasOutputOverrides) return;

        var keys = OutputRows.Where(r => r.IsSelected).Select(r => r.RowKey).ToList();
        playlist.SetOutputOverrides(keys);
    }

    private static string RowKey(PlayerOutputKind kind, string name) => $"{kind}:{name}";

    private void HookPlayerEvents()
    {
        // Track per-player so a rebuild leaves no dangling subscription on the
        // disposed instance (would NRE / leak on next state change before GC).
        if (_eventsHooked) return;
        _eventsHooked = true;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.PlaybackCompleted += OnPlaybackCompleted;
        _player.PlaybackFailed += OnPlaybackFailed;
    }

    private void UnhookPlayerEvents()
    {
        if (!_eventsHooked) return;
        _eventsHooked = false;
        try { _player.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
        try { _player.PlaybackCompleted -= OnPlaybackCompleted; } catch { }
        try { _player.PlaybackFailed -= OnPlaybackFailed; } catch { }
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
        {
            SeekNormalized = Math.Clamp(p.TotalSeconds / dur.TotalSeconds, 0, 1);
            var remaining = dur - p;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            RemainingText = "−" + FormatTime(remaining);
        }
        else
        {
            RemainingText = "−0:00";
        }
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.Days * 24 + t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// Tries to parse <paramref name="text"/> into a positional <see cref="TimeSpan"/>.
    /// Accepts <c>HH:MM:SS</c>, <c>MM:SS</c>, <c>SS</c>, fractional seconds
    /// (<c>1:23.5</c>), and bare numbers. Returns <see langword="false"/> on
    /// any malformed input — callers surface a status message instead of
    /// throwing into the UI thread.
    /// </summary>
    internal static bool TryParseTimestamp(string? text, out TimeSpan position)
    {
        position = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        // Bare seconds (with optional fractional part).
        if (!s.Contains(':'))
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var secs)
                && secs >= 0)
            {
                position = TimeSpan.FromSeconds(secs);
                return true;
            }
            return false;
        }

        // hh:mm:ss[.fff] or mm:ss[.fff]
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return false;

        int hours = 0, minutes;
        double seconds;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out hours)) return false;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out minutes)) return false;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out seconds)) return false;
        }
        else
        {
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out minutes)) return false;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out seconds)) return false;
        }

        if (hours < 0 || minutes < 0 || seconds < 0) return false;
        if (minutes >= 60 || seconds >= 60) return false;
        position = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>
    /// Command bound to the bottom-bar "Go" button next to the Seek-to input.
    /// Parses <see cref="SeekToTimestampInput"/> via <see cref="TryParseTimestamp"/>
    /// and asks the player to seek; clamps to <c>[0, Duration]</c>.
    /// </summary>
    [RelayCommand]
    private async Task SeekToTimestampAsync()
    {
        if (!TryParseTimestamp(SeekToTimestampInput, out var pos))
        {
            StatusMessage = "Invalid time format. Use HH:MM:SS, MM:SS, or seconds.";
            return;
        }

        var dur = _player.Duration;
        if (dur is { } d && pos > d) pos = d;

        try
        {
            await _player.SeekAsync(pos);
            StatusMessage = $"Seeked to {FormatTime(pos)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Seek failed: {ex.Message}";
        }
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
            RemainingText = "−0:00";
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

    /// <summary>
    /// Switches the player into NDI-source mode and starts the named source.
    /// Builds a fresh <see cref="MediaPlayer"/> via the
    /// <c>WithNDIInput(sourceName, preset)</c> lifecycle hook every time —
    /// the hook owns the source's open / start / stop. Subsequent calls keep
    /// rebuilding so a different source name is honoured. Switching back to a
    /// file is automatic on the next <see cref="OpenAndPlayPathAsync"/>.
    /// </summary>
    public async Task PlayNdiSourceAsync(string sourceName, NDIEndpointPreset preset)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            StatusMessage = "Enter a non-empty NDI source name.";
            return;
        }

        try
        {
            StatusMessage = $"NDI: connecting to '{sourceName}' ({preset})…";
            await SwitchPlayerAsync(() => BuildNdiPlayer(sourceName, preset));
            await ReconcileEndpointsAsync();

            if (_attachedEndpoints.Count == 0)
            {
                StatusMessage = "Select at least one started output (Audio / Video / NDI) in the list above.";
                return;
            }

            // Apply the selected router clock so the NDI clock can be picked up
            // when it's registered by the lifecycle hook on PlayAsync.
            RefreshClockChoices();
            ApplyClockSelectionToRouter();
            await _player.PlayAsync();
            // After the lifecycle hook has run, the NDI clock is in the registry —
            // re-evaluate so the dropdown lists it (sticks the user's choice).
            RefreshClockChoices();
            ApplyClockSelectionToRouter();
            ApplyAveToAllNdiRows();
            StatusMessage = $"NDI: playing '{sourceName}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"NDI: {ex.Message}";
            SLog.LogWarning(ex, "PlayNdiSourceAsync failed for '{Source}'", sourceName);
        }
    }

    private async Task OpenAndPlayPathAsync(string path)
    {
        EnsureFfmpeg();

        // If the previous session was an NDI source, the player instance still
        // carries the NDI lifecycle hook which would re-attach NDI inputs on
        // the next PlayAsync. Rebuild a clean file-mode player before opening.
        if (_sourceMode != SourceMode.File)
        {
            await SwitchPlayerAsync(BuildFilePlayer);
        }

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
            // Apply the selected router clock *before* OpenAsync so the transport never
            // briefly switches to auto/stopwatch (ResetAllDriftTrackers + push flush) when
            // RefreshClockChoices reconciles a new media session. That intermediate master
            // is visible in logs and can leave NDI video queued then burst to catch up.
            RefreshClockChoices();
            ApplyClockSelectionToRouter();
            await _player.OpenAsync(path, GetDecoderOptions());
            // Re-evaluate with decoder metadata (e.g. VideoPts clock) after the file is open.
            RefreshClockChoices();
            ApplyClockSelectionToRouter();
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
        RefreshClockChoices();
    }

    private FFmpegDecoderOptions GetDecoderOptions()
    {
        bool wantsAudio = WantsAudioDecoding();
        bool forceRgba = false;

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
                forceRgba = true;
                break;
            }
        }

        return new FFmpegDecoderOptions
        {
            EnableAudio = wantsAudio,
            EnableVideo = DecoderOptionsForUiPlayback.EnableVideo,
            VideoTargetPixelFormat = forceRgba ? PixelFormat.Rgba32 : DecoderOptionsForUiPlayback.VideoTargetPixelFormat,
            VideoBufferDepth = DecoderOptionsForUiPlayback.VideoBufferDepth,
            PreferHardwareDecoding = DecoderOptionsForUiPlayback.PreferHardwareDecoding,
            DecoderThreadCount = DecoderOptionsForUiPlayback.DecoderThreadCount
        };
    }

    private bool WantsAudioDecoding()
    {
        foreach (var row in OutputRows)
        {
            if (!row.IsSelected || !row.IsRowAvailable) continue;
            if (row.Kind == PlayerOutputKind.Audio)
                return true;
            if (row.Kind == PlayerOutputKind.Ndi &&
                row.AveToPlayer != AveStreamSelection.VideoOnly)
                return true;
        }
        return false;
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

    private void ApplyClockSelectionToRouter()
    {
        try
        {
            var key = SelectedClockChoice?.Key ?? "auto";
            if (key == "auto")
            {
                TryResolveAutoClock(out var autoClock);
                _player.Router.SetClock(autoClock);
                SLog.LogInformation(
                    "A/V: router clock = Auto → {Type} (MediaPlayer will sync pull video to Router.Clock).",
                    autoClock.GetType().Name);
                return;
            }
            if (key == "internal")
            {
                _player.Router.SetClock(_player.Router.InternalClock);
                SLog.LogInformation("A/V: router clock = Internal stopwatch (pull video will follow same).");
                return;
            }

            if (_clockChoiceByKey.TryGetValue(key, out var chosenClock))
            {
                // VideoPts-based clocks should only be forced when video is actually present.
                if (chosenClock is VideoPtsClock && _player.VideoChannel is null)
                {
                    _player.Router.SetClock(null);
                    SLog.LogInformation("A/V: VideoPts clock cleared — no video track; reverting to Auto clock choice.");
                    SelectedClockChoice = ClockChoices.FirstOrDefault(c => c.Key == "auto");
                    return;
                }

                _player.Router.SetClock(chosenClock);
                SLog.LogInformation(
                    "A/V: router clock = manual {Type} (pull video presentation should match; check 'A/V:' logs).",
                    chosenClock.GetType().Name);
                return;
            }

            _player.Router.SetClock(null);
            SLog.LogInformation("A/V: router clock override cleared; registry/defaults apply.");
            SelectedClockChoice = ClockChoices.FirstOrDefault(c => c.Key == "auto");
        }
        catch (Exception ex)
        {
            SLog.LogWarning(ex, "Failed to apply selected clock '{ClockKey}'; keeping current router clock.", SelectedClockChoice?.Key);
        }
    }

    private void TryRecoverMissingManualClockChoice(string previousKey)
    {
        if (ClockChoices.Any(c => c.Key == previousKey)) return;
        if (string.IsNullOrEmpty(previousKey) || previousKey is "auto" or "internal") return;

        if (!previousKey.StartsWith("ndi:", StringComparison.Ordinal)) return;

        string rowKey = previousKey["ndi:".Length..];
        foreach (var row in OutputRows)
        {
            if (row.RowKey != rowKey || row.Kind != PlayerOutputKind.Ndi) continue;
            if (!row.TryGetNdiAveEndpointUnconditionally(out var ndiAv) || ndiAv is null) continue;

            ClockChoices.Add(new ClockChoiceItem(previousKey, $"{row.Name} (NDIClock)"));
            _clockChoiceByKey[previousKey] = ndiAv.Clock;
            SLog.LogDebug("RefreshClockChoices: restored missing clock choice {Key} (NDI row briefly unavailable for dropdown).", previousKey);
            return;
        }
    }

    private void RefreshClockChoices()
    {
        string previousKey = SelectedClockChoice?.Key ?? "auto";
        ClockChoices.Clear();
        _clockChoiceByKey.Clear();

        ClockChoices.Add(new ClockChoiceItem("auto", "Auto (prefer audio hardware)"));
        ClockChoices.Add(new ClockChoiceItem("internal", "Internal stopwatch"));

        foreach (var row in OutputRows)
        {
            if (!row.IsSelected) continue;

            if (row.TryGetSelectedEndpoint(out var ep))
            {
                if (ep is IClockCapableEndpoint clocked)
                {
                    try
                    {
                        string key = $"endpoint:{row.RowKey}";
                        string label = $"{row.Name} ({clocked.Clock.GetType().Name})";
                        ClockChoices.Add(new ClockChoiceItem(key, label));
                        _clockChoiceByKey[key] = clocked.Clock;
                    }
                    catch (Exception ex)
                    {
                        SLog.LogDebug(ex, "RefreshClockChoices: endpoint clock unavailable for '{Name}'.", row.Name);
                    }
                }
            }

            // NDI: register the sender NDIClock whenever an AveEndpoint exists, even
            // if IsRowAvailable is false (e.g. model Open flips). Gating the ndi:* entry
            // on TryGetSelectedEndpoint only caused the dropdown to lose ndi: during
            // Reconcile/Refresh, fall back to Auto, and SetClock(Stopwatch) — gating
            // video to wall time while the wire still uses stream PTS.
            if (row is { Kind: PlayerOutputKind.Ndi }
                && row.TryGetNdiAveEndpointUnconditionally(out var ndiAve) && ndiAve is not null)
            {
                string key = $"ndi:{row.RowKey}";
                if (!_clockChoiceByKey.ContainsKey(key))
                {
                    string label = $"{row.Name} (NDIClock)";
                    ClockChoices.Add(new ClockChoiceItem(key, label));
                    _clockChoiceByKey[key] = ndiAve.Clock;
                }
            }
        }

        // If the NDI row was momentarily !IsRowAvailable, the ndi:* entry is missing above
        // and we would fall back to "auto" → SetClock(Stopwatch), which gates video against
        // wall time while NDIClock still advances from stream PTS — ~1s burst/hang on NDI out.
        TryRecoverMissingManualClockChoice(previousKey);

        var selected = ClockChoices.FirstOrDefault(c => c.Key == previousKey)
            ?? ClockChoices.FirstOrDefault(c => c.Key == "auto");
        if (selected?.Key == "auto"
            && !string.IsNullOrEmpty(previousKey)
            && !string.Equals(previousKey, "auto", StringComparison.Ordinal)
            && !string.Equals(previousKey, "internal", StringComparison.Ordinal))
        {
            SLog.LogWarning(
                "RefreshClockChoices: could not re-resolve clock key '{PreviousKey}'; list has ndi/endpoint = [{Keys}]. " +
                "Falling back to Auto — A/V and NDI may desync until you reselect the clock.",
                previousKey,
                string.Join(", ", ClockChoices.Select(c => c.Key)));
        }

        string newKey = selected?.Key ?? "auto";
        _suppressClockChoiceChanged = true;
        try
        {
            SelectedClockChoice = selected;
        }
        finally
        {
            _suppressClockChoiceChanged = false;
        }

        if (!string.Equals(previousKey, newKey, StringComparison.Ordinal))
            ApplyClockSelectionToRouter();
    }

    private bool TryResolveAutoClock(out IMediaClock clock)
    {
        // For local playback, audio-device clock should normally lead to avoid
        // "audio behind video" drift from a video-clock master.
        foreach (var row in OutputRows)
        {
            if (row.Kind != PlayerOutputKind.Audio) continue;
            if (!row.TryGetSelectedEndpoint(out var ep)) continue;
            if (ep is not IClockCapableEndpoint clocked) continue;
            try
            {
                clock = clocked.Clock;
                return true;
            }
            catch
            {
                // Ignore unavailable endpoint clock and keep searching.
            }
        }

        clock = _player.Router.InternalClock;
        return true;
    }

    /// <summary>
    /// Opens the "Play NDI source" dialog, then if the user confirms switches
    /// the player into NDI mode and starts the chosen source. Hooked from the
    /// Player view's NDI menu / button.
    /// </summary>
    [RelayCommand]
    private async Task OpenNdiSourceDialogAsync(Window window)
    {
        var vm = new PlayNDISourceViewModel();
        var dialog = new PlayNDISourceDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = vm
        };
        var ok = await dialog.ShowDialog<bool>(window);
        if (!ok || vm.Result is null) return;
        await PlayNdiSourceAsync(vm.Result.SourceName, vm.Result.Preset);
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

    // ── Playlist entry management (Doc/Clock-And-AV-Drift-Analysis.md UI refactor) ──

    [RelayCommand]
    private void RemovePlaylistEntry(PlaylistEntry? entry)
    {
        var pl = SelectedPlaylist;
        if (pl is null || entry is null) return;
        pl.RemoveEntry(entry);
    }

    [RelayCommand]
    private void RemoveSelectedPlaylistEntry()
    {
        var pl = SelectedPlaylist;
        pl?.RemoveSelected();
    }

    [RelayCommand]
    private void MovePlaylistEntryUp(PlaylistEntry? entry)
    {
        SelectedPlaylist?.MoveEntryUp(entry);
    }

    [RelayCommand]
    private void MovePlaylistEntryDown(PlaylistEntry? entry)
    {
        SelectedPlaylist?.MoveEntryDown(entry);
    }

    [RelayCommand]
    private void ClearCurrentPlaylist()
    {
        SelectedPlaylist?.ClearEntries();
    }

    [RelayCommand]
    private void RenameCurrentPlaylist(string? newTitle)
    {
        if (SelectedPlaylist is null) return;
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        SelectedPlaylist.Title = newTitle.Trim();
    }

    // ── Per-playlist output overrides ────────────────────────────────────

    /// <summary>
    /// Snapshots the currently checked rows into the active playlist's override
    /// set, so that whenever this playlist is reselected the same routing is
    /// reapplied (instead of the global default-outputs set).
    /// </summary>
    [RelayCommand]
    private void UseOutputOverridesForPlaylist()
    {
        var pl = SelectedPlaylist;
        if (pl is null) return;
        var keys = OutputRows.Where(r => r.IsSelected).Select(r => r.RowKey).ToList();
        if (keys.Count == 0)
        {
            StatusMessage = "Select at least one output before saving as a playlist override.";
            return;
        }
        pl.SetOutputOverrides(keys);
        StatusMessage = $"Saved {keys.Count} output override(s) for '{pl.Title}'.";
    }

    /// <summary>
    /// Clears the active playlist's override set. Subsequent rebuilds fall back
    /// to the global default-outputs set from settings.
    /// </summary>
    [RelayCommand]
    private void ClearOutputOverridesForPlaylist()
    {
        var pl = SelectedPlaylist;
        if (pl is null) return;
        pl.SetOutputOverrides(null);
        ApplyOutputSelectionForCurrentPlaylist(forceFromDefaults: true);
        RefreshClockChoices();
        StatusMessage = $"Cleared output override for '{pl.Title}'.";
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        if (_settings is not null)
            _settings.SettingsApplied -= OnSettingsApplied;
        _outputs.AudioEndpointModels.CollectionChanged -= OnOutputPoolChanged;
        _outputs.VideoEndpointModels.CollectionChanged -= OnOutputPoolChanged;
        _outputs.NdiEndpointModels.CollectionChanged -= OnOutputPoolChanged;

        foreach (var row in OutputRows)
        {
            row.PropertyChanged -= OnOutputRowPropertyChanged;
            row.Dispose();
        }
        OutputRows.Clear();

        UnhookPlayerEvents();
        _player.Dispose();
    }
}
