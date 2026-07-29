using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public partial class CuePlayerViewModel : ViewModelBase
{
    public CueHotkeyProfile Hotkeys { get; set; } = new();

    public bool IsNDIAvailable => RuntimeModules.IsNDIAvailable;

    /// <summary>Whether the projectM visualizer is available - gates the per-composition VIZ toggle.</summary>
    public bool IsProjectMAvailable => RuntimeModules.IsProjectMAvailable;

    private CancellationTokenSource? _transportRunCts;

    /// <summary>Cancellation scope of the headless run in each NON-selected list (cross-list merged
    /// session). One per list, so a schedule/trigger re-firing list B replaces only B's own run -
    /// <see cref="_transportRunCts"/> (the visible transport) is never touched by it, and vice versa.
    /// Stop / Panic cancel every scope, matching their session-wide StopAll.</summary>
    private readonly Dictionary<CueListEditorViewModel, CancellationTokenSource> _foreignListRuns = new();

    /// <summary>
    /// Host-provided media execution callback. When null, media cues only update transport state.
    /// </summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueExecutor { get; set; }

    /// <summary>Manual fire for a media cue nested inside a Group. The host assigns a per-cue runtime
    /// transport slot so it can play alongside other children instead of replacing the group's active cue.</summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueIndependentExecutor { get; set; }

    /// <summary>Playlist crossfade fire (cue, window, curve, ct → error detail or null): fires the next
    /// pick into its authored group with an overlap window so the outgoing item fades out under it - the
    /// framework's dual-voice crossfade (Ideas/Dual-Voice-Crossfade-Design.md). Wired by
    /// <see cref="CueShowSessionCoordinator"/>; null hosts (tests without the seam) never advance early -
    /// their playlists keep the butt-splice natural-end path.</summary>
    public Func<MediaCueNode, TimeSpan, S.Media.Session.FadeCurve, CancellationToken, Task<string?>>?
        MediaCueCrossfadeExecutor { get; set; }

    /// <summary>
    /// Host-provided coordinated group execution callback. Opens all cues in parallel, then starts
    /// them in sync. When null, falls back to dispatching each cue independently.
    /// </summary>
    public Func<IReadOnlyList<MediaCueNode>, CancellationToken, Task<string?>>? MediaCueGroupExecutor { get; set; }

    /// <summary>
    /// Host-provided action execution callback. When null, action cues only update transport state.
    /// </summary>
    public Func<ActionCueNode, CancellationToken, Task<string?>>? ActionCueExecutor { get; set; }

    /// <summary>Host-provided visualizer-cue executor (#26): start/stop the projectM layer on a
    /// composition with placement. Wired by <see cref="CueShowSessionCoordinator"/>.</summary>
    public Func<VisualizerCueNode, CancellationToken, Task<string?>>? VisualizerCueExecutor { get; set; }

    /// <summary>Host-provided Fade-cue executor: (fade cue, resolved target cue ids, ct) → error detail
    /// or null. The VM resolves WHICH cues fade (explicit stable-id targets expanded through groups, or
    /// every active cue for TargetAllPlaying); the host ramps each target's clip via the session. Wired
    /// by <see cref="CueShowSessionCoordinator"/>.</summary>
    public Func<FadeCueNode, IReadOnlyList<Guid>, CancellationToken, Task<string?>>? FadeCueExecutor { get; set; }

    /// <summary>Hot-updates the placement of a running visualizer surface. Visualizers are not session
    /// clips, so they need a separate callback from <see cref="UpdateActiveCueVideoPlacementCallback"/>.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveVisualizerPlacementCallback { get; set; }

    /// <summary>Requests a preset advance on the visualizer currently attached to a composition.</summary>
    public Func<Guid, Task<bool>>? NextVisualizerPresetCallback { get; set; }

    /// <summary>Host-provided stop callback - Stop / Panic forwards the resolved effective fade
    /// (cue-list/app-settings precedence, Panic's hard cut) so the playback engine can tear down its
    /// session. Optional; null in tests.</summary>
    public Func<CueStopFadeRequest, Task>? StopPlaybackCallback { get; set; }

    /// <summary>Host-provided pause callback - Pause/Resume forwards to this so the playback
    /// engine freezes active media instead of only deferring pending cue delays.</summary>
    public Func<bool, Task>? SetPlaybackPausedCallback { get; set; }

    /// <summary>Host-provided preview callbacks (Phase 5.5). Null in tests.</summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? PreviewCueCallback { get; set; }
    public Func<Task>? StopPreviewCallback { get; set; }
    public Func<Guid, TimeSpan, Task>? SeekCueCallback { get; set; }

    private bool MediaExecutionConfigured => MediaCueExecutor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewingSelectedCue))]
    [NotifyPropertyChangedFor(nameof(IsCueScrubberVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(TogglePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekActiveCueFromScrubberCommand))]
    private Guid? _previewingCueId;

    public bool IsPreviewing => PreviewingCueId is not null;

    public bool IsPreviewingSelectedCue =>
        PreviewingCueId is { } id && SelectedCueNode?.Id == id;

    public string PreviewButtonLabel =>
        IsPreviewingSelectedCue ? Strings.StopPreviewCueButton : Strings.PreviewCueButton;

    public ObservableCollection<PreviewAudioDeviceOption> PreviewAudioDevices { get; } = new();

    [ObservableProperty]
    private PreviewAudioDeviceOption? _selectedPreviewAudioDevice;

    // Device-dependence fix #1: distinguishes operator picks (UI selection or a restored project choice)
    // from automatic preselection, so only real choices are persisted and automatic ones stay re-derivable
    // when the configured output lines change.
    private bool _isAutomaticPreviewDeviceSelection;

    /// <summary>True once the operator picked a preview device (or a project restored one) - automatic
    /// derivation from the configured cue output lines then stops overriding the selection.</summary>
    public bool HasExplicitPreviewAudioDeviceChoice { get; private set; }

    partial void OnSelectedPreviewAudioDeviceChanged(PreviewAudioDeviceOption? value)
    {
        if (!_isAutomaticPreviewDeviceSelection && value is not null)
            HasExplicitPreviewAudioDeviceChoice = true;
        OnPropertyChanged(nameof(PreviewAudioDeviceIndex));
    }

    public int? PreviewAudioDeviceIndex => SelectedPreviewAudioDevice?.DeviceIndex;

    public void RefreshPreviewAudioDevices()
    {
        PreviewAudioDevices.Clear();
        PreviewAudioDevices.Add(new PreviewAudioDeviceOption(null, Strings.Format(nameof(Strings.DefaultDeviceLabel))));
        // Runs in the MainViewModel ctor - on a machine without the portaudio native library the
        // enumeration throws DllNotFoundException and takes the whole process down before the first
        // frame. MediaRuntime already degrades to other backends; the preview picker must too.
        if (RuntimeModules.IsPortAudioAvailable)
        {
            foreach (var dev in S.Media.Audio.PortAudio.PortAudioDeviceCatalog.EnumerateOutputDevices())
                PreviewAudioDevices.Add(new PreviewAudioDeviceOption(dev.GlobalDeviceIndex, dev.Name));
        }
        ApplyAutomaticPreviewDeviceSelection();
    }

    /// <summary>Preselects the preview device while the operator has made no explicit choice: the first
    /// configured PortAudio cue output line's device when one resolves, else "Default device" (fix #1 -
    /// preview on a show machine must not implicitly land on the house default when lines are configured).</summary>
    private void ApplyAutomaticPreviewDeviceSelection()
    {
        if (HasExplicitPreviewAudioDeviceChoice && SelectedPreviewAudioDevice is not null)
            return;
        // Index match first (the id the runtime saves), device name second (indices shift across restarts).
        var derived = AvailableOutputs
            .Select(l => l.Definition)
            .OfType<Models.PortAudioOutputDefinition>()
            .Where(d => d.UsesPortAudioBackend)
            .Select(d => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex == d.GlobalDeviceIndex)
                         ?? PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is not null
                             && string.Equals(o.DisplayName, d.DeviceName, StringComparison.Ordinal)))
            .FirstOrDefault(o => o is not null);
        _isAutomaticPreviewDeviceSelection = true;
        try
        {
            SelectedPreviewAudioDevice = derived ?? PreviewAudioDevices.FirstOrDefault();
        }
        finally
        {
            _isAutomaticPreviewDeviceSelection = false;
        }
    }

    /// <summary>The preview-device choice to persist with the project: null while the operator never picked
    /// one (the automatic first-configured-line derivation stays live on load), "" for an explicit
    /// "Default device", else the picked device's name (stable across restarts, unlike its index).</summary>
    public string? BuildPreviewAudioDeviceSnapshot() =>
        !HasExplicitPreviewAudioDeviceChoice ? null
        : SelectedPreviewAudioDevice is not { DeviceIndex: not null } sel ? string.Empty
        : sel.DisplayName;

    /// <summary>Restores a persisted preview-device choice (see <see cref="BuildPreviewAudioDeviceSnapshot"/>).
    /// A persisted device that is no longer present is ignored - the selection falls back to the automatic
    /// derivation instead of pinning a stale name.</summary>
    public void RestorePreviewAudioDevice(string? persistedDeviceName)
    {
        var option = persistedDeviceName switch
        {
            null => null,
            "" => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is null),
            _ => PreviewAudioDevices.FirstOrDefault(o => o.DeviceIndex is not null
                && string.Equals(o.DisplayName, persistedDeviceName, StringComparison.Ordinal)),
        };
        if (option is not null)
        {
            SelectedPreviewAudioDevice = option; // counts as an explicit choice - it round-trips on save
            return;
        }
        HasExplicitPreviewAudioDeviceChoice = false;
        ApplyAutomaticPreviewDeviceSelection();
    }

    private float[]? _selectedCueWaveform;
    private int _selectedCueWaveformRevision;
    private CancellationTokenSource? _waveformCts;

    public float[]? SelectedCueWaveform
    {
        get => _selectedCueWaveform;
        private set { _selectedCueWaveform = value; OnPropertyChanged(); }
    }

    public int SelectedCueWaveformRevision
    {
        get => _selectedCueWaveformRevision;
        private set { _selectedCueWaveformRevision = value; OnPropertyChanged(); }
    }

    public bool HasSelectedCueWaveform =>
        HasSelectedMediaCueWithAudio && SelectedCueWaveform is { Length: > 0 };

    private void ExtractCueWaveform(CueNodeViewModel? cue)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;

        if (cue is not { Kind: CueNodeKind.Media } || !cue.SourceHasAudio)
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        var source = cue.MediaSourceItem;
        var path = source is FilePlaylistItem f ? f.Path : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;
        _ = Task.Run(async () =>
        {
            // Progressive display: throttled partial snapshots fill the editor waveform in left-to-right.
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, ct, partial =>
            {
                if (ct.IsCancellationRequested)
                    return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    SelectedCueWaveform = partial;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            });
            if (!ct.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SelectedCueWaveform = peaks;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            }
        }, ct);
    }

    /// <summary>Visible when the selected cue is active in the Now Playing panel (Phase 5.5.2).</summary>
    public bool IsCueScrubberVisible =>
        SelectedCueNode is not null
        && (ActiveCues.Any(a => a.CueId == SelectedCueNode.Id) || IsPreviewingSelectedCue);

    [ObservableProperty]
    private double _cueScrubberValue;

    public CuePlayerViewModel()
    {
        CueLists.CollectionChanged += OnCueListsCollectionChanged;
        var initial = new CueListEditorViewModel(Strings.DefaultCueListName);
        CueLists.Add(initial);
        SelectedCueList = initial;
    }

    // Lists whose Name we watch (see OnCueListNameChanged). A HashSet, not a list: ApplyCueLists
    // clears and refills the collection, and CollectionChanged fires per operation.
    private readonly HashSet<CueListEditorViewModel> _watchedCueListNames = new();

    private void OnCueListsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        foreach (var gone in _watchedCueListNames.Where(list => !CueLists.Contains(list)).ToList())
        {
            gone.PropertyChanged -= OnCueListNameChanged;
            _watchedCueListNames.Remove(gone);
        }

        foreach (var list in CueLists)
            if (_watchedCueListNames.Add(list))
                list.PropertyChanged += OnCueListNameChanged;

        RefreshNowPlayingListNames();
        RefreshArmedScopeTooltips();
    }

    /// <summary>A rename has to re-stamp the LIVE Now-Playing rows: rows fired from a non-selected list
    /// carry that list's name (<see cref="ForeignListNameOf"/>), and nothing else observed the name, so
    /// a renamed list kept its old prefix on every row already on screen until it ended.</summary>
    private void OnCueListNameChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName != nameof(CueListEditorViewModel.Name))
            return;
        RefreshNowPlayingListNames();
    }

    /// <summary>Wire the cue player to the shared output registry. Audio routes and video output
    /// bindings pick lines from this list directly - no per-cue-list device config.</summary>
    // Video output lines we've hooked for window-resize → PlacementCanvasAspect refresh (see
    // WatchVideoOutputLinesForResize). Held so the handlers can be detached before a rebucket.
    private readonly List<OutputLineViewModel> _resizeWatchedOutputLines = new();

    public void SetAvailableOutputs(ObservableCollection<OutputLineViewModel> outputs)
    {
        AvailableOutputs = outputs;
        outputs.CollectionChanged += (_, _) => RefreshAvailableOutputBuckets();
        RefreshAvailableOutputBuckets();
    }

    private void RefreshAvailableOutputBuckets()
    {
        AvailableAudioOutputs.Clear();
        AvailableVideoOutputs.Clear();
        foreach (var line in AvailableOutputs)
        {
            if (line.Definition is Models.PortAudioOutputDefinition)
            {
                AvailableAudioOutputs.Add(line);
            }
            else if (line.Definition is Models.LocalVideoOutputDefinition)
            {
                AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.NDIOutputDefinition ndi)
            {
                if (ndi.StreamMode != NDIOutputStreamMode.VideoOnly)
                    AvailableAudioOutputs.Add(line);
                if (ndi.StreamMode != NDIOutputStreamMode.AudioOnly)
                    AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.FileOutputDefinition file)
            {
                // Encode lines route like NDI carriers: cues bind video and matrix-route audio onto the
                // pre-defined tracks (the combined sink's concatenated channels). Frames/samples only
                // flow while the line is ARMED - a disarmed line is a silent target, not an error.
                var mode = file.EffectiveEncode.OutputMode;
                if (mode != "VideoOnly")
                    AvailableAudioOutputs.Add(line);
                if (mode != "AudioOnly")
                    AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.LiveStreamOutputDefinition stream)
            {
                var mode = stream.EffectiveEncode.OutputMode;
                if (mode != "VideoOnly")
                    AvailableAudioOutputs.Add(line);
                if (mode != "AudioOnly")
                    AvailableVideoOutputs.Add(line);
            }
        }
        WatchVideoOutputLinesForResize();
        ResolveAllBindingLineRefs();
        // Line-up changes re-derive the preview preselect (no-op once the operator picked a device):
        // the first configured PortAudio line's device beats the implicit "Default device".
        ApplyAutomaticPreviewDeviceSelection();
    }

    // A local output's window resize replaces the line's Definition (OutputManagementViewModel
    // .NotifyLocalPreviewResized); watch each video line so PlacementCanvasAspect re-reads the new window
    // size and the placement canvas re-lays-out to match. Re-subscribed whenever the bucket is rebuilt.
    private void WatchVideoOutputLinesForResize()
    {
        foreach (var line in _resizeWatchedOutputLines)
            line.PropertyChanged -= OnAvailableOutputLineChanged;
        _resizeWatchedOutputLines.Clear();
        foreach (var line in AvailableVideoOutputs)
        {
            line.PropertyChanged += OnAvailableOutputLineChanged;
            _resizeWatchedOutputLines.Add(line);
        }
    }

    private void OnAvailableOutputLineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputLineViewModel.Definition)
            or nameof(OutputLineViewModel.LiveVideoWidth)
            or nameof(OutputLineViewModel.LiveVideoHeight))
            OnPropertyChanged(nameof(PlacementCanvasAspect));
    }

    private OutputLineViewModel? ResolveOutputLine(Guid lineId) =>
        AvailableOutputs.FirstOrDefault(l => l.Definition.Id == lineId);

    /// <summary>Walks every loaded cue list and refreshes the resolved <c>LineRef</c> on each
    /// audio route + video output binding. Called when the available output set changes (lines
    /// added/removed/swapped) so the row dots and tooltips stay accurate.</summary>
    private void ResolveAllBindingLineRefs()
    {
        foreach (var list in CueLists)
        {
            foreach (var binding in list.VideoOutputs)
                binding.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(list.Nodes);
        }
    }

    private void ResolveLineRefsInNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var route in node.AudioRoutes)
                route.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(node.Children);
        }
    }

    public ObservableCollection<CueListEditorViewModel> CueLists { get; } = new();

    public IReadOnlyList<CueEndBehavior> CueEndBehaviors { get; } = Enum.GetValues<CueEndBehavior>();
    public IReadOnlyList<CueTriggerMode> CueTriggerModes { get; } = Enum.GetValues<CueTriggerMode>();
    public IReadOnlyList<CueFadeCurve> CueFadeCurves { get; } = Enum.GetValues<CueFadeCurve>();
    public IReadOnlyList<CueScheduleKind> CueScheduleKinds { get; } = Enum.GetValues<CueScheduleKind>();
    public IReadOnlyList<CueTimecodeRate> CueTimecodeRates { get; } = Enum.GetValues<CueTimecodeRate>();
    public IReadOnlyList<CueGroupFireMode> GroupFireModes { get; } = Enum.GetValues<CueGroupFireMode>();
    public IReadOnlyList<CuePlaylistEndBehavior> PlaylistEndBehaviors { get; } = Enum.GetValues<CuePlaylistEndBehavior>();
    public IReadOnlyList<CueLayerPosition> LayerPositions { get; } = Enum.GetValues<CueLayerPosition>();

    public IReadOnlyList<TextAlignH> TextHAlignOptions { get; } = Enum.GetValues<TextAlignH>();

    public IReadOnlyList<TextAlignV> TextVAlignOptions { get; } = Enum.GetValues<TextAlignV>();

    /// <summary>Installed system font family names for the text-cue font dropdown. The dropdown actually binds to
    /// the selected node's <c>FontFamilyOptions</c> (which also pins the cue's current family, e.g. the embedded
    /// "Inter" default); this stays for anything that wants the plain system list.</summary>
    public IReadOnlyList<string> AvailableFontFamilies => FontCatalog.SystemFamilies;

    [ObservableProperty]
    private CueListEditorViewModel? _selectedCueList;

    [ObservableProperty]
    private CueNodeViewModel? _selectedCueNode;

    /// <summary>All cue nodes the operator currently has highlighted in the tree (multi-select).
    /// The drawer still shows fields from the singular <see cref="SelectedCueNode"/>, but
    /// "+ Route" / "+ Placement" fan their action out across every media cue in this list - so
    /// the operator can stage a route on 11 audio cues in one click.</summary>
    private readonly List<CueNodeViewModel> _selectedCueNodes = new();

    public IReadOnlyList<CueNodeViewModel> SelectedCueNodes => _selectedCueNodes;

    /// <summary>Called by <c>CuePlayerView</c>'s row-selection changed handler with the live set
    /// of selected nodes. Keeps the singular <see cref="SelectedCueNode"/> as the primary
    /// (first in the list) so all the existing drawer bindings keep working.</summary>
    public void UpdateSelection(IReadOnlyList<CueNodeViewModel> selected)
    {
        _selectedCueNodes.Clear();
        _selectedCueNodes.AddRange(selected);
        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        ResubscribeMultiEditSelection();
        RefreshMultiEditSelectionState();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    /// <summary>Records an explicit row press even when the operator clicks the already-selected row and the
    /// grid therefore emits no SelectionChanged event.</summary>
    public void MarkCueForNextGo(CueNodeViewModel cue)
    {
        if (IsInCurrentCueTree(cue))
            _selectedCuePendingForGo = true;
    }

    [ObservableProperty]
    private CueCompositionViewModel? _selectedComposition;

    [ObservableProperty]
    private CueVideoOutputBindingViewModel? _selectedVideoOutput;

    [ObservableProperty]
    private CueAudioRouteViewModel? _selectedAudioRoute;

    [ObservableProperty]
    private CueVideoPlacementViewModel? _selectedVideoPlacement;

    [ObservableProperty]
    private ActionEndpoint? _selectedActionEndpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private CueNodeViewModel? _standbyCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private CueNodeViewModel? _currentCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    [NotifyPropertyChangedFor(nameof(TransportStateColor))]
    private bool _isTransportPaused;

    [ObservableProperty]
    private string? _statusMessage;

    private static readonly TimeSpan StatusMessageAutoClearDelay = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _statusMessageClearCts;

    [ObservableProperty]
    private bool _isCueEditMode = true;

    /// <summary>Master "Schedules armed" gate for wall-clock cue triggers (Ideas/CuePlayer-
    /// Enhancements.md §4). Deliberately SESSION-scoped and never persisted: arming schedules is a
    /// per-show act, so every app start (and project load) begins disarmed. Schedules fire only while
    /// this is on AND <see cref="IsCueEditMode"/> is off - an operator editing at 14:59 must not have
    /// Q50 fire into the room. <c>CueSchedulerService</c> observes this and the toggle in the cue
    /// transport row binds it.</summary>
    [ObservableProperty]
    private bool _schedulesArmed;

    /// <summary>Scheduling covers EVERY loaded list (cross-list merged session): a schedule in a
    /// non-selected list fires into the same <c>ShowSession</c> without moving the visible transport.
    /// The old "other lists will NOT fire" arm warning is gone with the scoping it described; the arm
    /// toggle's tooltip now carries the live armed-item count instead.</summary>
    partial void OnSchedulesArmedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(SchedulesArmedTooltip));
    }

    /// <summary>Live tooltip for the Schedules-armed toggle: the static explanation plus how many
    /// enabled schedules are armed and across how many loaded lists (the cross-list scope the merged
    /// session gives them).</summary>
    public string SchedulesArmedTooltip
    {
        get
        {
            var (items, lists) = CountAcrossLists(node => node.HasSchedule && node.ScheduleEnabled);
            var text = items == 0
                ? Strings.SchedulesArmedToggleTooltip
                : Strings.SchedulesArmedToggleTooltip + Environment.NewLine
                  + Strings.Format(nameof(Strings.SchedulesArmedScopeCountFormat), items, lists);
            // The MTC chase line only exists while some list actually carries a Timecode schedule.
            return TimecodeChaseStatus is { Length: > 0 } chase
                ? text + Environment.NewLine + chase
                : text;
        }
    }

    /// <summary>Live MTC chase readout ("Timecode 01:23:45:12 @ 25 fps" / parked / no signal), written
    /// by <c>CueSchedulerService</c> each sweep and null unless a loaded list carries a Timecode
    /// schedule. Feeds the transport-row chip and the Schedules-armed tooltip; session-transient,
    /// never persisted.</summary>
    private string? _timecodeChaseStatus;

    public string? TimecodeChaseStatus
    {
        get => _timecodeChaseStatus;
        internal set
        {
            if (!SetProperty(ref _timecodeChaseStatus, value))
                return;
            OnPropertyChanged(nameof(HasTimecodeChaseStatus));
            OnPropertyChanged(nameof(SchedulesArmedTooltip));
        }
    }

    /// <summary>Transport-row visibility for the chase chip.</summary>
    public bool HasTimecodeChaseStatus => !string.IsNullOrEmpty(TimecodeChaseStatus);

    /// <summary>Live tooltip for the Triggers-armed toggle - <see cref="SchedulesArmedTooltip"/>'s
    /// sibling, counting cues with at least one ENABLED trigger binding.</summary>
    public string TriggersArmedTooltip
    {
        get
        {
            var (items, lists) = CountAcrossLists(node => node.HasActiveTriggers);
            return items == 0
                ? Strings.TriggersArmedToggleTooltip
                : Strings.TriggersArmedToggleTooltip + Environment.NewLine
                  + Strings.Format(nameof(Strings.TriggersArmedScopeCountFormat), items, lists);
        }
    }

    /// <summary>(matching cue count, number of loaded lists holding at least one match).</summary>
    private (int Items, int Lists) CountAcrossLists(Func<CueNodeViewModel, bool> predicate)
    {
        var items = 0;
        var lists = 0;
        foreach (var list in CueLists)
        {
            var inList = EnumerateAllCueNodes(list.Nodes).Count(predicate);
            if (inList == 0)
                continue;
            items += inList;
            lists++;
        }

        return (items, lists);
    }

    /// <summary>Refreshes both arm tooltips (their counts span every loaded list, so a list switch,
    /// a list add/remove and a project load all change them).</summary>
    private void RefreshArmedScopeTooltips()
    {
        OnPropertyChanged(nameof(SchedulesArmedTooltip));
        OnPropertyChanged(nameof(TriggersArmedTooltip));
    }

    /// <summary>Master "Triggers armed" gate for per-cue MIDI/OSC/hotkey trigger bindings
    /// (Ideas/CuePlayer-Enhancements.md §6). Deliberately a SEPARATE toggle from
    /// <see cref="SchedulesArmed"/> - an operator arming wall-clock schedules must not silently
    /// open the MIDI/OSC surface (and vice versa) - but with identical semantics: session-scoped,
    /// never persisted, defaults OFF, and bindings fire only while this is on AND
    /// <see cref="IsCueEditMode"/> is off. <c>CueTriggerService</c> observes this and the toggle in
    /// the cue transport row binds it.</summary>
    [ObservableProperty]
    private bool _triggersArmed;

    /// <summary>Trigger bindings cover EVERY loaded list, exactly like schedules (see
    /// <see cref="OnSchedulesArmedChanged"/>) - the count lives in the toggle's tooltip.</summary>
    partial void OnTriggersArmedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TriggersArmedTooltip));
    }

    /// <summary>Hotkey entry for trigger bindings, set by the host (MainViewModel) to
    /// <c>CueTriggerService.TryHandleHotkey</c>; the cue view's transport-key handler calls it LAST
    /// (after the transport keys and the legacy per-cue hotkey). Null in tests.</summary>
    public Func<Avalonia.Input.KeyEventArgs, bool>? TriggerHotkeyProbe { get; set; }

    /// <summary>The binding row currently capturing the next incoming MIDI message ("Learn"),
    /// or null. Session-transient; <c>CueTriggerService</c> fills the row and clears this. Learn
    /// works regardless of the armed gate - it is an EDIT-mode affordance (the control workspace's
    /// I/O must be armed for messages to flow at all).</summary>
    [ObservableProperty]
    private CueTriggerBindingViewModel? _midiLearnTarget;

    partial void OnMidiLearnTargetChanged(
        CueTriggerBindingViewModel? oldValue, CueTriggerBindingViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsMidiLearning = false;
        if (newValue is not null)
            newValue.IsMidiLearning = true;
    }

    /// <summary>Host callback - pushes the master-trim scale (0..1) into the playback session
    /// (<c>ShowSession.SetMasterTrimAsync</c>). Wired by <c>CueShowSessionCoordinator</c>; null in
    /// tests, where the slider still tracks its value.</summary>
    public Func<float, Task>? SetMasterTrimCallback { get; set; }

    /// <summary>The fader's bottom of travel: at (or below) this the trim is a hard 0 (silence),
    /// not 10^(-60/20).</summary>
    public const double MasterTrimFloorDb = -60.0;

    /// <summary>The transport row's "Master" fader (Ideas/CuePlayer-Enhancements.md §6): a live
    /// session-wide trim over EVERY playing cue (and inherited by cues fired while reduced), in dB
    /// (<see cref="MasterTrimFloorDb"/>..0 with 0 = unity; the floor maps to silence). The linear
    /// factor (<see cref="MasterTrimLinear"/>) multiplies fades/envelopes/cue levels in the session -
    /// a manual show-level trim, not a stop. Deliberately SESSION-scoped and never persisted (the
    /// <see cref="SchedulesArmed"/> precedent): every app start begins at unity. Double-click on the
    /// slider resets it (<see cref="ResetMasterTrimCommand"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterTrimDisplay))]
    [NotifyPropertyChangedFor(nameof(MasterTrimLinear))]
    private double _masterTrimDb;

    partial void OnMasterTrimDbChanged(double value)
    {
        var clamped = Math.Clamp(double.IsNaN(value) ? 0.0 : value, MasterTrimFloorDb, 0.0);
        if (clamped != value)
        {
            MasterTrimDb = clamped; // re-enters this handler with the clamped value
            return;
        }

        _ = SetMasterTrimCallback?.Invoke(MasterTrimLinear);
    }

    /// <summary>The fader's dB position as the linear scale the session multiplies by
    /// (dB -&gt; 10^(dB/20); the <see cref="MasterTrimFloorDb"/> floor -&gt; 0).</summary>
    public float MasterTrimLinear =>
        MasterTrimDb <= MasterTrimFloorDb ? 0f : (float)Math.Pow(10.0, MasterTrimDb / 20.0);

    /// <summary>dB readout beside the fader ("0.0 dB" at unity, "-inf" at the floor).</summary>
    public string MasterTrimDisplay =>
        MasterTrimDb <= MasterTrimFloorDb
            ? "-inf dB"
            : MasterTrimDb.ToString("0.0;-0.0", CultureInfo.InvariantCulture) + " dB";

    /// <summary>Snaps the master fader back to unity (the slider's double-click gesture).</summary>
    [RelayCommand]
    private void ResetMasterTrim() => MasterTrimDb = 0.0;

    public ObservableCollection<CueNodeViewModel> VisibleNodes =>
        SelectedCueList?.Nodes ?? _emptyNodes;

    private readonly ObservableCollection<CueNodeViewModel> _emptyNodes = new();
    private readonly ObservableCollection<CueCompositionViewModel> _emptyCompositions = new();
    private readonly ObservableCollection<CueVideoOutputBindingViewModel> _emptyVideoOutputs = new();
    private readonly ObservableCollection<CueAudioRouteViewModel> _emptyAudioRoutes = new();
    private readonly ObservableCollection<CueVideoPlacementViewModel> _emptyVideoPlacements = new();
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    /// <summary>Bag of output lines the operator has created in the shared
    /// <c>OutputManagementView</c>. <see cref="MainViewModel"/> populates this via
    /// <see cref="SetAvailableOutputs"/>. Updates are live - adding/removing in OutputManagement
    /// flows through to the cue player's dropdowns immediately.</summary>
    public ObservableCollection<OutputLineViewModel> AvailableOutputs { get; private set; } = new();

    public ObservableCollection<OutputLineViewModel> AvailableAudioOutputs { get; } = new();
    public ObservableCollection<OutputLineViewModel> AvailableVideoOutputs { get; } = new();

    public ObservableCollection<CueCompositionViewModel> VisibleCompositions =>
        SelectedCueList?.Compositions ?? _emptyCompositions;

    public ObservableCollection<CueVideoOutputBindingViewModel> VisibleVideoOutputs =>
        SelectedCueList?.VideoOutputs ?? _emptyVideoOutputs;

    public ObservableCollection<CueAudioRouteViewModel> VisibleAudioRoutes =>
        SelectedAudioCue?.AudioRoutes ?? _emptyAudioRoutes;

    public ObservableCollection<CueVideoPlacementViewModel> VisibleVideoPlacements =>
        SelectedVideoCue?.VideoPlacements ?? _emptyVideoPlacements;

    /// <summary>Aspect ratio (w/h) of the composition the placement editor canvas should mirror.</summary>
    public double PlacementCanvasAspect
    {
        get
        {
            var comp = SelectedVideoPlacement is { } p
                ? SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                : null;
            comp ??= SelectedComposition ?? SelectedCueList?.Compositions.FirstOrDefault();
            if (comp is null)
                return 16.0 / 9.0;
            // A composition wired to a resizable local window follows that window's live aspect, so the
            // placement canvas matches what the operator actually sees on that output while they drag a
            // resize handle. Re-raised from OnAvailableOutputLineChanged when the window reports a resize.
            return TryGetBoundLocalWindowAspect(comp)
                ?? (comp is { Width: > 0, Height: > 0 } ? (double)comp.Width / comp.Height : 16.0 / 9.0);
        }
    }

    /// <summary>Live w/h of a resizable local window output the composition feeds, or null when the
    /// composition has no bound windowed output (then the canvas uses the composition's own resolution).</summary>
    private double? TryGetBoundLocalWindowAspect(CueCompositionViewModel comp)
    {
        if (SelectedCueList is null)
            return null;
        foreach (var binding in SelectedCueList.VideoOutputs)
        {
            if (binding.CompositionId != comp.Id)
                continue;
            if (binding.LineRef is { LiveVideoWidth: > 0, LiveVideoHeight: > 0 } live)
                return (double)live.LiveVideoWidth.Value / live.LiveVideoHeight.Value;
            if (binding.LineRef?.Definition is Models.LocalVideoOutputDefinition
                { WindowWidth: > 0, WindowHeight: > 0 } lv)
                return (double)lv.WindowWidth.Value / lv.WindowHeight.Value;
        }
        return null;
    }

    public bool HasSelectedMediaCue => SelectedMediaCue is not null;
    public bool HasSelectedTextCue => SelectedTextCue is not null;

    /// <summary>The selected cue sits directly in a Timeline-mode group, so its authored lane start
    /// (<see cref="CueNodeViewModel.TimelineStartMs"/>) is meaningful - shows the numeric drawer
    /// field (canvas-drag stays the primary editor).</summary>
    public bool IsSelectedCueInTimelineGroup =>
        SelectedCueNode is { } cue
        && FindContainingGroupPath(cue) is { Count: > 0 } path
        && ParseGroupFireMode(path[^1]) == CueGroupFireMode.Timeline;

    /// <summary>Image/text cues have no inherent length, so the operator sets the hold duration directly.</summary>
    public bool HasSelectedStaticCue =>
        SelectedStaticCue is not null;
    public bool HasSelectedActionCue => SelectedActionCue is not null;
    public bool HasSelectedCommentCue => SelectedCommentCue is not null;
    public bool HasSelectedGroupCue => SelectedGroupCue is not null;
    public bool HasSelectedCue => SelectedCueNode is not null;

    /// <summary>Video tab visibility: media cue AND the source actually has a video stream
    /// (decodable - covers regular video files and audio files with attached picture cover art).</summary>
    public bool HasSelectedMediaCueWithVideo =>
        SelectedVideoCue is not null;

    /// <summary>Audio tab visibility: media cue AND (the probe found audio OR the cue already
    /// has routes wired). The "has routes" branch keeps the tab editable for pre-Phase-5.1 cues
    /// that never went through the audio-stream probe but already have routes saved on disk.</summary>
    public bool HasSelectedMediaCueWithAudio =>
        SelectedAudioCue is not null;

    /// <summary>Operator hint banner - true when the only "video" the source offers is an
    /// attached picture (e.g. MP3 album art). The Video tab still works (the still frame can be
    /// placed into a composition for a now-playing slate) but it's worth flagging.</summary>
    public bool HasSelectedMediaCueWithAttachedPictureOnly =>
        SelectedVideoCue is { Kind: CueNodeKind.Media, SourceVideoIsAttachedPicture: true };

    /// <summary>Non-null when the selected media cue's probed frame rate doesn't divide evenly
    /// into at least one wired composition's canvas rate (Phase 5.9.2).</summary>
    public string? VideoFrameRateMismatchWarning => BuildVideoFrameRateMismatchWarning();

    public bool HasVideoFrameRateMismatchWarning =>
        !string.IsNullOrWhiteSpace(VideoFrameRateMismatchWarning);

    /// <summary>How many cues the operator currently has highlighted in the tree. The drawer
    /// shows a banner above the routes/placements lists when this is > 1 so the operator knows
    /// that "+ Route" / "+ Placement" applies to all of them, not just the primary.</summary>
    public int SelectedCueCount => _selectedCueNodes.Count;

    /// <summary>True iff <see cref="SelectedCueCount"/> > 1. Bound as the banner visibility flag -
    /// Avalonia's <c>ObjectConverters</c> doesn't ship a <c>GreaterThan</c>, so we expose a
    /// dedicated boolean rather than wire a per-view converter.</summary>
    public bool IsMultiSelected => _selectedCueNodes.Count > 1;

    public string SelectedCueDrawerTitle => SelectedCueNode is null
        ? Strings.SelectACueDrawerHint
        : string.IsNullOrWhiteSpace(SelectedCueNode.Number)
            ? $"{SelectedCueNode.Label} - {SelectedCueNode.KindLabel}"
            : $"{SelectedCueNode.Number} {SelectedCueNode.Label} - {SelectedCueNode.KindLabel}";
    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();

    public string SelectedActionEndpointSummary
    {
        get
        {
            if (SelectedActionCue is not { } actionCue)
                return string.Empty;

            if (!Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
                return Strings.NoActionTargetSelected;

            return SelectedActionEndpoint is null
                ? Strings.Format(nameof(Strings.ActionTargetMissingFormat), endpointId)
                : Strings.Format(
                    nameof(Strings.SelectedActionTargetFormat),
                    SelectedActionEndpoint.Name,
                    SelectedActionEndpoint.KindLabel,
                    SelectedActionEndpoint.Summary);
        }
    }

    public string TransportState =>
        CurrentCueNode is null
            ? Strings.Format(
                nameof(Strings.CueTransportStandbyFormat),
                StandbyCueNode is null ? Strings.NoneInParensLabel : CueDisplay(StandbyCueNode))
            : Strings.Format(
                nameof(Strings.CueTransportRunningFormat),
                IsTransportPaused ? Strings.CueTransportPausedLabel : Strings.CueTransportRunningLabel,
                CueDisplay(CurrentCueNode))
              + (StandbyCueNode is null
                  ? string.Empty
                  : Strings.Format(nameof(Strings.CueTransportNextFormat), CueDisplay(StandbyCueNode)));

    /// <summary>Fill for the transport-state chip - same palette as the media deck's state pill:
    /// green running, amber paused or standby-armed (ready), translucent gray idle.</summary>
    public Avalonia.Media.ISolidColorBrush TransportStateColor =>
        CurrentCueNode is null
            ? StandbyCueNode is null
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#44808080"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"))
            : IsTransportPaused
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F9A825"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2E7D32"));

    /// <summary>UX-10: true when the selected list has no cues (or none selected) - drives the empty-state
    /// call-to-action over the cue tree instead of a blank grid.</summary>
    public bool HasNoCues => SelectedCueList is null || SelectedCueList.Nodes.Count == 0;

    private CueListEditorViewModel? _watchedCueListForNodes;

    private void ResubscribeCueNodesWatch(CueListEditorViewModel? value)
    {
        if (_watchedCueListForNodes is not null)
            _watchedCueListForNodes.Nodes.CollectionChanged -= OnCueNodesCollectionChanged;
        _watchedCueListForNodes = value;
        if (value is not null)
            value.Nodes.CollectionChanged += OnCueNodesCollectionChanged;
    }

    private void OnCueNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(HasNoCues));
        RefreshCueTargetDisplays();
    }

    partial void OnSelectedCueListChanged(CueListEditorViewModel? value)
    {
        CancelTransportRun();
        ResubscribeCueNodesWatch(value);
        OnPropertyChanged(nameof(HasNoCues));
        OnPropertyChanged(nameof(VisibleNodes));
        OnPropertyChanged(nameof(VisibleCompositions));
        OnPropertyChanged(nameof(VisibleVideoOutputs));
        SelectedComposition = value?.Compositions.FirstOrDefault();
        SelectedVideoOutput = value?.VideoOutputs.FirstOrDefault();
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RemoveCueListCommand.NotifyCanExecuteChanged();
        OpenCueOutputSetupCommand.NotifyCanExecuteChanged();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        ResubscribeCompositionFpsWatch(value);
        RefreshCueTargetDisplays();
        // Cross-list session: rows fired from the list we just left need their list qualifier, and the
        // newly-selected list's own rows must drop theirs. The armed counts span every list, but the
        // "which list" part of the tooltip reads differently after a switch.
        RefreshNowPlayingListNames();
        RefreshArmedScopeTooltips();
    }

    private void RefreshCueTargetDisplays()
    {
        var nodes = EnumerateAllCueNodes().ToList();
        var byId = nodes.ToDictionary(node => node.Id);
        static string CueReference(CueNodeViewModel cue) =>
            string.IsNullOrWhiteSpace(cue.Number) ? cue.Label : $"#{cue.Number}";

        foreach (var node in nodes)
        {
            if (node.EndTargetCueId is { } endId)
            {
                node.TargetDisplayBase = byId.TryGetValue(endId, out var target)
                    ? $"End → {CueReference(target)}"
                    : "End → ?";
                continue;
            }

            if (node.Kind == CueNodeKind.Jump && node.JumpTargetIds.Count > 0)
            {
                var targets = string.Join(", ", node.JumpTargetIds.Select(id =>
                {
                    if (!byId.TryGetValue(id, out var target))
                        return "?";
                    var immediateCycle = ReferenceEquals(ResolveFireableCue(target), node);
                    return $"{CueReference(target)}{(immediateCycle ? " ⚠ cycle" : string.Empty)}";
                }));
                var mode = node.JumpRandom
                    ? node.JumpAvoidImmediateRepeat ? "Random (no repeat)" : "Random"
                    : string.Equals(node.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase)
                        ? "Standby"
                        : "Jump";
                node.TargetDisplayBase = $"{mode} → {targets}";
                continue;
            }

            if (node.Kind == CueNodeKind.Fade)
            {
                var level = node.FadeTargetLevelDb <= FadeCueNode.SilenceLevelDb
                    ? "silence"
                    : $"{node.FadeTargetLevelDb:0.#} dB";
                var targets = node.FadeTargetAllPlaying
                    ? "all playing"
                    : node.FadeTargetIds.Count == 0
                        ? "?"
                        : string.Join(", ", node.FadeTargetIds.Select(id =>
                            byId.TryGetValue(id, out var target) ? CueReference(target) : "?"));
                node.TargetDisplayBase = $"Fade → {targets} ({level})";
                continue;
            }

            node.TargetDisplayBase = string.Empty;
        }
    }

    private CueListEditorViewModel? _watchedCueListForFps;

    private void ResubscribeCompositionFpsWatch(CueListEditorViewModel? value)
    {
        if (_watchedCueListForFps is not null)
        {
            foreach (var comp in _watchedCueListForFps.Compositions)
                comp.CompositionFrameRateChanged -= OnCompositionFrameRateChanged;
        }

        _watchedCueListForFps = value;
        if (value is null)
            return;

        foreach (var comp in value.Compositions)
            comp.CompositionFrameRateChanged += OnCompositionFrameRateChanged;
        RefreshVideoFrameRateMismatchWarning();
    }

    private void OnCompositionFrameRateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshVideoFrameRateMismatchWarning();
    }

    private CueNodeViewModel? _watchedSelectedCueForProbe;

    private void OnSelectedCueProbeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem)
            or nameof(CueNodeViewModel.SourceCapabilitiesKnown)
            or nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture)
            or nameof(CueNodeViewModel.SourceFrameRateNum)
            or nameof(CueNodeViewModel.SourceFrameRateDen))
        {
            OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
            OnPropertyChanged(nameof(HasSelectedTextCue));
            OnPropertyChanged(nameof(HasSelectedStaticCue));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
            OnPropertyChanged(nameof(IsPreviewingSelectedCue));
            OnPropertyChanged(nameof(PreviewButtonLabel));
            OnPropertyChanged(nameof(IsCueScrubberVisible));
            RefreshVideoFrameRateMismatchWarning();
            SyncCueScrubberFromActiveSelection();
            TogglePreviewCommand.NotifyCanExecuteChanged();
            SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(CueNodeViewModel.SourceHasAudio))
                ExtractCueWaveform(_watchedSelectedCueForProbe);
            RefreshMultiEditSelectionState(resetSelectedItems:
                e.PropertyName is not nameof(CueNodeViewModel.MediaSourceItem));
        }
        else if (e.PropertyName is nameof(CueNodeViewModel.HasSubtitleTracks))
        {
            RefreshMultiEditSelectionState();
        }
    }

    private CueNodeViewModel? _preRollWatchedCue;

    /// <summary>Tracks the selected media/visualizer cue so that in-place edits to routes and placements
    /// can reach the live runtime. Media edits also re-warm standby pre-roll.</summary>
    private void WatchSelectedCueForPreRoll(CueNodeViewModel? value)
    {
        var next = value is { Kind: CueNodeKind.Media or CueNodeKind.Visualizer } ? value : null;
        if (ReferenceEquals(_preRollWatchedCue, next))
            return;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged -= OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged -= OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged -= OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        }

        _preRollWatchedCue = next;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged += OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged += OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged += OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
        }
    }

    private void OnWatchedCuePreRollPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The cue master level is baked into the routed gains: apply it to the running clip live
        // (same path as a per-route gain tweak) and let the stale-standby refresh re-prepare.
        if (e.PropertyName is nameof(CueNodeViewModel.LevelDb))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (e.PropertyName is nameof(CueNodeViewModel.StartOffsetMs)
            or nameof(CueNodeViewModel.EndOffsetMs)
            or nameof(CueNodeViewModel.Loop)
            or nameof(CueNodeViewModel.EndBehavior)
            or nameof(CueNodeViewModel.DurationMs)        // image/text duration drives the hold window
            or nameof(CueNodeViewModel.MediaSourceItem)   // text restyle replaces the source -> re-render
            or nameof(CueNodeViewModel.AudioTrackIndex)   // track change is part of the prepared-cue key
            or nameof(CueNodeViewModel.VideoTrackIndex))  // ditto for the video stream selection
            OnWatchedCueEdited();

        // A text/style edit replaces the TextPlaylistItem source; if that cue is playing, re-render its frame in
        // place so the change shows immediately (the deferred document rebuild otherwise only lands on the next
        // fire - see MainViewModel's reload deferral, which keeps the running cue from being torn down mid-edit).
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem))
            PushActiveTextUpdate();
    }

    private static readonly Microsoft.Extensions.Logging.ILogger LiveTextTrace =
        S.Media.Core.Diagnostics.MediaDiagnostics.CreateLogger("HaPlay.LiveText");

    private void PushActiveTextUpdate()
    {
        var watched = _preRollWatchedCue;
        var isText = watched?.MediaSourceItem is TextPlaylistItem;
        var isActive = watched is not null && _activeCueIds.Contains(watched.Id);
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(LiveTextTrace,
            "PushActiveTextUpdate: watched={Watched} isText={IsText} isActive={IsActive} hasCallback={HasCb} activeCount={Count}",
            watched?.Id, isText, isActive, UpdateActiveCueTextCallback is not null, _activeCueIds.Count);

        if (watched is { } cue
            && isText
            && UpdateActiveCueTextCallback is { } callback
            && isActive
            && cue.ToModel() is MediaCueNode model)
            _ = callback(cue.Id, model);
    }

    private void OnWatchedCueRouteCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        PushActiveAudioRoutesUpdate();
        // Add/Remove route commands already suggest a refresh, but a programmatic edit might not.
        OnWatchedCueEdited();
    }

    private void OnWatchedCuePlacementCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        OnWatchedCueEdited();
    }

    private void RebindItemSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var item in e.OldItems.OfType<ObservableObject>())
                item.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<ObservableObject>())
                item.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
    }

    private void OnWatchedRouteOrPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // LineRef is a resolved UI reference, not part of the cue's cache key - ignore it so a mere
        // output-line resolution doesn't churn pre-roll.
        if (e.PropertyName is nameof(CueAudioRouteViewModel.SourceChannel)
            or nameof(CueAudioRouteViewModel.OutputLineId)
            or nameof(CueAudioRouteViewModel.OutputChannel)
            or nameof(CueAudioRouteViewModel.GainDb)
            or nameof(CueAudioRouteViewModel.Muted))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (sender is CueVideoPlacementViewModel placement
            && IsVideoPlacementProperty(e.PropertyName))
        {
            if (IsLiveEditableVideoPlacementProperty(e.PropertyName))
                PushActiveVideoPlacementUpdate(placement);
            RefreshVideoFrameRateMismatchWarning();
        }
    }

    private static bool IsVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.CompositionId)
            or nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyColorHex)
            or nameof(CueVideoPlacementViewModel.ChromaKeySimilarity)
            or nameof(CueVideoPlacementViewModel.ChromaKeySmoothness)
            or nameof(CueVideoPlacementViewModel.ChromaKeySpill)
            or nameof(CueVideoPlacementViewModel.ColorAdjustEnabled)
            or nameof(CueVideoPlacementViewModel.ColorAdjustBrightness)
            or nameof(CueVideoPlacementViewModel.ColorAdjustContrast);

    private static bool IsLiveEditableVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom)
            or nameof(CueVideoPlacementViewModel.RotationDegrees)
            or nameof(CueVideoPlacementViewModel.VideoFx)
            or nameof(CueVideoPlacementViewModel.VideoFxEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyEnabled)
            or nameof(CueVideoPlacementViewModel.ChromaKeyColorHex)
            or nameof(CueVideoPlacementViewModel.ChromaKeySimilarity)
            or nameof(CueVideoPlacementViewModel.ChromaKeySmoothness)
            or nameof(CueVideoPlacementViewModel.ChromaKeySpill)
            or nameof(CueVideoPlacementViewModel.ColorAdjustEnabled)
            or nameof(CueVideoPlacementViewModel.ColorAdjustBrightness)
            or nameof(CueVideoPlacementViewModel.ColorAdjustContrast);

    /// <summary>Maps a cue-wide placement index to the placement's index AMONG THE CUE'S PLACEMENTS ON
    /// THE SAME COMPOSITION - the order the visualizer executor attached that composition's surface
    /// layers in, and therefore the index the live hot-update API addresses (#26 multi-placement).</summary>
    private static int VisualizerPlacementIndexOnComposition(CueNodeViewModel cue, int placementIndex)
    {
        var compositionId = cue.VideoPlacements[placementIndex].CompositionId;
        var indexOnComposition = 0;
        for (var i = 0; i < placementIndex; i++)
            if (cue.VideoPlacements[i].CompositionId == compositionId)
                indexOnComposition++;
        return indexOnComposition;
    }

    private void PushActiveVideoPlacementUpdate(CueVideoPlacementViewModel placement)
    {
        if (_preRollWatchedCue is not { } cue)
            return;

        var index = cue.VideoPlacements.IndexOf(placement);
        if (index < 0)
            return;

        // A visualizer is a persistent composition surface, not an active ShowSession clip. Its persistent
        // runtime latch outlives a finite Now-Playing row, and its layer has its own hot-update API.
        if (cue.Kind == CueNodeKind.Visualizer)
        {
            if (_runningVisualizers.ContainsKey(cue.Id)
                && UpdateActiveVisualizerPlacementCallback is { } visualizerCallback)
                _ = visualizerCallback(cue.Id, VisualizerPlacementIndexOnComposition(cue, index), placement.ToModel());
            return;
        }

        // Not running yet: the edited placement lives only in the cue model, and the backing ShowSession
        // document a GO fires from is NOT rebuilt on placement edits (only structural changes reload it).
        // Flag it stale so the next fire reloads with the current placement - otherwise the cue fires with
        // the placement captured at the last reload and the new geometry only takes hold once the operator
        // nudges it again (which then takes the live path below). A running cue is updated live instead.
        if (!_activeCueIds.Contains(cue.Id))
        {
            CueClipModelStaleCallback?.Invoke();
            return;
        }

        if (UpdateActiveCueVideoPlacementCallback is not { } callback)
            return;

        _ = callback(cue.Id, index, placement.ToModel());
    }

    private void PushActiveAudioRoutesUpdate()
    {
        if (_preRollWatchedCue is not { } cue
            || UpdateActiveCueAudioRoutesCallback is not { } callback
            || !_activeCueIds.Contains(cue.Id))
            return;

        var routes = cue.AudioRoutes.Select(route => route.ToModel()).ToArray();
        _ = callback(cue.Id, routes, cue.LevelDb);
    }

    /// <summary>An edit-relevant change to the watched (selected) cue: immediately flag its warm
    /// standby <see cref="PreparedCueState.Stale"/> so the badge reflects the drift, then request a
    /// debounced pre-roll refresh that re-prepares it.</summary>
    private void OnWatchedCueEdited()
    {
        if (_preRollWatchedCue is { } cue)
            CueStandbyInvalidated?.Invoke(this, cue.Id);
        SuggestPreRollRefresh();
    }

    /// <summary>Raised with a cue id when an in-place edit drifts that cue's warm standby out of date.
    /// The host marks the engine's prepared entry stale; the following refresh re-prepares it.</summary>
    public event EventHandler<Guid>? CueStandbyInvalidated;

    private void RefreshVideoFrameRateMismatchWarning()
    {
        OnPropertyChanged(nameof(VideoFrameRateMismatchWarning));
        OnPropertyChanged(nameof(HasVideoFrameRateMismatchWarning));
    }

    private string? BuildVideoFrameRateMismatchWarning()
    {
        if (SelectedVideoCue is not { Kind: CueNodeKind.Media } node || !node.SourceHasVideo)
            return null;
        if (!CueFrameRatePolicy.IsKnown(node.SourceFrameRateNum, node.SourceFrameRateDen))
            return null;
        if (SelectedCueList is null)
            return null;

        foreach (var placement in node.VideoPlacements)
        {
            var comp = SelectedCueList.Compositions.FirstOrDefault(c => c.Id == placement.CompositionId);
            if (comp is null)
                continue;
            if (!CueFrameRatePolicy.RatesMismatch(
                    node.SourceFrameRateNum, node.SourceFrameRateDen,
                    comp.FrameRateNum, comp.FrameRateDen))
                continue;

            var srcFps = FormatProbeFps(node.SourceFrameRateNum, node.SourceFrameRateDen);
            var canvasFps = FormatProbeFps(comp.FrameRateNum, comp.FrameRateDen);
            return Strings.Format(
                nameof(Strings.VideoFrameRateMismatchWarningFormat),
                srcFps,
                canvasFps,
                comp.DisplayName);
        }

        return null;
    }

    private static string FormatProbeFps(int num, int den)
    {
        if (den <= 0)
            return "?";
        var fps = num / (double)den;
        return fps >= 100 ? fps.ToString("0.#") : fps.ToString("0.###");
    }

    /// <summary>Drawer-editable cue number with the same uniqueness rule as the rename dialog: a
    /// duplicate anywhere in the list is rejected (number-based jump/feed references need it).</summary>
    public string SelectedCueNumber
    {
        get => SelectedCueNode?.Number ?? string.Empty;
        set
        {
            if (SelectedCueNode is not { } cue)
                return;
            var trimmed = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && EnumerateAllCueNodes().Any(c => !ReferenceEquals(c, cue)
                    && string.Equals(c.Number?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = Strings.Format(nameof(Strings.CueNumberDuplicateFormat), trimmed);
                OnPropertyChanged(nameof(SelectedCueNumber));
                OnPropertyChanged(nameof(SelectedEndTargetText)); // revert the editor to the old value
                return;
            }

            cue.Number = trimmed;
            StatusMessage = null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedCueNumber));
            OnPropertyChanged(nameof(SelectedEndTargetText));
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
            OnPropertyChanged(nameof(SelectedFadeTargetsText));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    public string SelectedCueLabel
    {
        get => SelectedCueNode?.Label ?? string.Empty;
        set
        {
            if (SelectedCueNode is { } cue)
                cue.Label = value ?? string.Empty;
            OnPropertyChanged(nameof(SelectedCueLabel));
            OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        }
    }

    /// <summary>Visualizer-cue feed sources as cue numbers (same pattern as jump targets).</summary>
    public string SelectedVisualizerFeedText
    {
        get
        {
            if (SelectedVisualizerCue is not { } viz)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", viz.VisualizerFeedCueIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedVisualizerCue is not { } viz)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var token in tokens)
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    c.Kind == CueNodeKind.Media
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !resolved.Contains(match.Id))
                    resolved.Add(match.Id);
                else if (match is null)
                    unknown.Add(token);
            }

            viz.VisualizerFeedCueIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Visualizer))
                if (!ReferenceEquals(target, viz))
                    target.VisualizerFeedCueIds = [.. resolved];
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        }
    }

    public bool SelectedVisualizerFeedAll
    {
        get => SelectedVisualizerCue is not { } v || v.VisualizerFeedAll;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerFeedAll = value;
                OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
            }
        }
    }

    /// <summary>Media end-trigger target (on natural end), entered by number and stored as a stable id.</summary>
    public string SelectedEndTargetText
    {
        get
        {
            if (SelectedMediaCue is not { } m || m.EndTargetCueId is not { } id)
                return string.Empty;
            var target = EnumerateAllCueNodes().FirstOrDefault(c => c.Id == id);
            return target is null ? "?" : (string.IsNullOrWhiteSpace(target.Number) ? target.Label : target.Number);
        }
        set
        {
            if (SelectedMediaCue is not { } m)
                return;
            var token = (value ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                m.EndTargetCueId = null;
            }
            else
            {
                var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                    !ReferenceEquals(c, m) && c.Kind is not CueNodeKind.Comment
                    && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    StatusMessage = Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), token);
                }
                else
                {
                    m.EndTargetCueId = match.Id;
                    StatusMessage = null;
                }
            }

            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedEndTargetText));
        }
    }

    /// <summary>Visualizer timeline duration in seconds (0 = infinite).</summary>
    public double SelectedVisualizerDurationSeconds
    {
        get => SelectedVisualizerCue is { } v ? v.VisualizerDurationMs / 1000.0 : 0;
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.VisualizerDurationMs = (int)Math.Clamp(value * 1000.0, 0, int.MaxValue);
                OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
            }
        }
    }

    /// <summary>Visualizer-cue resolution preset ("WxH@F") - the media player dialog's buttons.</summary>
    [RelayCommand]
    private void SetVisualizerCueResolution(string spec)
    {
        if (SelectedVisualizerCue is not { } viz)
            return;
        var parts = spec.Split(['x', '@']);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var w)
            || !int.TryParse(parts[1], out var h)
            || !int.TryParse(parts[2], out var f))
            return;
        viz.VisualizerRenderWidth = w;
        viz.VisualizerRenderHeight = h;
        viz.VisualizerRenderFps = f;
        OnPropertyChanged(nameof(SelectedVisualizerCue)); // refresh the numerics
    }

    [RelayCommand(CanExecute = nameof(CanNextVisualizerPreset))]
    private async Task NextVisualizerPresetAsync()
    {
        if (SelectedVisualizerCue is not { } cue
            || NextVisualizerPresetCallback is not { } callback)
            return;

        var compositionId = VisualizerTargetComposition(cue);
        if (compositionId != Guid.Empty)
            await callback(compositionId);
    }

    private bool CanNextVisualizerPreset() =>
        SelectedVisualizerCue is not null
        && NextVisualizerPresetCallback is not null;

    /// <summary>Drawer gate for the jump-cue section.</summary>
    public bool IsJumpCueSelected => SelectedJumpCue is not null;

    /// <summary>Drawer gate for the visualizer-cue section (#26).</summary>
    public bool IsVisualizerCueSelected => SelectedVisualizerCue is not null;

    public Guid SelectedVisualizerCompositionId
    {
        get => SelectedVisualizerCue?.VisualizerCompositionId ?? Guid.Empty;
        set
        {
            if (SelectedVisualizerCue is { } v)
                v.VisualizerCompositionId = value;
        }
    }

    public bool SelectedVisualizerStarts
    {
        get => SelectedVisualizerCue is { } v
               && !string.Equals(v.Extra, "Stop", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedVisualizerCue is { } v)
            {
                v.Extra = value ? "Start" : "Stop";
                OnPropertyChanged(nameof(SelectedVisualizerStarts));
            }
        }
    }

    /// <summary>Jump targets as CUE NUMBERS (display/entry); stored as stable cue IDs (renumber-safe).
    /// Setting parses a comma/space separated list (including inclusive hierarchical ranges such as
    /// 2.2-2.4), resolves each number against the list, keeps the resolvable ones, and reports unknowns
    /// in the status bar.</summary>
    public string SelectedJumpTargetsText
    {
        get
        {
            if (SelectedJumpCue is not { } jump)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", jump.JumpTargetIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedJumpCue is not { } jump)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var enteredToken in tokens)
            {
                foreach (var token in ExpandCueNumberToken(enteredToken))
                {
                    var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                        !ReferenceEquals(c, jump)
                        && c.Kind is not CueNodeKind.Comment
                        && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !resolved.Contains(match.Id))
                        resolved.Add(match.Id);
                    else if (match is null)
                        unknown.Add(token);
                }
            }

            jump.JumpTargetIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                if (!ReferenceEquals(target, jump))
                    target.JumpTargetIds = [.. resolved];
            foreach (var target in SelectedKindTargets(CueNodeKind.Jump))
                _lastRandomJumpTargetIds.Remove(target.Id);
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedJumpTargetsText));
        }
    }

    private const int MaximumCueNumberRangeSize = 10_000;

    /// <summary>
    /// Expands only unambiguous numeric, dot-hierarchical ranges. Other hyphenated values remain
    /// literal cue numbers, so a custom number such as "intro-1" retains its existing meaning.
    /// </summary>
    private static IReadOnlyList<string> ExpandCueNumberToken(string token)
    {
        var separator = token.IndexOf('-');
        if (separator <= 0
            || separator != token.LastIndexOf('-')
            || separator >= token.Length - 1
            || !TryParseHierarchicalCueNumber(token[..separator], out var start)
            || !TryParseHierarchicalCueNumber(token[(separator + 1)..], out var end)
            || start.Length != end.Length)
            return [token];

        for (var i = 0; i < start.Length - 1; i++)
        {
            if (start[i] != end[i])
                return [token];
        }

        var distance = Math.Abs((long)end[^1] - start[^1]);
        if (distance >= MaximumCueNumberRangeSize)
            return [token];

        var prefix = start.Length == 1
            ? string.Empty
            : $"{string.Join('.', start[..^1])}.";
        var step = start[^1] <= end[^1] ? 1 : -1;
        var expanded = new List<string>((int)distance + 1);
        for (var value = start[^1];; value += step)
        {
            expanded.Add($"{prefix}{value.ToString(CultureInfo.InvariantCulture)}");
            if (value == end[^1])
                break;
        }

        return expanded;
    }

    private static bool TryParseHierarchicalCueNumber(string text, out int[] components)
    {
        var parts = text.Split('.');
        components = new int[parts.Length];
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        return true;
    }

    public bool SelectedJumpRandom
    {
        get => SelectedJumpCue is { } j && j.JumpRandom;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpRandom = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpRandom));
            }
        }
    }

    public bool SelectedJumpAvoidImmediateRepeat
    {
        get => SelectedJumpCue is { } j && j.JumpAvoidImmediateRepeat;
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.JumpAvoidImmediateRepeat = value;
                _lastRandomJumpTargetIds.Remove(j.Id);
                RefreshCueTargetDisplays();
                OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
            }
        }
    }

    public bool SelectedJumpFiresTarget
    {
        get => SelectedJumpCue is { } j
               && !string.Equals(j.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (SelectedJumpCue is { } j)
            {
                j.SourceOrAction = value ? "fire" : "standby";
                RefreshCueTargetDisplays();
            }
        }
    }

    /// <summary>Drawer gate for the fade-cue section.</summary>
    public bool IsFadeCueSelected => SelectedFadeCue is not null;

    /// <summary>Fade targets as CUE NUMBERS (display/entry) stored as stable cue IDs - the Jump-targets
    /// pattern, including inclusive hierarchical ranges (2.2-2.4). Media cues and groups are valid
    /// targets (a group fades its descendant media cues).</summary>
    public string SelectedFadeTargetsText
    {
        get
        {
            if (SelectedFadeCue is not { } fade)
                return string.Empty;
            var byId = EnumerateAllCueNodes().ToDictionary(c => c.Id, c => c);
            return string.Join(", ", fade.FadeTargetIds.Select(id =>
                byId.TryGetValue(id, out var c)
                    ? (string.IsNullOrWhiteSpace(c.Number) ? c.Label : c.Number)
                    : "?"));
        }
        set
        {
            if (SelectedFadeCue is not { } fade)
                return;
            var tokens = (value ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var resolved = new List<Guid>();
            var unknown = new List<string>();
            foreach (var enteredToken in tokens)
            {
                foreach (var token in ExpandCueNumberToken(enteredToken))
                {
                    var match = EnumerateAllCueNodes().FirstOrDefault(c =>
                        !ReferenceEquals(c, fade)
                        && c.Kind is CueNodeKind.Media or CueNodeKind.Group
                        && string.Equals(c.Number?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !resolved.Contains(match.Id))
                        resolved.Add(match.Id);
                    else if (match is null)
                        unknown.Add(token);
                }
            }

            fade.FadeTargetIds = resolved;
            foreach (var target in SelectedKindTargets(CueNodeKind.Fade))
                if (!ReferenceEquals(target, fade))
                    target.FadeTargetIds = [.. resolved];
            StatusMessage = unknown.Count > 0
                ? Strings.Format(nameof(Strings.CueJumpUnknownNumbersFormat), string.Join(", ", unknown))
                : null;
            RefreshCueTargetDisplays();
            OnPropertyChanged(nameof(SelectedFadeTargetsText));
        }
    }

    /// <summary>Resolves the cue ids a Fade cue actually ramps (UI thread - reads transport state):
    /// every active cue for TargetAllPlaying, else the explicit targets with groups expanded to their
    /// descendant media cues.
    /// <para>Explicit targets resolve inside the fade cue's OWN list, exactly like a Jump cue's targets
    /// (<see cref="ExecuteJumpCueOnUi"/>): fade target ids are authored links WITHIN one list, and the
    /// cross-list merged session fires a foreign list's cues headlessly. Resolving against the SELECTED
    /// list instead found none of them, so a scheduled/triggered Fade in another list silently ramped
    /// nothing (or, worse, matched an unrelated id).</para></summary>
    internal IReadOnlyList<Guid> ResolveFadeCueTargetsOnUi(CueNodeViewModel fade)
    {
        if (fade.FadeTargetAllPlaying)
            return _activeCueIds.ToList();

        var byId = EnumerateAllCueNodesFor(fade).ToDictionary(c => c.Id, c => c);
        var resolved = new List<Guid>();
        foreach (var id in fade.FadeTargetIds)
        {
            if (!byId.TryGetValue(id, out var target))
                continue;
            if (target.Kind == CueNodeKind.Group)
            {
                foreach (var media in EnumerateMediaNodes(target.Children))
                    if (!resolved.Contains(media.Id))
                        resolved.Add(media.Id);
            }
            else if (target.Kind == CueNodeKind.Media && !resolved.Contains(target.Id))
            {
                resolved.Add(target.Id);
            }
        }

        return resolved;
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        // Programmatic selections (add/duplicate/load) do not necessarily pass through
        // UpdateSelection. Keep the multi-edit subscriptions aligned in that path too.
        ResubscribeMultiEditSelection();
        _selectedCuePendingForGo = value is not null;
        // Loaded trigger rows carry no edit-time transport-clash veto (FromModel has no player
        // context); stamp it when the row surfaces in the drawer. Selecting away cancels MIDI learn.
        MidiLearnTarget = null;
        if (value is not null)
        {
            foreach (var row in value.Triggers)
                row.HotkeyConflictProbe ??= ProbeTriggerHotkeyConflict;
        }
        OnPropertyChanged(nameof(SelectedCueNumber));
        OnPropertyChanged(nameof(SelectedEndTargetText));
        OnPropertyChanged(nameof(SelectedCueLabel));
        OnPropertyChanged(nameof(SelectedVisualizerFeedText));
        OnPropertyChanged(nameof(SelectedVisualizerFeedAll));
        OnPropertyChanged(nameof(SelectedVisualizerDurationSeconds));
        OnPropertyChanged(nameof(IsJumpCueSelected));
        OnPropertyChanged(nameof(IsVisualizerCueSelected));
        OnPropertyChanged(nameof(SelectedVisualizerCompositionId));
        OnPropertyChanged(nameof(SelectedVisualizerStarts));
        OnPropertyChanged(nameof(SelectedJumpTargetsText));
        OnPropertyChanged(nameof(SelectedJumpRandom));
        OnPropertyChanged(nameof(SelectedJumpAvoidImmediateRepeat));
        OnPropertyChanged(nameof(SelectedJumpFiresTarget));
        OnPropertyChanged(nameof(SelectedFadeCue));
        OnPropertyChanged(nameof(IsFadeCueSelected));
        OnPropertyChanged(nameof(SelectedFadeTargetsText));
        // The selected cue's probe fields can land AFTER selection (when the operator picks a
        // file via "Browse media…"; the probe is async). Re-subscribe so the Video tab visibility
        // re-evaluates when the probe finishes.
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged -= OnSelectedCueProbeChanged;
        _watchedSelectedCueForProbe = value;
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged += OnSelectedCueProbeChanged;

        // In-place edits to the selected cue's routes/placements/offsets don't go through the
        // add/remove commands (those already suggest a refresh), so watch the node directly to keep
        // its standby pre-roll warm after gain/channel/opacity/offset tweaks. Debounced downstream.
        WatchSelectedCueForPreRoll(value);

        // Cues loaded from disk have no probed track list yet - fill the audio-track picker lazily
        // on first selection (stream-table probe only, no decoder build).
        if (value is { Kind: CueNodeKind.Media })
            _ = EnsureAudioTrackChoicesAsync(value);

        SelectedAudioRoute = SelectedAudioCue?.AudioRoutes.FirstOrDefault();
        SelectedVideoPlacement = SelectedVideoCue?.VideoPlacements.FirstOrDefault();
        RemoveNodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedTextCue));
        OnPropertyChanged(nameof(HasSelectedStaticCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(HasSelectedCue));
        OnPropertyChanged(nameof(IsSelectedCueInTimelineGroup));
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        ApplyCueDownmixPresetCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        FireSelectedVisualizerCommand.NotifyCanExecuteChanged();
        FireSelectedCueNowCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
        NextVisualizerPresetCommand.NotifyCanExecuteChanged();
        BrowseMediaSourceCommand.NotifyCanExecuteChanged();
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
        EditActionCueCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPreviewingSelectedCue));
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        RefreshVideoFrameRateMismatchWarning();
        ExtractCueWaveform(value);
        RefreshMultiEditSelectionState(resetSelectedItems: false);

        if (SelectedActionCue is { } actionCue && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        else
            SelectedActionEndpoint = null;
    }

    partial void OnSelectedAudioRouteChanged(CueAudioRouteViewModel? value)
    {
        WatchSelectedAudioRouteForMultiEdit(value);
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoPlacementChanged(CueVideoPlacementViewModel? value)
    {
        WatchSelectedVideoPlacementForMultiEdit(value);
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        ApplyPlacementLayoutCommand.NotifyCanExecuteChanged();
        EditSelectedPlacementVideoFxCommand.NotifyCanExecuteChanged();
        ApplyCropPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
        RefreshVideoFrameRateMismatchWarning();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCueEditModeChanged(bool value)
    {
        _ = value;
        MoveSelectedCueUpCommand.NotifyCanExecuteChanged();
        MoveSelectedCueDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnStandbyCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        RebuildUpcomingCues();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        if (!_suppressStandbyPreRollRefresh)
            SuggestPreRollRefresh();
    }

    /// <summary>Host subscribes to warm the selected player's pre-roll cache (§5.7).</summary>
    public event EventHandler? PreRollRefreshSuggested;

    private void SuggestPreRollRefresh() => PreRollRefreshSuggested?.Invoke(this, EventArgs.Empty);

    private bool _suppressStandbyPreRollRefresh;

    /// <summary>The fireable cue order starting at standby (or list start) - the window each
    /// pre-roll query pulls its targets from. Callers apply a per-source-type filter, so a
    /// non-matching cue (e.g. an NDI cue while scanning for files) is skipped without changing
    /// the file-media target set.</summary>
    private IEnumerable<CueNodeViewModel> EnumeratePreRollWindow()
    {
        if (SelectedCueList is null)
            yield break;

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            yield break;

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        for (var i = startIdx; i < ordered.Count; i++)
            yield return ordered[i];
    }

    private IReadOnlyList<CueNodeViewModel> GetStandbySimultaneousGroupTargets()
    {
        // Timeline groups pre-roll like simultaneous ones: every planned child is opened from standby.
        if (StandbyCueNode is not { Kind: CueNodeKind.Group } group
            || ParseGroupFireMode(group) is not (CueGroupFireMode.FireAllSimultaneously or CueGroupFireMode.Timeline))
            return [];

        return BuildTriggerPlan(group).Select(step => step.Cue).ToList();
    }

    /// <summary>Next file media cues from standby for the cue engine's own opened/routed cache.</summary>
    public IReadOnlyList<MediaCueNode> GetPreparedMediaCueTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<MediaCueNode>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not FilePlaylistItem
                    || cue.ToModel() is not MediaCueNode media)
                    continue;
                groupTargets.Add(media);
            }

            return groupTargets;
        }

        var targets = new List<MediaCueNode>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not FilePlaylistItem
                || cue.ToModel() is not MediaCueNode media)
                continue;
            targets.Add(media);
        }

        return targets;
    }

    /// <summary>NDI media cues in the pre-roll window (§6.11).</summary>
    public IReadOnlyList<(Guid CueId, NDIInputPlaylistItem Item)> GetNDIPreConnectTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<(Guid, NDIInputPlaylistItem)>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not NDIInputPlaylistItem ndi
                    || !ndi.SupportsPreRoll())
                    continue;
                groupTargets.Add((cue.Id, ndi));
            }

            return groupTargets;
        }

        var targets = new List<(Guid, NDIInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not NDIInputPlaylistItem ndi
                || !ndi.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, ndi));
        }

        return targets;
    }

    partial void OnCurrentCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        PauseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Set of cue ids the playback engine reports as currently active. Maintained via
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/> from the host (MainViewModel wires
    /// these to the engine's events). Used by <see cref="RefreshRowStatuses"/> so every active
    /// cue lights up - the singular <see cref="CurrentCueNode"/> only tracks the last-started
    /// one for AutoFollow / transport-state purposes.</summary>
    private readonly HashSet<Guid> _activeCueIds = new();

    /// <summary>True while any cue is currently playing (fired via <see cref="OnCueStarted"/>, not yet
    /// <see cref="OnCueEnded"/>). The authoritative "is something playing" signal - used to defer the ShowSession
    /// document rebuild on an in-place edit so it never stops a running cue (unlike a clock's <c>IsRunning</c>,
    /// which is unreliable for a video-only held/text clip).</summary>
    public bool HasActiveCues => _activeCueIds.Count > 0;

    /// <summary>Rows visible in the right-side Now Playing panel. Maintained by
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/>; their progress fields update via
    /// <see cref="OnCueProgress"/>.</summary>
    public ObservableCollection<ActiveCueViewModel> ActiveCues { get; } = new();

    /// <summary>
    /// P4 (plan §3.2) - what the Now Playing panel renders: <see cref="ActiveCueViewModel"/> for
    /// standalone cues, <see cref="ActiveGroupViewModel"/> aggregating active cues that share a
    /// parent group node. <see cref="ActiveCues"/> stays the flat source of truth.
    /// </summary>
    public ObservableCollection<object> NowPlayingRows { get; } = new();

    /// <summary>Host-provided coordinated multi-cue seek (engine.SeekCuesAsync): all targets pause,
    /// seek in parallel and resume through one barrier so group children stay aligned. When null
    /// (tests), group seeks fall back to sequential per-cue <see cref="SeekCueCallback"/> calls.</summary>
    public Func<IReadOnlyList<(Guid CueId, TimeSpan Position)>, Task>? SeekCuesCallback { get; set; }

    /// <summary>Group-row seek: every child seeks to the same fraction of ITS OWN duration (keeps
    /// proportional alignment for staggered-length children). Same padlock gate as single rows.</summary>
    public async Task SeekActiveGroupToFractionAsync(ActiveGroupViewModel group, double fraction)
    {
        if (!NowPlayingSeekUnlocked)
            return;

        if (SeekCuesCallback is { } batched)
        {
            var clamped = Math.Clamp(fraction, 0.0, 1.0);
            var targets = group.Children
                .Where(child => child.DurationMs > 0)
                .Select(child => (child.CueId, TimeSpan.FromMilliseconds(child.DurationMs * clamped)))
                .ToList();
            if (targets.Count > 0)
                await batched(targets).ConfigureAwait(false);
            return;
        }

        foreach (var child in group.Children.ToArray())
            await SeekActiveCueToFractionAsync(child, fraction).ConfigureAwait(false);
    }

    /// <summary>The group node a cue sits under, or null for top-level cues. Searches the tree of the
    /// list that OWNS the cue - the selected one for a visible cue, otherwise the loaded list a
    /// cross-list schedule/trigger fired from (its cues share the merged session's Now-Playing panel).</summary>
    private CueNodeViewModel? FindParentGroupOf(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } owner ? Search(owner.Nodes, null, node) : null;

    private static CueNodeViewModel? Search(
        IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel? parent, CueNodeViewModel node)
    {
        foreach (var candidate in nodes)
        {
            if (ReferenceEquals(candidate, node))
                return parent is { IsGroup: true } ? parent : null;
            if (Search(candidate.Children, candidate, node) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>The owning list's name when <paramref name="node"/> lives in a list OTHER than the
    /// selected one, else null (no prefix for the visible list's own rows).</summary>
    private string? ForeignListNameOf(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } owner && !ReferenceEquals(owner, SelectedCueList)
            ? owner.Name
            : null;

    /// <summary>Re-stamps the Now-Playing list qualifier after a list switch: rows fired from what is now
    /// the selected list drop their prefix, and rows of the list just left gain one.</summary>
    private void RefreshNowPlayingListNames()
    {
        foreach (var row in ActiveCues)
            row.ListName = ForeignListNameOf(row.Node);
        foreach (var group in NowPlayingRows.OfType<ActiveGroupViewModel>())
            group.ListName = ForeignListNameOf(group.GroupNode);
    }

    private void AddNowPlayingRow(ActiveCueViewModel entry)
    {
        if (FindParentGroupOf(entry.Node) is { } groupNode)
        {
            var group = NowPlayingRows.OfType<ActiveGroupViewModel>()
                .FirstOrDefault(g => g.GroupId == groupNode.Id);
            if (group is null)
            {
                group = new ActiveGroupViewModel(groupNode) { ListName = ForeignListNameOf(groupNode) };
                NowPlayingRows.Add(group);
            }

            group.Children.Add(entry);
            RefreshPlaylistNowPlayingStatus(groupNode.Id);
            return;
        }

        NowPlayingRows.Add(entry);
    }

    private void RemoveNowPlayingRow(Guid cueId)
    {
        for (var i = NowPlayingRows.Count - 1; i >= 0; i--)
        {
            switch (NowPlayingRows[i])
            {
                case ActiveCueViewModel single when single.CueId == cueId:
                    NowPlayingRows.RemoveAt(i);
                    break;
                case ActiveGroupViewModel group:
                    for (var c = group.Children.Count - 1; c >= 0; c--)
                        if (group.Children[c].CueId == cueId)
                            group.Children.RemoveAt(c);
                    if (group.Children.Count == 0)
                        NowPlayingRows.RemoveAt(i);
                    break;
            }
        }
    }

    /// <summary>Cues that *will* fire once the operator presses Go from the current Standby
    /// position - used by the Now Playing panel's Upcoming section.</summary>
    public ObservableCollection<CueNodeViewModel> UpcomingCues { get; } = new();

    /// <summary>Host-provided per-cue stop callback (engine.StopCueAsync). The Now Playing
    /// panel's per-row ✕ button forwards through this; null in tests.</summary>
    public Func<Guid, Task>? CancelCueCallback { get; set; }

    // ----- UI rewrite P4: Now Playing row seek (with lock) --------------------------------------

    /// <summary>Unlocks dragging/tapping the Now Playing progress bars to seek. Default locked -
    /// the panel sits next to GO, so accidental seeks during a show must be opt-in (plan §3.2).</summary>
    [ObservableProperty]
    private bool _nowPlayingSeekUnlocked;

    /// <summary>Seeks an active cue to a 0..1 fraction of its duration. No-op while locked or when
    /// the cue has no known duration yet.</summary>
    public Task SeekActiveCueToFractionAsync(ActiveCueViewModel cue, double fraction)
    {
        if (!NowPlayingSeekUnlocked || cue.DurationMs <= 0 || SeekCueCallback is null)
            return Task.CompletedTask;
        var clamped = Math.Clamp(fraction, 0.0, 1.0);
        return SeekCueCallback(cue.CueId, TimeSpan.FromMilliseconds(cue.DurationMs * clamped));
    }

    /// <summary>Host callback for mutating a placement's already-running compositor slot while the
    /// selected cue is active. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveCueVideoPlacementCallback { get; set; }

    /// <summary>Host callback raised when an <em>idle</em> (not-yet-fired) cue's clip model is edited in a way
    /// the backing show document captures only at (re)load - e.g. a video placement nudge. The host flags its
    /// document stale so the next GO rebuilds it with the current model, instead of firing stale geometry. A
    /// running cue is updated live via <see cref="UpdateActiveCueVideoPlacementCallback"/> and does not raise this.</summary>
    public Action? CueClipModelStaleCallback { get; set; }

    /// <summary>Host callback for reconciling the selected cue's running audio routes after route
    /// row edits. No-op in tests or when the cue is not playing.</summary>
    /// <summary>Host callback: re-apply a playing cue's audio routing live. Receives the edited routes
    /// plus the cue's master <see cref="CueNodeViewModel.LevelDb"/> (baked into the routed gains by the
    /// mapper, so every live re-apply must carry it or the cue would pop to unity).</summary>
    public Func<Guid, IReadOnlyList<CueAudioRoute>, double, Task>? UpdateActiveCueAudioRoutesCallback { get; set; }

    /// <summary>Host callback to live-re-render a playing text cue after a text/style edit (so it updates in
    /// place instead of only on the next fire). No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, MediaCueNode, Task>? UpdateActiveCueTextCallback { get; set; }

    /// <summary>Host callback - live-applies an output mapping (warp sections) to a running
    /// composition: (compositionId, outputLineId, mapping). No-op when the composition isn't live.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool>? UpdateOutputMappingCallback { get; set; }

    /// <summary>Host callback - live-applies a composition-level video FX mapping to a running composition.</summary>
    public Func<Guid, CueOutputMapping?, bool>? UpdateCompositionVideoFxCallback { get; set; }

    /// <summary>Host callback - shows/hides the mapping calibration grid for one composition output.</summary>
    public Func<Guid, Guid, CueOutputMapping?, bool, bool>? SetCompositionTestPatternCallback { get; set; }

    /// <summary>Engine callback - cue began playing. Marks its row Current and pushes a new
    /// <see cref="ActiveCueViewModel"/> into <see cref="ActiveCues"/>.</summary>
    public void OnCueStarted(Guid cueId)
    {
        _activeCueIds.Add(cueId);
        RefreshRowStatuses();

        var node = FindNodeById(cueId);
        if (node is not null && !ActiveCues.Any(a => a.CueId == cueId))
        {
            var entry = new ActiveCueViewModel(node, cueId, id => _ = CancelActiveCueAsync(id))
            {
                DurationMs = Math.Max(0, node.EffectiveDurationMs),
                // Cross-list fire: the cue plays in the SAME merged session, so it belongs in
                // Now-Playing - qualified with its list name so the operator can tell it apart from
                // the visible list's rows.
                ListName = ForeignListNameOf(node),
            };
            ActiveCues.Add(entry);
            AddNowPlayingRow(entry);
        }
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - preview stopped. Clears preview state on the VM.</summary>
    public void OnPreviewEnded(Guid cueId)
    {
        _ = cueId;
        if (PreviewingCueId is null) return;
        PreviewingCueId = null;
        StatusMessage = Strings.PreviewStoppedStatus;
    }

    /// <summary>Engine callback - cue stopped (natural end, Stop, or Panic). Clears Current
    /// status and removes the matching <see cref="ActiveCueViewModel"/>.</summary>
    public void OnCueEnded(Guid cueId)
    {
        _activeCueIds.Remove(cueId);
        RefreshRowStatuses();

        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (ActiveCues[i].CueId == cueId)
                ActiveCues.RemoveAt(i);
        RemoveNowPlayingRow(cueId);
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        StopSelectedCueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback - progress sample for one active cue. Updates the row's
    /// position so the progress bar and "mm:ss / mm:ss" display advance.</summary>
    public void OnCueProgress(CuePlaybackProgress p)
    {
        foreach (var a in ActiveCues)
        {
            if (a.CueId != p.CueId) continue;
            a.PositionMs = (long)p.Position.TotalMilliseconds;
            if (p.Duration > TimeSpan.Zero)
                a.DurationMs = (long)p.Duration.TotalMilliseconds;
            break;
        }

        if (SelectedCueNode?.Id == p.CueId && p.Duration > TimeSpan.Zero)
            CueScrubberValue = p.Position.TotalMilliseconds * 1000.0 / p.Duration.TotalMilliseconds;
    }

    private void SyncCueScrubberFromActiveSelection()
    {
        if (SelectedCueNode is null)
            return;
        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;
        var positionMs = active?.PositionMs ?? 0;
        CueScrubberValue = positionMs * 1000.0 / durationMs;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreview))]
    private async Task TogglePreviewAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } node)
            return;

        if (IsPreviewingSelectedCue)
        {
            if (StopPreviewCallback is not null)
                await StopPreviewCallback();
            PreviewingCueId = null;
            StatusMessage = Strings.PreviewStoppedStatus;
            return;
        }

        if (PreviewCueCallback is null)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        if (node.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueInvalidMediaCue;
            return;
        }

        using var cts = new CancellationTokenSource();
        var err = await PreviewCueCallback(media, cts.Token);
        if (!string.IsNullOrWhiteSpace(err))
        {
            StatusMessage = err;
            return;
        }

        PreviewingCueId = node.Id;
        StatusMessage = Strings.Format(nameof(Strings.PreviewingCueStatusFormat), CueDisplay(node));
    }

    private bool CanTogglePreview() =>
        SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanSeekActiveCueFromScrubber))]
    private async Task SeekActiveCueFromScrubberAsync()
    {
        if (SelectedCueNode is null || SeekCueCallback is null)
            return;

        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;

        var position = TimeSpan.FromMilliseconds(CueScrubberValue * durationMs / 1000.0);
        await SeekCueCallback(SelectedCueNode.Id, position);
    }

    private bool CanSeekActiveCueFromScrubber() => IsCueScrubberVisible;

    /// <summary>The cue node with this id in ANY loaded list (selected first). Cue ids are Guids, so the
    /// cross-list lookup can never alias; it is what lets a cue fired from a non-selected list appear in
    /// Now-Playing and answer progress/end events from the merged session.</summary>
    private CueNodeViewModel? FindNodeById(Guid id)
    {
        foreach (var node in EnumerateAllCueNodesAcrossLists())
            if (node.Id == id)
                return node;
        return null;
    }

    /// <summary>Operator-facing "number label" for a cue id (status/alert messages), or null when the id
    /// isn't in the loaded lists. UI thread.</summary>
    internal string? DescribeCue(Guid id) =>
        FindNodeById(id) is { } node
            ? string.IsNullOrWhiteSpace(node.Number) ? node.Label : $"{node.Number} {node.Label}".TrimEnd()
            : null;

    /// <summary>Host callback - the set of warmed (standby-ready) cues changed. Snapshot lists the cue ids
    /// that are currently warmed. Walks every loaded cue node and sets <c>IsPreRollWarm</c>
    /// accordingly so the status badge column can render the warming indicator (Phase 5.7.2).
    /// <para>This method does not marshal threads on its own; the host wiring (MainViewModel)
    /// hops onto the UI dispatcher before invoking, because the underlying
    /// <c>ShowSession.PreparedCuesChanged</c> can fire from any thread.</para>
    /// </summary>
    public void OnPreRollCacheChanged(IReadOnlyCollection<Guid> warmCueIds)
    {
        var warm = warmCueIds as HashSet<Guid> ?? new HashSet<Guid>(warmCueIds);
        foreach (var node in EnumerateAllCueNodes())
        {
            var shouldBeWarm = warm.Contains(node.Id);
            if (node.IsPreRollWarm != shouldBeWarm)
                node.IsPreRollWarm = shouldBeWarm;
        }
    }

    /// <summary>Host callback - richer per-cue standby preparation states changed (Idle/Preparing/
    /// Ready/Failed). Cues absent from the snapshot are Idle. Drives the status badge + tooltip and,
    /// via <see cref="CueNodeViewModel.PreRollState"/>, keeps <c>IsPreRollWarm</c> in sync.</summary>
    public void OnPreparedCueStatesChanged(IReadOnlyList<Playback.CuePreparationStatus> states)
    {
        var byId = states.ToDictionary(s => s.CueId);
        foreach (var node in EnumerateAllCueNodes())
        {
            if (byId.TryGetValue(node.Id, out var status))
            {
                node.PreRollState = status.State;
                node.PreRollError = status.Error;
            }
            else
            {
                node.PreRollState = PreparedCueState.Idle;
                node.PreRollError = null;
            }
        }
    }

    private void RebuildUpcomingCues()
    {
        UpcomingCues.Clear();
        if (SelectedCueList is null) return;
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            foreach (var c in simultaneousGroup)
            {
                if (_activeCueIds.Contains(c.Id)) continue;
                UpcomingCues.Add(c);
            }
            return;
        }

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0) return;

        var anchor = StandbyCueNode ?? ordered.FirstOrDefault();
        if (anchor is null) return;
        var startIdx = ordered.FindIndex(c => ReferenceEquals(c, ResolveFireableCue(anchor) ?? anchor));
        if (startIdx < 0) return;

        for (var i = startIdx; i < ordered.Count; i++)
        {
            var c = ordered[i];
            // Don't list already-active cues as upcoming - they're in the Active section.
            if (_activeCueIds.Contains(c.Id)) continue;
            UpcomingCues.Add(c);
        }
    }

    private void RefreshRowStatuses()
    {
        foreach (var node in EnumerateAllCueNodes())
        {
            var status = _activeCueIds.Contains(node.Id)
                ? CueRowStatus.Current
                : ReferenceEquals(node, StandbyCueNode)
                    ? CueRowStatus.Standby
                    : CueRowStatus.Idle;
            if (node.RowStatus != status)
                node.RowStatus = status;
        }
    }

    partial void OnIsTransportPausedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TransportState));
        OnPropertyChanged(nameof(TransportStateColor));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        _statusMessageClearCts?.Cancel();
        _statusMessageClearCts?.Dispose();
        _statusMessageClearCts = null;

        if (string.IsNullOrWhiteSpace(value))
            return;

        // Status surfaces as a top-right toast (MainView overlay) instead of the old inline banner,
        // which pushed the whole cue list down mid-click. Severity is a keyword heuristic - cue
        // status strings carry no structured level.
        ToastCenter.Post(ClassifyStatusSeverity(value), value);

        var cts = new CancellationTokenSource();
        _statusMessageClearCts = cts;
        _ = ClearStatusMessageLaterAsync(value, cts.Token);
    }

    private static ToastSeverity ClassifyStatusSeverity(string message) =>
        message.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || message.Contains("error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drift", StringComparison.OrdinalIgnoreCase)
        || message.Contains("drop", StringComparison.OrdinalIgnoreCase)
            ? ToastSeverity.Warning
            : ToastSeverity.Info;

    private async Task ClearStatusMessageLaterAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusMessageAutoClearDelay, token).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && string.Equals(StatusMessage, message, StringComparison.Ordinal))
                    StatusMessage = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ICollection<CueNodeViewModel>? SelectedParentCollection()
    {
        if (SelectedCueList is null)
            return null;
        if (SelectedCueNode is null)
            return SelectedCueList.Nodes;
        if (SelectedCueNode.IsGroup)
            return SelectedCueNode.Children;
        return FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) ?? SelectedCueList.Nodes;
    }

    private static ICollection<CueNodeViewModel>? FindParentCollection(
        ICollection<CueNodeViewModel> nodes,
        CueNodeViewModel target)
    {
        if (nodes.Contains(target))
            return nodes;
        foreach (var n in nodes)
        {
            var c = FindParentCollection(n.Children, target);
            if (c is not null) return c;
        }
        return null;
    }

    private static bool RemoveNodeRecursive(ICollection<CueNodeViewModel> nodes, CueNodeViewModel target)
    {
        if (nodes.Remove(target))
            return true;
        foreach (var n in nodes)
            if (RemoveNodeRecursive(n.Children, target))
                return true;
        return false;
    }

    private bool IsInCurrentCueTree(CueNodeViewModel node) =>
        SelectedCueList is not null && ContainsNode(SelectedCueList.Nodes, node);

    private void PruneSelectionToCurrentTree()
    {
        var removed = _selectedCueNodes.RemoveAll(n => !IsInCurrentCueTree(n));
        if (removed == 0 && (SelectedCueNode is null || IsInCurrentCueTree(SelectedCueNode)))
            return;

        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    private void ReconcileTransportAfterTreeMutation(int removedFireableIndex)
    {
        var ordered = EnumerateFireableCueOrder().ToList();

        if (CurrentCueNode is not null && !IsInCurrentCueTree(CurrentCueNode))
        {
            CurrentCueNode = null;
            IsTransportPaused = false;
        }

        if (StandbyCueNode is not null && !IsInCurrentCueTree(StandbyCueNode))
        {
            StandbyCueNode = ordered.Count == 0
                ? null
                : ordered[Math.Clamp(removedFireableIndex < 0 ? 0 : removedFireableIndex, 0, ordered.Count - 1)];
        }
        else
        {
            RefreshRowStatuses();
            RebuildUpcomingCues();
        }

        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddCueList()
    {
        var list = new CueListEditorViewModel(Strings.Format(nameof(Strings.CueListNameFormat), CueLists.Count + 1));
        CueLists.Add(list);
        SelectedCueList = list;
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCueList))]
    private void RemoveCueList()
    {
        if (SelectedCueList is null || CueLists.Count <= 1)
            return;
        var idx = CueLists.IndexOf(SelectedCueList);
        CueLists.RemoveAt(idx);
        SelectedCueList = CueLists[Math.Clamp(idx - 1, 0, CueLists.Count - 1)];
        SelectedCueNode = null;
    }

    private bool CanRemoveCueList() => SelectedCueList is not null && CueLists.Count > 1;

    /// <summary>Next auto-number: global max numeric + 1 (#27 uniqueness - the old siblings.Count+1
    /// collided across nesting levels, e.g. root "2" vs a group's second child "2").</summary>
    private string NextNumber(ICollection<CueNodeViewModel> siblings)
    {
        _ = siblings; // kept for call-site compatibility
        var max = 0;
        foreach (var c in EnumerateAllCueNodes())
            if (int.TryParse(c.Number, out var n) && n > max)
                max = n;
        return (max + 1).ToString();
    }

    private CueNodeViewModel? ResolveFireableCue(CueNodeViewModel? node)
    {
        if (node is null)
            return null;
        if (node.Kind != CueNodeKind.Group)
            return node;
        // A playlist/armed-list group resolves to its armed NEXT PICK, so GO, standby pre-roll and
        // the upcoming list all agree on the same item (§3 + spec point 5).
        if (IsPlaylistGroup(node))
            return PeekPlaylistPick(node);
        return EnumerateFireableCueOrder(node.Children).FirstOrDefault();
    }

    private IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder() =>
        SelectedCueList is null ? [] : EnumerateFireableCueOrder(SelectedCueList.Nodes);

    private static IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Group)
            {
                foreach (var child in EnumerateFireableCueOrder(node.Children))
                    yield return child;
                continue;
            }
            yield return node;
        }
    }

    private static CueNodeViewModel? NextCueAfter(CueNodeViewModel current, IReadOnlyList<CueNodeViewModel> ordered)
    {
        var idx = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ReferenceEquals(ordered[i], current))
                continue;
            idx = i;
            break;
        }
        if (idx < 0 || idx + 1 >= ordered.Count)
            return null;
        return ordered[idx + 1];
    }

    /// <summary>
    /// Resolves the trigger policy for a sequential transition. The flat fireable order omits Group rows,
    /// but crossing from outside into a Group must honor that Group's trigger mode; once inside the same
    /// Group, the destination cue's own mode applies. This makes a Manual Group a real boundary gate even
    /// when its first child is Auto-Follow/Auto-Continue.
    /// </summary>
    internal bool SequentialTransitionUsesMode(
        CueNodeViewModel from, CueNodeViewModel to, CueTriggerMode requiredMode)
    {
        var fromPath = FindContainingGroupPath(from);
        var toPath = FindContainingGroupPath(to);
        var shared = 0;
        while (shared < fromPath.Count
               && shared < toPath.Count
               && ReferenceEquals(fromPath[shared], toPath[shared]))
            shared++;

        // Entering nested groups requires every newly-crossed boundary to opt into the same automatic
        // transition. A Manual inner group must not be bypassed merely because its outer group is automatic.
        return shared < toPath.Count
            ? toPath.Skip(shared).All(group => group.TriggerMode == requiredMode)
            : to.TriggerMode == requiredMode;
    }

    /// <summary>The chain of groups enclosing <paramref name="target"/>, searched in the list that owns
    /// it: the selected list first (unchanged for every visible cue), then the other loaded lists so a
    /// cross-list fire resolves its own group modes / auto-follow boundaries.</summary>
    private IReadOnlyList<CueNodeViewModel> FindContainingGroupPath(CueNodeViewModel target)
    {
        if (SelectedCueList is { } selected && FindPathIn(selected.Nodes) is { } selectedPath)
            return selectedPath;
        foreach (var list in CueLists)
        {
            if (ReferenceEquals(list, SelectedCueList))
                continue;
            if (FindPathIn(list.Nodes) is { } foreignPath)
                return foreignPath;
        }

        return [];

        List<CueNodeViewModel>? FindPathIn(IEnumerable<CueNodeViewModel> roots)
        {
            var path = new List<CueNodeViewModel>();
            return Find(roots) ? path : null;

            bool Find(IEnumerable<CueNodeViewModel> nodes)
            {
                foreach (var node in nodes)
                {
                    if (ReferenceEquals(node, target))
                        return true;
                    if (!node.IsGroup)
                        continue;
                    path.Add(node);
                    if (Find(node.Children))
                        return true;
                    path.RemoveAt(path.Count - 1);
                }

                return false;
            }
        }
    }

    private static string CueDisplay(CueNodeViewModel cue) =>
        string.IsNullOrWhiteSpace(cue.Number)
            ? cue.Label
            : $"{cue.Number} {cue.Label}".Trim();

    /// <summary><see cref="CueDisplay"/>, prefixed with the owning list's name when the cue lives in a
    /// list OTHER than the selected one: cue numbers restart per list, so an unqualified "3 Stinger" in
    /// a status line would read as the visible list's cue 3. Identical to <see cref="CueDisplay"/> for
    /// every cue in the selected list (and therefore for a single-list project).</summary>
    internal string CueDisplayQualified(CueNodeViewModel cue) =>
        ForeignListNameOf(cue) is { } listName
            ? Strings.Format(nameof(Strings.CueListQualifiedNameFormat), listName, CueDisplay(cue))
            : CueDisplay(cue);

    private static CueGroupFireMode ParseGroupFireMode(CueNodeViewModel group) =>
        Enum.TryParse<CueGroupFireMode>(group.Extra, out var mode)
            ? mode
            : CueGroupFireMode.FirstCueOnly;

    /// <summary><c>Independent</c> steps fire each media cue in its OWN runtime transport group
    /// (<see cref="MediaCueIndependentExecutor"/>): overlap modes (Timeline, FireAllSimultaneously)
    /// need concurrent clips, and the authored shared group holds only one active clip - firing a
    /// later lane through it would REPLACE the earlier lane's still-playing clip.</summary>
    internal List<(CueNodeViewModel Cue, int DelayMs, bool Independent)> BuildTriggerPlan(CueNodeViewModel target)
    {
        var plan = new List<(CueNodeViewModel Cue, int DelayMs, bool Independent)>();
        if (target.Kind != CueNodeKind.Group)
        {
            plan.Add((target, Math.Max(0, target.PreWaitMs), false));
            AppendAutoContinueCues(plan, target);
            return plan;
        }

        var mode = ParseGroupFireMode(target);
        var children = target.Children.ToList();
        var groupPreWait = Math.Max(0, target.PreWaitMs);
        if (children.Count == 0)
            return plan;

        if (mode == CueGroupFireMode.FireAllSimultaneously)
        {
            foreach (var cue in EnumerateFireableCueOrder(children))
                plan.Add((cue, checked(groupPreWait + Math.Max(0, cue.PreWaitMs)), true));
            plan.Sort(static (a, b) => a.DelayMs.CompareTo(b.DelayMs));
            return plan;
        }

        if (mode == CueGroupFireMode.Timeline)
        {
            // Same delay-sorted plan as FireAllSimultaneously, with the authored lane start added on the
            // group's plan epoch. A nested group child keeps the sim-mode flattening (its fireable
            // descendants), all anchored at the child group's own lane start.
            foreach (var child in children)
            {
                if (child.Kind == CueNodeKind.Comment)
                    continue;
                var laneStart = checked(groupPreWait + Math.Max(0, child.TimelineStartMs));
                if (child.Kind == CueNodeKind.Group)
                {
                    var nestedBase = checked(laneStart + Math.Max(0, child.PreWaitMs));
                    foreach (var cue in EnumerateFireableCueOrder(child.Children))
                        plan.Add((cue, checked(nestedBase + Math.Max(0, cue.PreWaitMs)), true));
                }
                else
                {
                    plan.Add((child, checked(laneStart + Math.Max(0, child.PreWaitMs)), true));
                }
            }
            plan.Sort(static (a, b) => a.DelayMs.CompareTo(b.DelayMs));
            return plan;
        }

        if (mode is CueGroupFireMode.Playlist or CueGroupFireMode.ArmedList)
        {
            // GO on a group whose run already finished deliberately restarts a fresh run (the
            // Finished state only survives between "last pick fired" and its natural end so the
            // end-of-run behavior can trigger exactly once).
            if (_playlistRuns.TryGetValue(target.Id, out var finishedRun) && finishedRun.Finished)
                _playlistRuns.Remove(target.Id);

            var pick = PeekPlaylistPick(target);
            if (pick is null)
                return plan;
            if (pick.Kind == CueNodeKind.Group)
            {
                // A nested-group item fires through its own fire mode's plan (keeping each inner
                // step's own independence).
                foreach (var (cue, delayMs, independent) in BuildTriggerPlan(pick))
                    plan.Add((cue, checked(groupPreWait + delayMs), independent));
            }
            else
            {
                // No AppendAutoContinueCues here: a playlist item plays alone - the run itself is
                // the chain.
                plan.Add((pick, checked(groupPreWait + Math.Max(0, pick.PreWaitMs)), false));
            }

            return plan;
        }

        var first = EnumerateFireableCueOrder(children).FirstOrDefault();
        if (first is not null)
        {
            plan.Add((first, checked(groupPreWait + Math.Max(0, first.PreWaitMs)), false));
            AppendAutoContinueCues(plan, first);
        }
        return plan;
    }

    private void AppendAutoContinueCues(
        List<(CueNodeViewModel Cue, int DelayMs, bool Independent)> plan, CueNodeViewModel anchor)
    {
        // The chain runs inside the anchor's OWN list (the selected list for every visible cue), so a
        // cross-list fire carries its list's Auto-Continue chain instead of stopping at the first cue.
        var ordered = EnumerateFireableCueOrderFor(anchor).ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, anchor));
        if (idx < 0)
            return;

        var previous = anchor;
        for (var i = idx + 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (!SequentialTransitionUsesMode(previous, next, CueTriggerMode.AutoContinue))
                break;
            if (plan.Any(p => ReferenceEquals(p.Cue, next)))
            {
                previous = next;
                continue;
            }
            plan.Add((next, Math.Max(0, next.PreWaitMs), false));
            previous = next;
        }
    }

    // ----- Playlist / armed-list group runs (Ideas/CuePlayer-Enhancements.md §3) -----------------
    // Session-only state beside _lastRandomJumpTargetIds (the same "session state, not project
    // data" philosophy): loading a project, Stop and Panic all start every playlist afresh.

    private sealed class PlaylistRunState
    {
        /// <summary>This pass's item order (ids of direct non-comment children). For shuffle this IS
        /// the bag: a Fisher–Yates order drawn without replacement guarantees every child once per pass.</summary>
        public List<Guid> PassOrder = [];

        /// <summary>Index of the next item to fire within <see cref="PassOrder"/>.</summary>
        public int NextIndex;

        /// <summary>Items actually played per pass: the resolved PlayCount subset, else the child count.</summary>
        public int ItemsPerPass;

        /// <summary>1-based pass counter.</summary>
        public int Pass = 1;

        /// <summary>The item currently playing (the last consumed pick); guards the pass boundary
        /// no-repeat and routes natural-end events to this run.</summary>
        public Guid? CurrentItemId;

        // Display fields captured at consume time, BEFORE any pass rollover mutates the counters.
        public int CurrentItemOrdinal;
        public int CurrentItemPass;
        public int CurrentPassItemCount;

        /// <summary>The final pick of the final pass has fired; its natural end triggers the group's
        /// end behavior exactly once, after which the run is removed.</summary>
        public bool Finished;
    }

    private readonly Dictionary<Guid, PlaylistRunState> _playlistRuns = [];

    /// <summary>Shuffle RNG - injectable so tests can drive deterministic bags.</summary>
    internal Random PlaylistRandom { get; set; } = Random.Shared;

    /// <summary>Test/diagnostic accessor: whether a playlist run (armed or playing) exists for the group.</summary>
    internal bool HasActivePlaylistRun(Guid groupId) => _playlistRuns.ContainsKey(groupId);

    private bool HasFinishedPlaylistRun(Guid groupId) =>
        _playlistRuns.TryGetValue(groupId, out var run) && run.Finished;

    internal void ClearPlaylistRuns() => _playlistRuns.Clear();

    private bool IsPlaylistGroup(CueNodeViewModel? node) =>
        node is { Kind: CueNodeKind.Group }
        && ParseGroupFireMode(node) is CueGroupFireMode.Playlist or CueGroupFireMode.ArmedList;

    /// <summary>The group's playlist items: direct non-comment children, skipping nested groups with
    /// nothing fireable. Each item fires as a unit (a nested group runs through its own fire mode).</summary>
    private static List<CueNodeViewModel> PlaylistItems(CueNodeViewModel group) =>
        group.Children
            .Where(c => c.Kind != CueNodeKind.Comment
                        && (c.Kind != CueNodeKind.Group || EnumerateFireableCueOrder(c.Children).Any()))
            .ToList();

    /// <summary>The next pick of a playlist/armed-list group, creating (or repairing after tree
    /// edits) its session run on demand so standby pre-roll and GO always agree on the same item.
    /// Peeking commits the shuffle draw but consumes nothing - counters advance only when the pick
    /// fires. Null when the group has no items or its run just finished (transient window until the
    /// final item's natural end applies the end behavior).</summary>
    private CueNodeViewModel? PeekPlaylistPick(CueNodeViewModel group)
    {
        var items = PlaylistItems(group);
        if (items.Count == 0)
            return null;

        var run = GetPlaylistRun(group, items);
        if (run.Finished)
            return null;
        var pickId = run.PassOrder[run.NextIndex];
        return items.First(i => i.Id == pickId);
    }

    private PlaylistRunState GetPlaylistRun(CueNodeViewModel group, List<CueNodeViewModel> items)
    {
        if (_playlistRuns.TryGetValue(group.Id, out var run))
        {
            if (run.Finished)
                return run;
            // Repair after tree edits: the armed pick must reference a live child, else rebuild.
            if (run.NextIndex < run.PassOrder.Count
                && items.Any(i => i.Id == run.PassOrder[run.NextIndex]))
                return run;
            _playlistRuns.Remove(group.Id);
        }

        run = new PlaylistRunState();
        StartPlaylistPass(run, group, items);
        _playlistRuns[group.Id] = run;
        return run;
    }

    /// <summary>(Re)builds the pass order and per-pass counters. Reuses the previous shuffled order
    /// when ReshuffleEachPass is off; applies the AvoidImmediateRepeat pass-boundary guard.</summary>
    private void StartPlaylistPass(PlaylistRunState run, CueNodeViewModel group, List<CueNodeViewModel> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        var keepOrder = group.PlaylistShuffle
                        && !group.PlaylistReshuffleEachPass
                        && run.PassOrder.Count == ids.Count
                        && run.PassOrder.All(ids.Contains);
        if (!keepOrder)
        {
            run.PassOrder = [.. ids];
            if (group.PlaylistShuffle)
            {
                // Fisher–Yates: the whole pass is one bag drawn without replacement.
                for (var i = run.PassOrder.Count - 1; i > 0; i--)
                {
                    var j = PlaylistRandom.Next(i + 1);
                    (run.PassOrder[i], run.PassOrder[j]) = (run.PassOrder[j], run.PassOrder[i]);
                }
            }
        }

        // Pass-boundary guard: never open a pass with the item that just played when an
        // alternative exists (what "avoid immediate repeat" means across a reshuffle). Shuffle
        // only - a sequential playlist's order is authored, and the guard would scramble it
        // (with PlayCount=1 every pass replays child #1, which IS an immediate repeat by design).
        if (group.PlaylistShuffle
            && group.PlaylistAvoidImmediateRepeat
            && run.PassOrder.Count > 1
            && run.CurrentItemId is { } lastPlayed
            && run.PassOrder[0] == lastPlayed)
        {
            var swapWith = 1 + PlaylistRandom.Next(run.PassOrder.Count - 1);
            (run.PassOrder[0], run.PassOrder[swapWith]) = (run.PassOrder[swapWith], run.PassOrder[0]);
        }

        run.ItemsPerPass = Math.Clamp(
            group.PlaylistPlayCount ?? run.PassOrder.Count, 1, run.PassOrder.Count);
        run.NextIndex = 0;
    }

    /// <summary>Advances the run after its armed pick fired: bumps the counters, rolls the pass
    /// boundary (honoring LoopCount/PlayCount) and marks the run finished after the final pick.
    /// A pick that is itself a playlist/armed-list group is consumed recursively: its plan
    /// (<c>BuildTriggerPlan</c>'s nested branch) peeked the INNER run's armed pick, and without the
    /// inner consume that run's <see cref="PlaylistRunState.CurrentItemId"/> stays null - natural-end
    /// routing then swallows the item's end and neither run ever advances.</summary>
    private void ConsumePlaylistPick(CueNodeViewModel group)
    {
        var items = PlaylistItems(group);
        if (items.Count == 0 || GetPlaylistRun(group, items) is not { Finished: false } run)
            return;

        var pickId = run.PassOrder[run.NextIndex];
        run.CurrentItemId = pickId;
        run.CurrentItemOrdinal = run.NextIndex + 1;
        run.CurrentItemPass = run.Pass;
        run.CurrentPassItemCount = run.ItemsPerPass;
        run.NextIndex++;
        if (run.NextIndex >= run.ItemsPerPass)
        {
            var loops = Math.Max(0, group.PlaylistLoopCount);
            if (loops != 0 && run.Pass >= loops)
            {
                run.Finished = true;
            }
            else
            {
                run.Pass++;
                StartPlaylistPass(run, group, items);
            }
        }

        RefreshPlaylistNowPlayingStatus(group.Id);

        if (items.FirstOrDefault(i => i.Id == pickId) is { } pick && IsPlaylistGroup(pick))
            ConsumePlaylistPick(pick);
    }

    /// <summary>First fireable cue after the whole group (skipping all its descendants).</summary>
    private static CueNodeViewModel? NextCueAfterGroup(
        CueNodeViewModel group, IReadOnlyList<CueNodeViewModel> ordered)
    {
        var lastDescendant = EnumerateFireableCueOrder(group.Children).LastOrDefault();
        return lastDescendant is null ? null : NextCueAfter(lastDescendant, ordered);
    }

    /// <summary>Routes a natural-end event to the playlist run that owns it, if any. Innermost
    /// playlist group wins. Returns null (default sequential logic applies) when a nested-group
    /// item is still running its own internal Auto-Follow chain.</summary>
    private (CueNodeViewModel Group, PlaylistRunState Run, bool IsCurrentItem)? FindPlaylistRunForEndedCue(
        CueNodeViewModel ended)
    {
        if (_playlistRuns.Count == 0)
            return null;

        var path = FindContainingGroupPath(ended);
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var group = path[i];
            if (!IsPlaylistGroup(group) || !_playlistRuns.TryGetValue(group.Id, out var run))
                continue;

            var item = i + 1 < path.Count ? path[i + 1] : ended;
            if (item.Kind == CueNodeKind.Group)
            {
                // The item's own Auto-Follow chain continues inside it - not the item's end yet.
                var ordered = EnumerateFireableCueOrderFor(ended).ToList();
                var idx = ordered.FindIndex(c => ReferenceEquals(c, ended));
                if (idx >= 0 && idx + 1 < ordered.Count)
                {
                    var next = ordered[idx + 1];
                    if (FindContainingGroupPath(next).Any(g => ReferenceEquals(g, item))
                        && SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
                        return null;
                }
            }

            return (group, run, run.CurrentItemId == item.Id);
        }

        return null;
    }

    /// <param name="foreign">The run lives in a cue list OTHER than the selected one (cross-list merged
    /// session): its advance plays into the same session but must not move the visible transport, so every
    /// Standby/Current write below is skipped and the advance rides the headless fire path.</param>
    private async Task HandlePlaylistItemEndedAsync(
        CueNodeViewModel group, PlaylistRunState run, CueNodeViewModel ended, bool isCurrentItem,
        bool foreign = false)
    {
        // A skipped/overlapped item finishing late must not double-advance the run - swallow it
        // (falling through to default Auto-Follow would defeat the group semantics too).
        if (!isCurrentItem)
            return;

        // Armed list: GO advances, a natural end does not - and it must not fall through to the
        // default next-sibling Auto-Follow either.
        if (ParseGroupFireMode(group) == CueGroupFireMode.ArmedList)
            return;

        if (!run.Finished)
        {
            // Auto-advance: fire the next pick through the normal GO machinery (GoCore's playlist
            // branch consumes the pick and keeps standby on the group).
            if (foreign)
            {
                await GoForeignListAsync(group);
                return;
            }

            StandbyCueNode = group;
            _immediateJumpChain.Clear();
            await GoCore(group);
            return;
        }

        // Run complete - the run state is over either way.
        _playlistRuns.Remove(group.Id);
        RefreshPlaylistNowPlayingStatus(group.Id);

        // A nested playlist completing while an ENCLOSING run plays it as its current item is that
        // outer item's natural end: route completion to the outer run (its semantics take precedence
        // over the inner group's own end behavior, the same rule that trumps per-cue end-jumps).
        // Without this the outer run stalls forever on its nested-group item.
        foreach (var outer in FindContainingGroupPath(group).Reverse())
        {
            if (IsPlaylistGroup(outer)
                && _playlistRuns.TryGetValue(outer.Id, out var outerRun)
                && outerRun.CurrentItemId == group.Id)
            {
                await HandlePlaylistItemEndedAsync(outer, outerRun, ended, isCurrentItem: true, foreign);
                return;
            }
        }

        // No enclosing run owns this group - apply its own configured end behavior.
        switch (group.PlaylistEndBehavior)
        {
            case CuePlaylistEndBehavior.AdvancePastGroup:
            {
                var ordered = EnumerateFireableCueOrderFor(group).ToList();
                var next = NextCueAfterGroup(group, ordered);
                if (next is null)
                {
                    if (!foreign)
                        CurrentCueNode = null;
                    return;
                }

                if (!foreign)
                    StandbyCueNode = next;
                if (SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
                {
                    StatusMessage = Strings.Format(
                        nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(next));
                    if (foreign)
                    {
                        await GoForeignListAsync(next);
                        return;
                    }

                    _immediateJumpChain.Clear();
                    await GoCore();
                }

                return;
            }
            case CuePlaylistEndBehavior.Hold:
                // Leave the transport exactly where it is (held/freeze-frame clips keep showing).
                return;
            case CuePlaylistEndBehavior.Stop:
            default:
                if (!foreign)
                {
                    CurrentCueNode = null;
                    StandbyCueNode = group; // a fresh GO restarts the playlist
                }

                StatusMessage = Strings.Format(
                    nameof(Strings.CuePlaylistFinishedStatusFormat), CueDisplayQualified(group));
                return;
        }
    }

    /// <summary>Now-Playing aggregate status for a playlist group row: "item i/N · pass p/M"
    /// (or "… · pass p" for an infinite run). Null when the group has no live run item.</summary>
    internal string? BuildPlaylistStatus(CueNodeViewModel group)
    {
        if (!IsPlaylistGroup(group)
            || !_playlistRuns.TryGetValue(group.Id, out var run)
            || run.CurrentItemId is null)
            return null;

        var loops = Math.Max(0, group.PlaylistLoopCount);
        return loops == 0
            ? Strings.Format(
                nameof(Strings.PlaylistStatusInfiniteFormat),
                run.CurrentItemOrdinal, run.CurrentPassItemCount, run.CurrentItemPass)
            : Strings.Format(
                nameof(Strings.PlaylistStatusFormat),
                run.CurrentItemOrdinal, run.CurrentPassItemCount, run.CurrentItemPass, loops);
    }

    private void RefreshPlaylistNowPlayingStatus(Guid groupId)
    {
        var row = NowPlayingRows.OfType<ActiveGroupViewModel>().FirstOrDefault(g => g.GroupId == groupId);
        if (row is not null)
            row.PlaylistStatus = BuildPlaylistStatus(row.GroupNode);
    }

    /// <summary>Called when the active player finishes a file naturally during cue-driven playback.</summary>
    public Task OnMediaCueNaturallyEndedAsync() =>
        CurrentCueNode is { Kind: CueNodeKind.Media } current
            ? OnMediaCueNaturallyEndedAsync(current.Id)
            : Task.CompletedTask;

    public async Task OnMediaCueNaturallyEndedAsync(Guid endedCueId)
    {
        // Cross-list merged session: the ended clip may belong to any loaded list. Its chain (playlist
        // advance / end target / Auto-Follow) is resolved inside that list; only the SELECTED list's
        // chain is allowed to move the visible Standby/Current pointers, everything else advances
        // headlessly into the same session.
        if (FindNodeById(endedCueId) is not { Kind: CueNodeKind.Media } ended)
            return;
        var foreign = IsForeignListNode(ended);

        // Playlist runs own their children's end events (before per-cue EndTarget: the group's run
        // semantics take precedence over a child's authored end-jump while the run is active).
        if (FindPlaylistRunForEndedCue(ended) is { } playlistHit)
        {
            await HandlePlaylistItemEndedAsync(
                playlistHit.Group, playlistHit.Run, ended, playlistHit.IsCurrentItem, foreign);
            return;
        }

        // End target ("then fire cue #"): an explicit on-end jump wins over the default
        // next-cue-Auto-Follow chain - "after this song, go anywhere".
        if (ended.EndTargetCueId is { } targetId)
        {
            var endTarget = EnumerateAllCueNodesFor(ended).FirstOrDefault(c => c.Id == targetId);
            if (endTarget is null || ReferenceEquals(endTarget, ended) || endTarget.Kind == CueNodeKind.Comment)
            {
                // An authored end target is an override, not a best-effort hint. If its stable link became
                // invalid after deletion/import, stop here and surface it instead of unexpectedly firing the
                // ordinary next Auto-Follow cue.
                StatusMessage = Strings.CueEndTargetUnavailable;
                return;
            }

            StatusMessage = Strings.Format(
                nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(endTarget));
            if (foreign)
            {
                await GoForeignListAsync(endTarget);
                return;
            }

            StandbyCueNode = endTarget;
            _immediateJumpChain.Clear();
            await GoCore();
            return;
        }

        var ordered = EnumerateFireableCueOrderFor(ended).ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, ended));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;

        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
            return;

        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(next));
        if (foreign)
        {
            await GoForeignListAsync(next);
            return;
        }

        StandbyCueNode = next;
        _immediateJumpChain.Clear();
        await GoCore();
    }

    /// <summary>Called when a playing media cue enters its playlist group's crossfade window (the
    /// session's <c>ClipApproachingEnd</c>, routed by the coordinator): fires the run's NEXT pick early,
    /// with the group's <see cref="CueNodeViewModel.PlaylistCrossfadeMs"/> as the dual-voice overlap, so
    /// the outgoing item fades out under the incoming one. Advancing here moves the run's current item,
    /// and the outgoing clip then never raises a natural end as the ACTIVE clip (it releases as the
    /// crossfade tail) - the natural-end handler's not-current-item guard swallows any straggler.
    /// Everything else is a no-op and keeps the butt-splice natural-end path: armed lists (GO-only
    /// advance), finished runs (the final item's natural end applies the end behavior), non-playlist
    /// cues, a zero window, and hosts without the crossfade seam.</summary>
    public async Task OnMediaCueApproachingEndAsync(Guid endingCueId)
    {
        if (MediaCueCrossfadeExecutor is null)
            return; // no dual-voice seam - advancing early would CUT the current item, not crossfade it
        if (FindNodeById(endingCueId) is not { Kind: CueNodeKind.Media } ending)
            return;
        if (FindPlaylistRunForEndedCue(ending) is not { } hit
            || !hit.IsCurrentItem
            || hit.Run.Finished
            || ParseGroupFireMode(hit.Group) != CueGroupFireMode.Playlist)
            return;
        var crossfadeMs = hit.Group.PlaylistCrossfadeMs;
        if (crossfadeMs <= 0)
            return;

        // The same advance as HandlePlaylistItemEndedAsync's auto-advance (GoCore consumes the pick and
        // keeps standby on the group), except the fire carries the overlap window. EqualPower is the
        // crossfade's law by construction: complementary up/down legs sum to constant power, which is
        // what an overlapping music transition should do (a linear pair dips audibly at the midpoint).
        var window = (TimeSpan.FromMilliseconds(crossfadeMs), S.Media.Session.FadeCurve.EqualPower);
        if (IsForeignListNode(ending))
        {
            // Another list's playlist crossfades in the same session - without touching this list's standby.
            await GoForeignListAsync(hit.Group, window);
            return;
        }

        StandbyCueNode = hit.Group;
        _immediateJumpChain.Clear();
        await GoCore(hit.Group, window);
    }

    public void RefreshBrokenEndpointFlags()
    {
        var ids = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var broken = 0;
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
            {
                node.IsEndpointBroken = false;
                continue;
            }

            node.IsEndpointBroken = Guid.TryParse(node.EndpointIdText, out var endpointId)
                                    && !ids.Contains(endpointId);
            if (node.IsEndpointBroken)
                broken++;
        }

        if (broken > 0)
            StatusMessage = Strings.Format(nameof(Strings.CueBrokenEndpointCountStatusFormat), broken);
    }

    /// <summary>Distinct missing endpoint IDs referenced by action cues.</summary>
    public IReadOnlyList<(Guid MissingId, int CueCount, CueActionKind Kind)> GetBrokenEndpointGroups()
    {
        var liveIds = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var groups = new Dictionary<Guid, (int Count, CueActionKind Kind)>();
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId) || liveIds.Contains(missingId))
                continue;
            var kind = Enum.TryParse<CueActionKind>(node.Extra, out var k) ? k : CueActionKind.OSCOut;
            if (groups.TryGetValue(missingId, out var g))
                groups[missingId] = (g.Count + 1, g.Kind);
            else
                groups[missingId] = (1, kind);
        }

        return groups.Select(kv => (kv.Key, kv.Value.Count, kv.Value.Kind)).ToList();
    }

    public void RemapActionEndpoints(IReadOnlyDictionary<Guid, Guid> missingToReplacement)
    {
        if (missingToReplacement.Count == 0)
            return;

        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId))
                continue;
            if (!missingToReplacement.TryGetValue(missingId, out var replacement))
                continue;
            node.EndpointIdText = replacement.ToString();
        }

        RefreshBrokenEndpointFlags();
    }

    public IReadOnlyList<(Guid CueId, PortAudioInputPlaylistItem Item)> GetPortAudioPreConnectTargets()
    {
        var simultaneousGroup = GetStandbySimultaneousGroupTargets();
        if (simultaneousGroup.Count > 0)
        {
            var groupTargets = new List<(Guid, PortAudioInputPlaylistItem)>();
            foreach (var cue in simultaneousGroup)
            {
                if (cue.Kind != CueNodeKind.Media
                    || cue.MediaSourceItem is not PortAudioInputPlaylistItem pa
                    || !pa.SupportsPreRoll())
                    continue;
                groupTargets.Add((cue.Id, pa));
            }

            return groupTargets;
        }

        var targets = new List<(Guid, PortAudioInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (cue.Kind != CueNodeKind.Media
                || cue.MediaSourceItem is not PortAudioInputPlaylistItem pa
                || !pa.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, pa));
        }

        return targets;
    }

    // Folder drops intentionally cover audio/video containers only. Still images and subtitle documents have
    // distinct cue types with different hold/render semantics, so silently wrapping those in FilePlaylistItem
    // would create a cue that behaves differently from the corresponding + Image / + Subtitle command.
    private static readonly HashSet<string> FolderDropMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video / A-V containers
        ".3g2", ".3gp", ".asf", ".avi", ".flv", ".m2ts", ".m2v", ".m4v", ".mkv", ".mov",
        ".mp4", ".mpe", ".mpeg", ".mpg", ".mts", ".ogv", ".ts", ".vob", ".webm", ".wmv",
        // Audio containers
        ".aac", ".ac3", ".aif", ".aiff", ".ape", ".caf", ".dts", ".flac", ".m4a", ".mka",
        ".mp2", ".mp3", ".oga", ".ogg", ".opus", ".wav", ".wma",
    };

    public Task AddMediaFilesFromDrop(IEnumerable<string> paths) =>
        AddMediaFilesFromDrop(paths, dropTarget: null);

    public Task AddMediaFilesFromDrop(IEnumerable<string> paths, CueNodeViewModel? dropTarget)
    {
        if (SelectedCueList is null)
            return Task.CompletedTask;

        // Honor the drop location (QoL): dropping onto a group appends inside it, onto any other
        // row inserts right after that row; empty space keeps the old append-to-selection-parent.
        IList<CueNodeViewModel> parent;
        var insertAt = -1; // -1 = append
        if (dropTarget is { IsGroup: true })
        {
            parent = dropTarget.Children;
            dropTarget.IsExpanded = true;
        }
        else if (dropTarget is not null
                 && FindParentCollection(SelectedCueList.Nodes, dropTarget) is IList<CueNodeViewModel> targetParent)
        {
            parent = targetParent;
            insertAt = targetParent.IndexOf(dropTarget) + 1;
        }
        else
        {
            parent = SelectedParentCollection() as IList<CueNodeViewModel> ?? SelectedCueList.Nodes;
        }

        void Place(CueNodeViewModel node)
        {
            if (insertAt < 0 || insertAt > parent.Count)
                parent.Add(node);
            else
                parent.Insert(insertAt++, node);
        }

        var mediaAdded = 0;
        var groupsAdded = 0;
        var probes = new List<(CueNodeViewModel Row, string Path)>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (File.Exists(path))
            {
                var row = AddDroppedMediaCue(parent, path, probes, add: false);
                Place(row);
                FinalizeAddedCue(row);
                mediaAdded++;
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            var group = new CueNodeViewModel(CueNodeKind.Group)
            {
                Number = NextNumber(parent),
                Label = DroppedDirectoryLabel(path),
                Extra = CueGroupFireMode.FirstCueOnly.ToString(),
            };
            Place(group);
            FinalizeAddedCue(group);
            groupsAdded++;

            foreach (var mediaPath in EnumerateDroppedDirectoryMedia(path))
            {
                AddDroppedMediaCue(group.Children, mediaPath, probes);
                mediaAdded++;
            }
        }

        if (groupsAdded > 0)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueFoldersAddedFromDropStatusFormat), groupsAdded, mediaAdded);
        }
        else if (mediaAdded > 0)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueAddedFromDropStatusFormat), mediaAdded);
        }

        // A large folder must not open every decoder at once. Probe sequentially in one detached async flow;
        // cue creation is immediate, while duration/stream metadata fills in progressively.
        return probes.Count > 0
            ? ProbeDroppedMediaAsync(probes)
            : Task.CompletedTask;
    }

    /// <summary>Builds (and by default appends + finalizes) a media cue for a dropped file. With
    /// <paramref name="add"/> false the caller owns placement (positioned drops): it must insert
    /// the returned row into <paramref name="parent"/> and then call <c>FinalizeAddedCue</c>,
    /// so the auto-renumber pass sees the row at its real position.</summary>
    private CueNodeViewModel AddDroppedMediaCue(
        ICollection<CueNodeViewModel> parent,
        string path,
        ICollection<(CueNodeViewModel Row, string Path)> probes,
        bool add = true)
    {
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Path.GetFileNameWithoutExtension(path),
            MediaSourceItem = new FilePlaylistItem(path),
            SourceOrAction = path,
        };
        if (add)
        {
            parent.Add(row);
            FinalizeAddedCue(row);
        }
        probes.Add((row, path));
        return row;
    }

    private static IReadOnlyList<string> EnumerateDroppedDirectoryMedia(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => FolderDropMediaExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string DroppedDirectoryLabel(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return string.IsNullOrWhiteSpace(name) ? directory : name;
    }

    private static async Task ProbeDroppedMediaAsync(
        IReadOnlyList<(CueNodeViewModel Row, string Path)> probes)
    {
        foreach (var (row, path) in probes)
        {
            try
            {
                await ProbeAndAssignDurationAsync(row, path).ConfigureAwait(false);
            }
            catch
            {
                // Metadata probing is best-effort. The cue remains usable and reports the open error on fire.
            }
        }
    }

    private IEnumerable<CueNodeViewModel> EnumerateAllCueNodes()
    {
        if (SelectedCueList is null)
            yield break;
        foreach (var node in EnumerateAllCueNodes(SelectedCueList.Nodes))
            yield return node;
    }

    /// <summary>Every cue node in EVERY loaded list, selected list first. The cross-list merged session
    /// (workstream A) maps all lists into the one <c>ShowSession</c>, so schedules, triggers and the
    /// remote API resolve their targets here; the visible transport (GO / standby / tree) keeps using the
    /// selected-list <see cref="EnumerateAllCueNodes()"/>.</summary>
    private IEnumerable<CueNodeViewModel> EnumerateAllCueNodesAcrossLists()
    {
        if (SelectedCueList is { } selected)
            foreach (var node in EnumerateAllCueNodes(selected.Nodes))
                yield return node;
        foreach (var list in CueLists)
        {
            if (ReferenceEquals(list, SelectedCueList))
                continue;
            foreach (var node in EnumerateAllCueNodes(list.Nodes))
                yield return node;
        }
    }

    /// <summary>The loaded list that owns <paramref name="node"/> - the SELECTED list first (the common
    /// case, and the only list a single-list project has), then the others so a cross-list schedule /
    /// trigger / remote fire resolves against its OWN list's tree. Null when the node is not (or no
    /// longer) in any loaded list.</summary>
    internal CueListEditorViewModel? FindOwningCueList(CueNodeViewModel node)
    {
        if (SelectedCueList is { } selected && ContainsNode(selected.Nodes, node))
            return selected;
        foreach (var list in CueLists)
            if (!ReferenceEquals(list, SelectedCueList) && ContainsNode(list.Nodes, node))
                return list;
        return null;
    }

    /// <summary>True when <paramref name="node"/> lives in a loaded list OTHER than the selected one -
    /// a fire on it must play into the merged session WITHOUT moving the visible transport.</summary>
    private bool IsForeignListNode(CueNodeViewModel node) =>
        SelectedCueList is null || !ContainsNode(SelectedCueList.Nodes, node);

    /// <summary>The fireable cue order of the list that OWNS <paramref name="node"/> (the selected list's
    /// order for a visible cue) - the ordering auto-follow / auto-continue chains walk.</summary>
    private IEnumerable<CueNodeViewModel> EnumerateFireableCueOrderFor(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } list ? EnumerateFireableCueOrder(list.Nodes) : [];

    /// <summary>Every node (groups included) of the list that OWNS <paramref name="node"/> - the scope an
    /// authored link like a cue's end target resolves in.</summary>
    private IEnumerable<CueNodeViewModel> EnumerateAllCueNodesFor(CueNodeViewModel node) =>
        FindOwningCueList(node) is { } list ? EnumerateAllCueNodes(list.Nodes) : [];

    /// <summary>Cues carrying schedule configuration (enabled or retained-while-disabled) - the
    /// <c>CueSchedulerService</c> sweep. Scoped to ALL loaded lists since the cross-list merged session:
    /// every list's cues live in the one <c>ShowSession</c>, so a schedule in a non-selected list fires
    /// into it headlessly instead of being a silent no-show.</summary>
    internal IEnumerable<CueNodeViewModel> EnumerateScheduledCueNodes() =>
        EnumerateAllCueNodesAcrossLists().Where(node => node.HasSchedule);

    /// <summary>Cues carrying MIDI/OSC/hotkey trigger bindings - the <c>CueTriggerService</c> match
    /// sweep. Same all-lists scoping (and reasoning) as <see cref="EnumerateScheduledCueNodes"/>.</summary>
    internal IEnumerable<CueNodeViewModel> EnumerateTriggeredCueNodes() =>
        EnumerateAllCueNodesAcrossLists().Where(node => node.HasTriggers);

    private static IEnumerable<CueNodeViewModel> EnumerateAllCueNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateAllCueNodes(node.Children))
                yield return child;
        }
    }

    /// <param name="trackCurrentCue">False for a headless cross-list run: the plan executes normally but
    /// leaves the VISIBLE transport's playing pointer alone (that pointer describes the selected list).</param>
    /// <param name="isPaused">The run's own pause state, which its pre-waits follow. Null = the VISIBLE
    /// transport's <see cref="IsTransportPaused"/> (the operator GO path).</param>
    private async Task RunTriggerPlanAsync(
        IReadOnlyList<(CueNodeViewModel Cue, int DelayMs, bool Independent)> plan, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null,
        bool trackCurrentCue = true,
        Func<bool>? isPaused = null)
    {
        var startedAt = DateTime.UtcNow;

        // Group steps that share the same delay for coordinated start.
        var groups = plan.GroupBy(s => s.DelayMs).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            await WaitUntilDelayAsync(
                startedAt, group.Key, ct, countdownCues: group.Select(s => s.Cue).ToList(),
                isPaused: isPaused);
            ct.ThrowIfCancellationRequested();

            var steps = group.ToList();
            // Only the playing pointer follows fired steps. The editor selection is operator-owned and
            // transport never changes it; current/standby row dots show the live playhead instead.
            if (trackCurrentCue)
                foreach (var step in steps)
                    CurrentCueNode = step.Cue;

            if (steps.Count > 1 && MediaCueGroupExecutor is not null)
            {
                // Coordinated group: open all decoders in parallel, start in sync.
                DispatchCueGroupExecution(steps.Select(s => s.Cue).ToList(), ct);
            }
            else
            {
                foreach (var step in steps)
                {
                    // Overlap-mode media steps (Timeline lanes, staggered sim-mode pre-waits) get
                    // their own runtime transport group: the shared authored group holds ONE active
                    // clip, so this fire would otherwise cut the earlier lane's clip mid-play.
                    if (step.Independent && step.Cue.Kind == CueNodeKind.Media)
                    {
                        DispatchIndependentCueExecution(step.Cue, ct);
                    }
                    else
                    {
                        DispatchCueExecution(step.Cue, ct, advanceCrossfade);
                        if (step.Cue.Kind == CueNodeKind.Media)
                            advanceCrossfade = null; // the window belongs to the FIRST media fire only
                    }
                }
            }
        }
    }

    /// <summary>Dispatches one media cue into its OWN runtime transport group (the
    /// <see cref="MediaCueIndependentExecutor"/> host path, same machinery as same-delay batches and
    /// operator overlap-fires). Falls back to the shared-group dispatch when no independent executor
    /// is configured (headless tests / degraded host).</summary>
    private void DispatchIndependentCueExecution(CueNodeViewModel cue, CancellationToken ct)
    {
        if (MediaCueIndependentExecutor is not { } executor || cue.ToModel() is not MediaCueNode media)
        {
            DispatchCueExecution(cue, ct);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await executor(media, ct).ConfigureAwait(false);
                await ApplyCueExecutionResultOnUiAsync(cue, result, MediaExecutionConfigured).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
            }
        }, ct);
    }

    private void DispatchCueExecution(
        CueNodeViewModel cue, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var exec = await ExecuteCueAsync(cue, ct, advanceCrossfade).ConfigureAwait(false);
                await ApplyCueExecutionResultOnUiAsync(cue, exec, MediaExecutionConfigured).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
            }
        }, ct);
    }

    private void DispatchCueGroupExecution(IReadOnlyList<CueNodeViewModel> cues, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var mediaCues = cues
                    .Where(c => c.Kind == CueNodeKind.Media)
                    .Select(c => c.ToModel())
                    .OfType<MediaCueNode>()
                    .ToList();

                if (mediaCues.Count > 0 && MediaCueGroupExecutor is not null)
                {
                    var result = await MediaCueGroupExecutor(mediaCues, ct).ConfigureAwait(false);
                    await SetStatusMessageOnUiAsync(string.IsNullOrWhiteSpace(result)
                        ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), $"{mediaCues.Count} cues")
                        : result);
                }

                // Non-media cues in the group still dispatch individually.
                foreach (var cue in cues.Where(c => c.Kind != CueNodeKind.Media))
                {
                    try
                    {
                        var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(exec))
                            await ApplyCueExecutionResultOnUiAsync(cue, exec, mediaExecutionConfigured: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await SetStatusMessageOnUiAsync(ex.Message);
            }
        }, ct);
    }

    private Task ApplyCueExecutionResultOnUiAsync(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured)).GetTask();
    }

    private Task ApplyCueExecutionFailureOnUiAsync(CueNodeViewModel cue, string detail)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionFailure(cue, detail);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionFailure(cue, detail)).GetTask();
    }

    private void ApplyCueExecutionResult(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (cue.Kind == CueNodeKind.Media
            && mediaExecutionConfigured
            && !_activeCueIds.Contains(cue.Id))
        {
            ApplyCueExecutionFailure(cue, detail);
            return;
        }

        // Qualified: this runs for cross-list fires too, and it lands AFTER GoForeignListAsync's own
        // qualified GO line, so an unprefixed label here overwrote the qualifier milliseconds later.
        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplayQualified(cue))
            : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplayQualified(cue), detail);

        // Visualizer rows in Now Playing: a fired Start cue appears with a ticking position (count-up
        // when indefinite, progress toward the set duration otherwise); a Stop cue (or the row's X, or
        // the duration elapsing) ends the row. The projectM LAYER itself keeps rendering unless stopped.
        if (cue.Kind == CueNodeKind.Visualizer && string.IsNullOrWhiteSpace(detail))
        {
            if (string.Equals(cue.Extra, "Stop", StringComparison.OrdinalIgnoreCase))
                EndVisualizerRows(VisualizerTargetComposition(cue));
            else
                StartVisualizerRow(cue);
        }

        // Runtime chaining for INSTANT cues (visualizer/action/fade - jumps redirect themselves): the
        // media end-machinery never fires for them, so a following Auto-Follow cue would otherwise never
        // start. An infinite visualizer (or an action, or a fade - its ramp runs in the background) is
        // non-blocking → the chain advances NOW; a visualizer WITH a duration occupies the timeline like
        // an image slide → advance after it.
        if (string.IsNullOrWhiteSpace(detail)
            && cue.Kind is CueNodeKind.Visualizer or CueNodeKind.Action or CueNodeKind.Fade)
        {
            var delayMs = cue.Kind == CueNodeKind.Visualizer ? Math.Max(0, cue.VisualizerDurationMs) : 0;
            _ = AdvanceAutoFollowAfterInstantCueAsync(cue, delayMs);
        }
    }

    /// <summary>Row-X cancel: a running VISUALIZER row stops the projectM layer (the generic session
    /// stop is a no-op for it - visualizers are not session clips); everything else goes to the host's
    /// cancel callback as before.</summary>
    private async Task CancelActiveCueAsync(Guid cueId)
    {
        if (_runningVisualizers.ContainsKey(cueId))
        {
            await StopVisualizerAsync(cueId);
            return;
        }

        await (CancelCueCallback?.Invoke(cueId) ?? Task.CompletedTask);
    }

    /// <summary>Stops one running visualizer: detaches its layer (synthetic Stop through the executor)
    /// and retires its Now-Playing row.</summary>
    private async Task StopVisualizerAsync(Guid vizCueId)
    {
        if (!_runningVisualizers.TryGetValue(vizCueId, out var info))
            return;
        if (VisualizerCueExecutor is not null)
        {
            try
            {
                await VisualizerCueExecutor(
                    new VisualizerCueNode { CompositionId = info.Composition, StartVisualizer = false },
                    CancellationToken.None);
            }
            catch
            {
                // best effort - the row still retires below
            }
        }

        EndVisualizerRows(info.Composition);
    }

    /// <summary>Panic/Stop: every running visualizer layer detaches and its row retires.</summary>
    public void StopAllVisualizers()
    {
        foreach (var id in _runningVisualizers.Keys.ToList())
            _ = StopVisualizerAsync(id);
    }

    /// <summary>A host rebuild removed one composition visualizer without executing its Stop cue.</summary>
    internal void OnVisualizerLayerCleared(Guid compositionId) => EndVisualizerRows(compositionId);

    /// <summary>The host removed every composition visualizer without executing individual Stop cues.</summary>
    internal void OnVisualizerLayersCleared()
    {
        foreach (var compositionId in _runningVisualizers.Values.Select(info => info.Composition).Distinct().ToList())
            EndVisualizerRows(compositionId);
    }

    // --- Visualizer Now-Playing rows (#29 first slice) --------------------------------------------
    // _runningVisualizers tracks the actual persistent layer lifetime. A finite cue duration only retires
    // its Now-Playing timeline row; it does not stop projectM, so keep that distinction explicit or Panic and
    // later live placement edits would lose the still-running layer after the row timed out.
    private readonly Dictionary<Guid, (Guid Composition, long StartedTicks, int DurationMs)> _runningVisualizers = new();
    private readonly HashSet<Guid> _visibleVisualizerRows = new();
    private Avalonia.Threading.DispatcherTimer? _visualizerRowTimer;

    private static Guid VisualizerTargetComposition(CueNodeViewModel viz) =>
        viz.VideoPlacements.FirstOrDefault()?.CompositionId ?? viz.VisualizerCompositionId;

    internal void StartVisualizerRow(CueNodeViewModel viz)
    {
        // Re-firing replaces the layer on that composition - end any prior rows for it first.
        EndVisualizerRows(VisualizerTargetComposition(viz));
        _runningVisualizers[viz.Id] =
            (VisualizerTargetComposition(viz), System.Diagnostics.Stopwatch.GetTimestamp(), Math.Max(0, viz.VisualizerDurationMs));
        _visibleVisualizerRows.Add(viz.Id);
        OnCueStarted(viz.Id);
        _visualizerRowTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, OnVisualizerRowTick);
        _visualizerRowTimer.Start();
    }

    private void EndVisualizerRows(Guid compositionId)
    {
        foreach (var (id, info) in _runningVisualizers.Where(kv => kv.Value.Composition == compositionId).ToList())
        {
            _runningVisualizers.Remove(id);
            if (_visibleVisualizerRows.Remove(id))
                OnCueEnded(id);
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    private void OnVisualizerRowTick(object? sender, EventArgs e)
    {
        foreach (var id in _visibleVisualizerRows.ToList())
        {
            if (!_runningVisualizers.TryGetValue(id, out var info))
            {
                _visibleVisualizerRows.Remove(id);
                continue;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(info.StartedTicks);
            if (info.DurationMs > 0 && elapsed.TotalMilliseconds >= info.DurationMs)
            {
                // Timeline slot done (the layer keeps rendering; only the row retires).
                _visibleVisualizerRows.Remove(id);
                OnCueEnded(id);
                continue;
            }

            OnCueProgress(new CuePlaybackProgress(
                id, elapsed, TimeSpan.FromMilliseconds(info.DurationMs)));
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    /// <summary>Fires the next Auto-Follow cue after an instant cue (optionally after its timeline
    /// duration). Cancels itself when the operator moved on (another GO changed the standby).
    /// <para>Cross-list merged session: the chain is resolved and continued inside the list that OWNS
    /// <paramref name="fired"/>, mirroring the media natural-end path
    /// (<see cref="OnMediaCueNaturallyEndedAsync(Guid)"/>). Resolving against the SELECTED list found no
    /// index for a foreign Action/Visualizer/Fade cue, so its Auto-Follow chain silently stopped dead
    /// after the first cue; continuing through <c>GoCore</c> would have been worse still - it would have
    /// armed the VISIBLE standby with another list's cue.</para></summary>
    private async Task AdvanceAutoFollowAfterInstantCueAsync(CueNodeViewModel fired, int delayMs)
    {
        var foreign = IsForeignListNode(fired);
        var ordered = EnumerateFireableCueOrderFor(fired).ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, fired));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;
        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(fired, next, CueTriggerMode.AutoFollow))
            return;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
            // Stale? The operator fired something else while the visualizer slide ran. Only meaningful
            // for the visible transport - CurrentCueNode describes the SELECTED list, and a headless
            // cross-list run is superseded by its own list's next fire (which cancels its run scope).
            if (!foreign && !ReferenceEquals(CurrentCueNode, fired) && CurrentCueNode is not null)
                return;
        }

        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(next));
        if (foreign)
        {
            await GoForeignListAsync(next);
            return;
        }

        StandbyCueNode = next;
        _immediateJumpChain.Clear();
        await GoCore();
    }

    private void ApplyCueExecutionFailure(CueNodeViewModel cue, string? detail)
    {
        if (ReferenceEquals(CurrentCueNode, cue))
            CurrentCueNode = null;

        // Parking the failed cue in standby is the OPERATOR's affordance ("it didn't go - press GO to
        // retry"), and it only makes sense for a cue the operator can see. A failed cross-list fire
        // must not reach it: writing a foreign node into StandbyCueNode dropped the visible list's own
        // standby (RefreshRowStatuses walks the SELECTED list only, so its dot simply vanished) and
        // armed the next GO to fire that foreign cue through the VISIBLE transport - the one thing
        // cross-list firing is defined not to do. Un-pausing the visible transport on another list's
        // failure is the same category of mistake.
        if (!IsForeignListNode(cue))
        {
            StandbyCueNode = cue;
            IsTransportPaused = false;
        }

        // Qualified like every other cross-list status line: an unprefixed "1 Station ID" reads as the
        // visible list's cue 1.
        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueExecutionFailedStatusFormat), CueDisplayQualified(cue))
            : Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplayQualified(cue), detail);
    }

    private async Task SetStatusMessageOnUiAsync(string? message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = message;
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = message);
    }

    private async Task<string?> ExecuteCueAsync(
        CueNodeViewModel cue, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        switch (cue.Kind)
        {
            case CueNodeKind.Media:
                if (MediaCueExecutor is null)
                    return Strings.CueMediaExecutionNotConfigured;
                if (cue.ToModel() is not MediaCueNode media)
                    return Strings.CueInvalidMediaCue;
                // A playlist-crossfade advance fires through the dual-voice seam; without one (a host
                // that cleared it mid-run) the plain executor is the butt-splice safety net.
                return advanceCrossfade is { } crossfade && MediaCueCrossfadeExecutor is { } crossfadeExecutor
                    ? await crossfadeExecutor(media, crossfade.Duration, crossfade.Curve, ct)
                    : await MediaCueExecutor(media, ct);
            case CueNodeKind.Action:
                if (ActionCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is ActionCueNode action
                    ? await ActionCueExecutor(action, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Comment:
                return Strings.CueCommentResult;
            case CueNodeKind.Visualizer:
                if (VisualizerCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is VisualizerCueNode viz
                    ? await VisualizerCueExecutor(viz, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Jump:
                // Control flow is transport/UI state - marshal to the UI thread (this executor runs on a
                // worker). The jump moves the playhead to a target (loop back / section repeat / random
                // pick) and, by default, fires it through the normal GO machinery.
                return await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => ExecuteJumpCueOnUi(cue));
            case CueNodeKind.Fade:
            {
                if (FadeCueExecutor is null)
                    return Strings.CueFadeExecutionNotConfigured;
                if (cue.ToModel() is not FadeCueNode fade)
                    return Strings.CueInvalidActionCue;
                // Target resolution reads transport state (_activeCueIds) and the cue tree - UI thread.
                var fadeTargets = await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => ResolveFadeCueTargetsOnUi(cue));
                if (fadeTargets.Count == 0)
                    // "Fade all playing" with nothing playing is a benign no-op; an explicit target
                    // list that resolved to nothing is an authoring error worth surfacing.
                    return cue.FadeTargetAllPlaying ? null : Strings.CueFadeNoTargets;
                return await FadeCueExecutor(fade, fadeTargets, ct);
            }
            case CueNodeKind.Group:
            default:
                return null;
        }
    }

    /// <summary>Executes a Jump cue (UI thread): resolves a live target by stable cue ID (numbers are
    /// display-only, so renumber/reorder never retargets), picks randomly when configured, then either
    /// fires the target (default - the loop actually loops) or arms it as standby for the next GO.</summary>
    internal string? ExecuteJumpCueOnUi(CueNodeViewModel jump)
    {
        if (jump.JumpTargetIds.Count == 0)
            return Strings.CueJumpNoTargets;

        // Targets resolve inside the jump's OWN list (the selected one for every visible jump): a cue
        // fired from another list by a schedule/trigger runs its list's control flow, not the visible
        // list's. Jump target ids are authored links within one list.
        var byId = EnumerateAllCueNodesFor(jump).ToDictionary(c => c.Id, c => c);
        var live = jump.JumpTargetIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Where(c => c.Kind != CueNodeKind.Comment)
            .DistinctBy(c => c.Id)
            .ToList();
        if (live.Count == 0)
            return Strings.CueJumpTargetMissing;

        if (!_immediateJumpChain.Add(jump.Id))
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        // A group target resolves through its fire mode to a concrete first cue. Exclude any candidate whose
        // next immediate jump was already visited: e.g. Jump #5 → containing Group → first child Jump #5.
        // Valid media/action targets in the same pool remain usable, so one bad group link cannot spin the app.
        var safe = live.Where(candidate =>
        {
            var resolved = ResolveFireableCue(candidate);
            return resolved is null
                   || resolved.Kind != CueNodeKind.Jump
                   || !_immediateJumpChain.Contains(resolved.Id);
        }).ToList();
        if (safe.Count == 0)
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        var candidates = safe;
        if (jump.JumpRandom
            && jump.JumpAvoidImmediateRepeat
            && safe.Count > 1
            && _lastRandomJumpTargetIds.TryGetValue(jump.Id, out var previousTargetId))
        {
            var nonRepeating = safe.Where(candidate => candidate.Id != previousTargetId).ToList();
            if (nonRepeating.Count > 0)
                candidates = nonRepeating;
        }

        var target = jump.JumpRandom && candidates.Count > 1
            ? candidates[Random.Shared.Next(candidates.Count)]
            : candidates[0];
        if (jump.JumpRandom && jump.JumpAvoidImmediateRepeat)
            _lastRandomJumpTargetIds[jump.Id] = target.Id;
        else
            _lastRandomJumpTargetIds.Remove(jump.Id);
        var resolvedTarget = ResolveFireableCue(target);

        if (IsForeignListNode(jump))
        {
            // Cross-list merged session: this jump belongs to a list the operator is not looking at, so
            // it must not arm the VISIBLE standby. "Standby" mode therefore has nothing to point at and
            // ends the chain; a firing jump continues headlessly in its own list.
            _immediateJumpChain.Clear();
            if (!string.Equals(jump.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase))
                _ = GoForeignListAsync(target);
            return null;
        }

        StandbyCueNode = target;
        if (!string.Equals(jump.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedTarget?.Kind != CueNodeKind.Jump)
                _immediateJumpChain.Clear();
            _ = GoCore(); // preserve the visited set only across an immediate Jump→Jump continuation
        }
        else
        {
            _immediateJumpChain.Clear();
        }
        return null;
    }

    /// <summary>Pre-wait visibility threshold: shorter waits are perceptually immediate and a badge
    /// would only flicker.</summary>
    private static readonly TimeSpan PreWaitCountdownThreshold = TimeSpan.FromMilliseconds(1500);

    /// <param name="isPaused">Which run's pause state freezes this pre-wait. Null = the VISIBLE
    /// transport (the operator GO path). A headless cross-list run passes its OWN state: gating a
    /// foreign list's pre-waits on <see cref="IsTransportPaused"/> meant that pausing the list the
    /// operator happened to be LOOKING at froze every other list's scheduled pre-rolls too - a
    /// half-hour station-ID pre-wait in an automation list simply never came due. A pre-wait is a
    /// scheduling delay, not playback; the visible Pause has no claim on a cue that has not started.</param>
    private async Task WaitUntilDelayAsync(
        DateTime startedAtUtc, int delayMs, CancellationToken ct,
        IReadOnlyList<CueNodeViewModel>? countdownCues = null,
        Func<bool>? isPaused = null)
    {
        if (delayMs <= 0)
            return;

        isPaused ??= () => IsTransportPaused;
        var showingCountdown = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (isPaused())
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(40, ct);
                    startedAtUtc = startedAtUtc.AddMilliseconds(40);
                }

                var due = startedAtUtc.AddMilliseconds(delayMs);
                var remaining = due - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;

                // Pre-wait visibility (review §6): a live "⏳ in m:ss" badge on the waiting cues' rows
                // so the operator sees WHY nothing is sounding yet. Runs on the UI thread already.
                if (countdownCues is not null && remaining >= PreWaitCountdownThreshold)
                {
                    showingCountdown = true;
                    var text = $"⏳ in {(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                    foreach (var cue in countdownCues)
                        cue.PreWaitCountdownText = text;
                }
                else if (showingCountdown)
                {
                    ClearPreWaitCountdown(countdownCues);
                    showingCountdown = false;
                }

                var slice = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
                await Task.Delay(slice, ct);
            }
        }
        finally
        {
            if (showingCountdown)
                ClearPreWaitCountdown(countdownCues);
        }
    }

    private static void ClearPreWaitCountdown(IReadOnlyList<CueNodeViewModel>? cues)
    {
        if (cues is null)
            return;
        foreach (var cue in cues)
            cue.PreWaitCountdownText = null;
    }

    private void CancelTransportRun()
    {
        try { _transportRunCts?.Cancel(); } catch { /* best effort */ }
        try { _transportRunCts?.Dispose(); } catch { /* best effort */ }
        _transportRunCts = null;
    }

    /// <summary>Cancels every headless cross-list run (Stop / Panic, which stop the whole session).
    /// A list switch deliberately does NOT: those runs play in the same merged session and keep going,
    /// exactly like the clips they started.</summary>
    private void CancelForeignListRuns()
    {
        if (_foreignListRuns.Count == 0)
            return;
        foreach (var cts in _foreignListRuns.Values)
        {
            try { cts.Cancel(); } catch { /* best effort */ }
            try { cts.Dispose(); } catch { /* best effort */ }
        }

        _foreignListRuns.Clear();
    }

    private static IEnumerable<CueNodeViewModel> EnumerateMediaNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Media)
                yield return node;
            foreach (var child in EnumerateMediaNodes(node.Children))
                yield return child;
        }
    }

    public void SetActionEndpoints(IEnumerable<ActionEndpoint> endpoints)
    {
        ActionEndpoints.Clear();
        foreach (var endpoint in endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            ActionEndpoints.Add(endpoint);
        if (SelectedActionCue is { } actionCue && Guid.TryParse(actionCue.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        RefreshBrokenEndpointFlags();
    }

    private string? _cueListsCollectionPath;

    public string? CueListsCollectionPath => _cueListsCollectionPath;

    public string? DisplayedCueFilePath => _cueListsCollectionPath ?? SelectedCueList?.Path;

    public List<CueList> BuildCueListsSnapshot() => CueLists.Select(c => c.ToModel()).ToList();

    public void ApplyCueLists(IReadOnlyList<CueList> lists, string? collectionPath = null)
    {
        _lastRandomJumpTargetIds.Clear();
        _playlistRuns.Clear(); // playlist runs are session state - a (re)load starts them afresh
        _cueListsCollectionPath = collectionPath;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
        CueLists.Clear();
        foreach (var list in lists)
            CueLists.Add(CueListEditorViewModel.FromModel(list, resolveLine: ResolveOutputLine));
        if (CueLists.Count == 0)
            CueLists.Add(new CueListEditorViewModel(Strings.DefaultCueListName));
        SelectedCueList = CueLists[0];
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RefreshCueTargetDisplays();
    }

    private void ClearCueListsCollectionPath()
    {
        if (_cueListsCollectionPath is null)
            return;

        _cueListsCollectionPath = null;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow;
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime single
            && single.MainView is Window w)
            return w;
        return null;
    }
}
