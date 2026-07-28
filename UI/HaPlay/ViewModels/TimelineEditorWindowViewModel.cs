using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>One snap-grid choice for the timeline editor toolbar.</summary>
public sealed record TimelineGridOption(int Ms, string Label);

/// <summary>
/// Window-scoped state for one Timeline group's editor (the <see cref="ScriptEditorWindowViewModel"/>
/// one-window-per-subject pattern): pins the group node, forwards the cue player's edit-mode gate,
/// and feeds the live playhead from the 200 ms <c>OnCueProgress</c> samples already flowing into
/// <see cref="CuePlayerViewModel.ActiveCues"/>, smoothed by a ~60 ms UI interpolation timer.
/// </summary>
public sealed partial class TimelineEditorWindowViewModel : ObservableObject, IDisposable
{
    private readonly CuePlayerViewModel _player;
    private readonly DispatcherTimer? _playheadTimer;
    private double _lastRawPlayheadMs = -1;
    private DateTime _lastRawSampleUtc;

    public TimelineEditorWindowViewModel(CuePlayerViewModel player, CueNodeViewModel group, bool startPlayheadTimer = true)
    {
        _player = player;
        Group = group;
        SelectedGridOption = GridOptions[0];
        _player.PropertyChanged += OnPlayerPropertyChanged;
        group.PropertyChanged += OnGroupPropertyChanged;
        if (startPlayheadTimer)
        {
            _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _playheadTimer.Tick += (_, _) => UpdatePlayhead();
            _playheadTimer.Start();
        }
    }

    public CueNodeViewModel Group { get; }

    /// <summary>One lane per direct child, tree order - the collection is the group's own, so tree
    /// edits (add/remove/reorder) reflect live.</summary>
    public ObservableCollection<CueNodeViewModel> Lanes => Group.Children;

    public string WindowTitle => Strings.Format(
        nameof(Strings.TimelineWindowTitleFormat),
        string.IsNullOrWhiteSpace(Group.Number) ? Group.Label : $"{Group.Number} {Group.Label}".Trim());

    /// <summary>Editing gate - mirrors the cue player's edit mode; off = live view (playhead only).</summary>
    public bool IsCueEditMode => _player.IsCueEditMode;

    public bool IsLiveView => !IsCueEditMode;

    public bool HasLanes => Lanes.Count > 0;

    [ObservableProperty]
    private bool _snapEnabled = true;

    /// <summary>Toolbar toggle: overlay the volume-envelope polylines on media lanes (default on).</summary>
    [ObservableProperty]
    private bool _showEnvelopes = true;

    public IReadOnlyList<TimelineGridOption> GridOptions { get; } =
    [
        new(1000, Strings.TimelineGrid1s),
        new(500, Strings.TimelineGrid500ms),
        new(100, Strings.TimelineGrid100ms),
    ];

    [ObservableProperty]
    private TimelineGridOption _selectedGridOption;

    /// <summary>Live playhead on the group's plan epoch (ms); negative while the group is idle.</summary>
    [ObservableProperty]
    private double _playheadMs = -1;

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CuePlayerViewModel.IsCueEditMode))
        {
            OnPropertyChanged(nameof(IsCueEditMode));
            OnPropertyChanged(nameof(IsLiveView));
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.Number) or nameof(CueNodeViewModel.Label))
            OnPropertyChanged(nameof(WindowTitle));
        if (e.PropertyName is nameof(CueNodeViewModel.HasChildren))
            OnPropertyChanged(nameof(HasLanes));
    }

    /// <summary>Projects the furthest-along active direct child onto the group epoch
    /// (start + pre-wait + clip position - the aggregate row's projection) and extrapolates by wall
    /// clock between the 200 ms samples so the playhead glides instead of stepping.</summary>
    internal void UpdatePlayhead()
    {
        double raw = -1;
        foreach (var child in Group.Children)
        {
            foreach (var active in _player.ActiveCues)
            {
                if (active.CueId != child.Id)
                    continue;
                raw = Math.Max(raw, Math.Max(0, child.TimelineStartMs) + Math.Max(0, child.PreWaitMs) + active.PositionMs);
                break;
            }
        }

        if (raw < 0)
        {
            _lastRawPlayheadMs = -1;
            PlayheadMs = -1;
            return;
        }

        var now = DateTime.UtcNow;
        if (Math.Abs(raw - _lastRawPlayheadMs) > 0.5)
        {
            _lastRawPlayheadMs = raw;
            _lastRawSampleUtc = now;
            PlayheadMs = raw;
            return;
        }

        PlayheadMs = _player.IsTransportPaused
            ? raw
            : raw + (now - _lastRawSampleUtc).TotalMilliseconds;
    }

    public void Dispose()
    {
        _playheadTimer?.Stop();
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        Group.PropertyChanged -= OnGroupPropertyChanged;
    }
}
