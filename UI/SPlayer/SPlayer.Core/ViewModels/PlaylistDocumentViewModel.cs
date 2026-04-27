using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SPlayer.Core.ViewModels;

public sealed partial class PlaylistEntry : ObservableObject
{
    public string FilePath { get; }
    public string? Title { get; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Title) ? Title! : Path.GetFileName(FilePath);

    /// <summary>True when this entry is the currently-playing item in its playlist.</summary>
    [ObservableProperty]
    private bool _isPlaying;

    public PlaylistEntry(string filePath, string? title = null)
    {
        FilePath = filePath;
        Title = title;
    }
}

/// <summary>
/// One playlist tab. Owns its entries, selection, and an optional set of
/// per-playlist output overrides. When <see cref="HasOutputOverrides"/> is
/// <see langword="true"/>, only the rows whose RowKey is contained in
/// <see cref="OutputOverrideKeys"/> are routed for that playlist (instead of
/// the global default-outputs set).
/// </summary>
public sealed partial class PlaylistDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Playlist";

    [ObservableProperty]
    private ObservableCollection<PlaylistEntry> _entries = new();

    [ObservableProperty]
    private PlaylistEntry? _selectedEntry;

    [ObservableProperty]
    private int _currentIndex = -1;

    /// <summary>
    /// When non-empty, replaces the global output selection while this playlist
    /// is the active one. Identifiers match <c>PlayerOutputRowViewModel.RowKey</c>.
    /// </summary>
    public ObservableCollection<string> OutputOverrideKeys { get; } = new();

    /// <summary>True if any output overrides have been configured for this tab.</summary>
    [ObservableProperty]
    private bool _hasOutputOverrides;

    public int EntryCount => Entries.Count;

    public PlaylistDocumentViewModel()
    {
        OutputOverrideKeys.CollectionChanged += (_, _) => HasOutputOverrides = OutputOverrideKeys.Count > 0;
        Entries.CollectionChanged += (_, _) => UpdatePlayingFlags();
    }

    /// <summary>
    /// Mirrors <see cref="CurrentIndex"/> onto each entry's <see cref="PlaylistEntry.IsPlaying"/>
    /// flag so the UI can render a "now playing" marker without per-row converters.
    /// </summary>
    private void UpdatePlayingFlags()
    {
        for (int i = 0; i < Entries.Count; i++)
            Entries[i].IsPlaying = i == CurrentIndex;
    }

    partial void OnCurrentIndexChanged(int value) => UpdatePlayingFlags();

    public void SetTitle(string title) => Title = title;

    public int IndexOf(PlaylistEntry? e) => e is null ? -1 : Entries.IndexOf(e);

    public PlaylistEntry? EntryAt(int index) =>
        index >= 0 && index < Entries.Count ? Entries[index] : null;

    public int MoveNext() =>
        CurrentIndex < Entries.Count - 1 ? ++CurrentIndex : CurrentIndex;

    public int MovePrevious() =>
        CurrentIndex > 0 ? --CurrentIndex : CurrentIndex;

    /// <summary>
    /// Replaces the override set with <paramref name="keys"/>. An empty / null
    /// argument clears overrides — the playlist will then use the global
    /// default outputs.
    /// </summary>
    public void SetOutputOverrides(IEnumerable<string>? keys)
    {
        OutputOverrideKeys.Clear();
        if (keys is null) return;
        foreach (var k in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal))
            OutputOverrideKeys.Add(k);
    }

    // ── Entry management ──────────────────────────────────────────────────

    [RelayCommand]
    public void RemoveEntry(PlaylistEntry? entry)
    {
        if (entry is null) return;
        int idx = Entries.IndexOf(entry);
        if (idx < 0) return;
        bool removingCurrent = idx == CurrentIndex;
        Entries.RemoveAt(idx);
        if (removingCurrent)
        {
            CurrentIndex = -1;
            SelectedEntry = null;
        }
        else if (idx < CurrentIndex)
        {
            CurrentIndex--;
        }
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (SelectedEntry is { } e) RemoveEntry(e);
    }

    [RelayCommand]
    public void MoveEntryUp(PlaylistEntry? entry)
    {
        if (entry is null) return;
        int idx = Entries.IndexOf(entry);
        if (idx <= 0) return;
        Entries.Move(idx, idx - 1);
        if (CurrentIndex == idx) CurrentIndex--;
        else if (CurrentIndex == idx - 1) CurrentIndex++;
    }

    [RelayCommand]
    public void MoveEntryDown(PlaylistEntry? entry)
    {
        if (entry is null) return;
        int idx = Entries.IndexOf(entry);
        if (idx < 0 || idx >= Entries.Count - 1) return;
        Entries.Move(idx, idx + 1);
        if (CurrentIndex == idx) CurrentIndex++;
        else if (CurrentIndex == idx + 1) CurrentIndex--;
    }

    [RelayCommand]
    public void ClearEntries()
    {
        Entries.Clear();
        CurrentIndex = -1;
        SelectedEntry = null;
    }
}
