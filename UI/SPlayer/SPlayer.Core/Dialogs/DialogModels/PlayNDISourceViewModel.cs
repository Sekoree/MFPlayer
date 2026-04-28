using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NDILib;
using S.Media.NDI;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

/// <summary>
/// UI row for one entry in <see cref="PlayNDISourceViewModel.DiscoveredSources"/>.
/// Wraps the immutable <see cref="NDIDiscoveredSource"/> with a stable
/// reference identity so Avalonia's selection sticks across discovery
/// snapshots that re-emit the same name.
/// </summary>
public sealed partial class NdiSourceRow : ObservableObject
{
    public string Name { get; }
    public string? Url { get; }

    public NdiSourceRow(string name, string? url)
    {
        Name = name;
        Url = url;
    }
}

/// <summary>
/// Drives the "Play NDI source" dialog. Subscribes to <see cref="NDIDiscovery"/>
/// while the dialog is open, mirrors <see cref="NDIDiscovery.CurrentSources"/>
/// into <see cref="DiscoveredSources"/>, and lets the user pick a discovered
/// source or type a name manually.
/// </summary>
public partial class PlayNDISourceViewModel : ViewModelBase, IDisposable
{
    /// <summary>List of currently-known NDI sources (live).</summary>
    public ObservableCollection<NdiSourceRow> DiscoveredSources { get; } = new();

    public ObservableCollection<NDIEndpointPreset> Presets { get; } = new([
        NDIEndpointPreset.Balanced,
        NDIEndpointPreset.LowLatency,
        NDIEndpointPreset.UltraLowLatency,
        NDIEndpointPreset.Safe
    ]);

    [ObservableProperty]
    private NdiSourceRow? _selectedSource;

    [ObservableProperty]
    private string _manualName = "";

    [ObservableProperty]
    private NDIEndpointPreset _selectedPreset = NDIEndpointPreset.Balanced;

    [ObservableProperty]
    private string _statusMessage = "Searching for NDI sources on the network…";

    /// <summary>
    /// Final result chosen by the user. <see langword="null"/> when the
    /// dialog was cancelled. Populated immediately before the dialog closes.
    /// </summary>
    public PlayNdiSourceResult? Result { get; private set; }

    private bool _subscribed;
    private bool _disposed;

    public PlayNDISourceViewModel()
    {
        // Lazy: wire discovery only when actually shown to avoid spinning the
        // mDNS thread for users who never use NDI input.
    }

    /// <summary>Called by the dialog's OnLoaded handler.</summary>
    public void StartDiscovery()
    {
        if (_subscribed) return;
        _subscribed = true;
        NDIDiscovery.Discovered += OnDiscovered;
        NDIDiscovery.Lost += OnLost;
        NDIDiscovery.AddRef();

        // Seed initial snapshot without waiting for the next watch tick.
        var seed = NDIDiscovery.CurrentSources.ToList();
        Dispatcher.UIThread.Post(() => Reseed(seed));
    }

    /// <summary>Called by the dialog's OnUnloaded handler.</summary>
    public void StopDiscovery()
    {
        if (!_subscribed) return;
        _subscribed = false;
        try { NDIDiscovery.Discovered -= OnDiscovered; } catch { }
        try { NDIDiscovery.Lost -= OnLost; } catch { }
        try { NDIDiscovery.Release(); } catch { }
    }

    private void OnDiscovered(object? sender, NDIDiscoveredSourceEventArgs e)
    {
        var name = e.Source.Name;
        var url = e.Source.UrlAddress;
        Dispatcher.UIThread.Post(() =>
        {
            if (!DiscoveredSources.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
                DiscoveredSources.Add(new NdiSourceRow(name, url));
            UpdateStatus();
        });
    }

    private void OnLost(object? sender, NDIDiscoveredSourceEventArgs e)
    {
        var name = e.Source.Name;
        Dispatcher.UIThread.Post(() =>
        {
            for (int i = DiscoveredSources.Count - 1; i >= 0; i--)
            {
                if (string.Equals(DiscoveredSources[i].Name, name, StringComparison.Ordinal))
                {
                    DiscoveredSources.RemoveAt(i);
                    break;
                }
            }
            UpdateStatus();
        });
    }

    private void Reseed(List<NDIDiscoveredSource> snapshot)
    {
        DiscoveredSources.Clear();
        foreach (var s in snapshot)
            DiscoveredSources.Add(new NdiSourceRow(s.Name, s.UrlAddress));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusMessage = DiscoveredSources.Count == 0
            ? "No NDI sources visible. Type a name below or wait for discovery to find one."
            : $"{DiscoveredSources.Count} source(s) on the network. Pick one or type a name.";
    }

    [RelayCommand]
    private void Add(Window dialog)
    {
        // Prefer the manual name when non-empty (so the user can type a
        // partial / hostname-only pattern even when the source is also in the
        // discovered list); otherwise fall back to the selection.
        var name = !string.IsNullOrWhiteSpace(ManualName)
            ? ManualName.Trim()
            : SelectedSource?.Name ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Pick a source from the list or enter a name.";
            return;
        }

        Result = new PlayNdiSourceResult(name, SelectedPreset);
        dialog.Close(true);
    }

    [RelayCommand]
    private void Cancel(Window dialog) => dialog.Close(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDiscovery();
    }
}

public sealed record PlayNdiSourceResult(string SourceName, NDIEndpointPreset Preset);
