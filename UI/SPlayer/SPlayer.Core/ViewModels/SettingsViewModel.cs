using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPlayer.Core.Models;
using SPlayer.Core.Services;

namespace SPlayer.Core.ViewModels;

/// <summary>
/// One row in the Settings &gt; Default outputs picker. Mirrors a configured
/// endpoint and exposes a checkbox for "auto-select on startup".
/// </summary>
public sealed partial class DefaultOutputItemViewModel : ObservableObject
{
    public string RowKey { get; }
    public string Name { get; }
    public string Kind { get; }
    public bool IsNdi { get; }

    [ObservableProperty]
    private bool _isDefault;

    /// <summary>NDI only: 0 = Both, 1 = AudioOnly, 2 = VideoOnly.</summary>
    [ObservableProperty]
    private int _ndiAveIndex;

    public DefaultOutputItemViewModel(string rowKey, string name, string kind, bool isNdi, bool isDefault, int ndiAveIndex)
    {
        RowKey = rowKey;
        Name = name;
        Kind = kind;
        IsNdi = isNdi;
        _isDefault = isDefault;
        _ndiAveIndex = ndiAveIndex;
    }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _store;
    private readonly OutputViewModel _outputs;
    private bool _hydrating;

    public ObservableCollection<DefaultOutputItemViewModel> AvailableOutputs { get; } = new();

    [ObservableProperty]
    private bool _autoAdvance = true;

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    private double _volumePercent = 100;

    [ObservableProperty]
    private bool _rememberPlaylistOverrides = true;

    /// <summary>
    /// §heavy-media-fixes phase 2 — when enabled, every Avalonia render path
    /// requested via the player paces its tick to the source frame interval
    /// instead of vsync.
    /// </summary>
    [ObservableProperty]
    private bool _limitRenderFpsToSource;

    // ── A/V drift correction ────────────────────────────────────────────────

    [ObservableProperty] private double _avInitialDelaySec = 10;
    [ObservableProperty] private double _avIntervalSec = 5;
    [ObservableProperty] private double _avMinDriftMs = 8;
    [ObservableProperty] private double _avIgnoreOutlierDriftMs = 250;
    [ObservableProperty] private int _avOutlierConsecutiveSamples = 3;
    [ObservableProperty] private double _avCorrectionGain = 0.15;
    [ObservableProperty] private double _avMaxStepMs = 20;
    [ObservableProperty] private double _avMaxAbsOffsetMs = 2000;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _settingsPath = "";

    /// <summary>Raised whenever user-visible settings change so the player can re-pick defaults.</summary>
    public event EventHandler? SettingsApplied;

    public SettingsViewModel(AppSettingsService store, OutputViewModel outputs)
    {
        _store = store;
        _outputs = outputs;
        SettingsPath = store.Path;

        _outputs.AudioEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.VideoEndpointModels.CollectionChanged += OnOutputPoolChanged;
        _outputs.NdiEndpointModels.CollectionChanged += OnOutputPoolChanged;

        Hydrate(_store.Load());
    }

    private void Hydrate(AppSettings s)
    {
        _hydrating = true;
        try
        {
            AutoAdvance = s.AutoAdvance;
            Loop = s.Loop;
            VolumePercent = s.VolumePercent;
            RememberPlaylistOverrides = s.RememberPlaylistOverrides;
            LimitRenderFpsToSource = s.LimitRenderFpsToSource;

            AvInitialDelaySec = s.AvDrift.InitialDelaySec;
            AvIntervalSec = s.AvDrift.IntervalSec;
            AvMinDriftMs = s.AvDrift.MinDriftMs;
            AvIgnoreOutlierDriftMs = s.AvDrift.IgnoreOutlierDriftMs;
            AvOutlierConsecutiveSamples = s.AvDrift.OutlierConsecutiveSamples;
            AvCorrectionGain = s.AvDrift.CorrectionGain;
            AvMaxStepMs = s.AvDrift.MaxStepMs;
            AvMaxAbsOffsetMs = s.AvDrift.MaxAbsOffsetMs;

            RebuildAvailableOutputs(s);
        }
        finally
        {
            _hydrating = false;
        }
    }

    private void OnOutputPoolChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Pull current persisted state to keep "default" checkmarks consistent
        // when endpoints are added/removed live.
        var current = ToAppSettings();
        RebuildAvailableOutputs(current);
    }

    private void RebuildAvailableOutputs(AppSettings s)
    {
        // Detach old item handlers.
        foreach (var item in AvailableOutputs)
            item.PropertyChanged -= OnItemChanged;
        AvailableOutputs.Clear();

        var defaults = new HashSet<string>(s.DefaultOutputs, StringComparer.Ordinal);

        foreach (var a in _outputs.AudioEndpointModels)
        {
            var key = $"Audio:{a.Name}";
            AvailableOutputs.Add(new DefaultOutputItemViewModel(
                key, a.Name, "Audio", isNdi: false,
                isDefault: defaults.Contains(key),
                ndiAveIndex: 0));
        }

        foreach (var v in _outputs.VideoEndpointModels)
        {
            var key = $"Video:{v.Name}";
            AvailableOutputs.Add(new DefaultOutputItemViewModel(
                key, v.Name, "Video", isNdi: false,
                isDefault: defaults.Contains(key),
                ndiAveIndex: 0));
        }

        foreach (var n in _outputs.NdiEndpointModels)
        {
            var key = $"Ndi:{n.Name}";
            int aveIdx = s.NdiAveDefaults.TryGetValue(key, out var idx) ? idx : 0;
            AvailableOutputs.Add(new DefaultOutputItemViewModel(
                key, n.Name, "NDI", isNdi: true,
                isDefault: defaults.Contains(key),
                ndiAveIndex: aveIdx));
        }

        foreach (var item in AvailableOutputs)
            item.PropertyChanged += OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hydrating) return;
        // Auto-save on any individual item flip; users expect immediate persistence.
        if (e.PropertyName is nameof(DefaultOutputItemViewModel.IsDefault)
                                  or nameof(DefaultOutputItemViewModel.NdiAveIndex))
            ApplyAndSave();
    }

    partial void OnAutoAdvanceChanged(bool value) => ApplyAndSaveIfReady();
    partial void OnLoopChanged(bool value) => ApplyAndSaveIfReady();
    partial void OnVolumePercentChanged(double value) => ApplyAndSaveIfReady();
    partial void OnRememberPlaylistOverridesChanged(bool value) => ApplyAndSaveIfReady();
    partial void OnLimitRenderFpsToSourceChanged(bool value) => ApplyAndSaveIfReady();

    partial void OnAvInitialDelaySecChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvIntervalSecChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvMinDriftMsChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvIgnoreOutlierDriftMsChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvOutlierConsecutiveSamplesChanged(int value) => ApplyAndSaveIfReady();
    partial void OnAvCorrectionGainChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvMaxStepMsChanged(double value) => ApplyAndSaveIfReady();
    partial void OnAvMaxAbsOffsetMsChanged(double value) => ApplyAndSaveIfReady();

    private void ApplyAndSaveIfReady()
    {
        if (_hydrating) return;
        ApplyAndSave();
    }

    private void ApplyAndSave()
    {
        var s = ToAppSettings();
        _store.Save(s);
        StatusMessage = $"Saved {DateTime.Now:HH:mm:ss}";
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    public AppSettings ToAppSettings()
    {
        var defaults = AvailableOutputs.Where(i => i.IsDefault).Select(i => i.RowKey).ToList();
        var ndiAve = AvailableOutputs
            .Where(i => i.IsNdi && i.NdiAveIndex != 0)
            .ToDictionary(i => i.RowKey, i => i.NdiAveIndex, StringComparer.Ordinal);

        return new AppSettings
        {
            DefaultOutputs = defaults,
            NdiAveDefaults = ndiAve,
            AutoAdvance = AutoAdvance,
            Loop = Loop,
            VolumePercent = VolumePercent,
            RememberPlaylistOverrides = RememberPlaylistOverrides,
            LimitRenderFpsToSource = LimitRenderFpsToSource,
            AvDrift = new AvDriftSettings
            {
                InitialDelaySec = AvInitialDelaySec,
                IntervalSec = AvIntervalSec,
                MinDriftMs = AvMinDriftMs,
                IgnoreOutlierDriftMs = AvIgnoreOutlierDriftMs,
                OutlierConsecutiveSamples = AvOutlierConsecutiveSamples,
                CorrectionGain = AvCorrectionGain,
                MaxStepMs = AvMaxStepMs,
                MaxAbsOffsetMs = AvMaxAbsOffsetMs
            }
        };
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Hydrate(new AppSettings());
        ApplyAndSave();
    }

    [RelayCommand]
    private void OpenSettingsLocation()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_store.Path);
            if (string.IsNullOrEmpty(dir)) return;
            System.IO.Directory.CreateDirectory(dir);
            // Best-effort cross-platform "open folder" without taking a new dependency.
            string? cmd = null;
            string args = $"\"{dir}\"";
            if (OperatingSystem.IsWindows()) cmd = "explorer.exe";
            else if (OperatingSystem.IsMacOS()) cmd = "open";
            else if (OperatingSystem.IsLinux()) cmd = "xdg-open";
            if (cmd is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, args) { UseShellExecute = true });
            StatusMessage = $"Opened {dir}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }
}
