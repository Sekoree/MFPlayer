using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SPlayer.Core.ViewModels;

public sealed class PlaylistEntry : ObservableObject
{
    public string FilePath { get; }
    public string? Title { get; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Title) ? Title! : Path.GetFileName(FilePath);

    public PlaylistEntry(string filePath, string? title = null)
    {
        FilePath = filePath;
        Title = title;
    }
}

public partial class PlaylistDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Playlist";

    [ObservableProperty]
    private ObservableCollection<PlaylistEntry> _entries = new();

    [ObservableProperty]
    private PlaylistEntry? _selectedEntry;

    [ObservableProperty]
    private int _currentIndex;

    public int EntryCount => Entries.Count;

    public void SetTitle(string title) => Title = title;

    public int IndexOf(PlaylistEntry? e) => e is null ? -1 : Entries.IndexOf(e);

    public PlaylistEntry? EntryAt(int index) =>
        index >= 0 && index < Entries.Count ? Entries[index] : null;

    public int MoveNext() =>
        CurrentIndex < Entries.Count - 1 ? ++CurrentIndex : CurrentIndex;

    public int MovePrevious() =>
        CurrentIndex > 0 ? --CurrentIndex : CurrentIndex;
}
