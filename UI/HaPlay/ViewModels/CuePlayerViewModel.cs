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

/// <summary>
/// The cue player: cue lists, the selection the drawer edits, the transport that fires them, and everything
/// the operator sees while they run.
///
/// <para><b>Where things live.</b> This class was always partial, but the ROOT file had become the place
/// anything without an obvious home landed - 4 479 of the 8 061 lines, against eight purposeful partials.
/// Re-split 2026-07-30 (review §3). This file now keeps the spine: the executor/callback surface the host
/// wires up, cue-list and selection state, the armed gates and master trim, the derived
/// <c>HasSelectedX</c>/<c>TransportState</c> facade the view binds, and pre-roll/standby bookkeeping.
/// The rest is one concern per file:</para>
/// <list type="table">
/// <item><term><c>.Transport.cs</c></term><description>GO / Back / Stop / pause and the standby cursor.</description></item>
/// <item><term><c>.Execution.cs</c></term><description>running a trigger plan, dispatching each cue to its
///   executor, applying the result, jump and auto-follow.</description></item>
/// <item><term><c>.NowPlaying.cs</c></term><description>active cues, Now Playing rows, upcoming, row status,
///   scrubbing - everything downstream of an engine callback.</description></item>
/// <item><term><c>.Playlists.cs</c></term><description>playlist / armed-list group runs and their
///   session-only state.</description></item>
/// <item><term><c>.Preview.cs</c></term><description>the operator's audition path and its waveform.</description></item>
/// <item><term><c>.SelectionProperties.cs</c></term><description>the drawer's two-way bound facade over the
///   selected cue.</description></item>
/// <item><term><c>.LiveEditWatch.cs</c></term><description>"this property changed - does the running clip
///   need to know?", and the pushes that answer yes.</description></item>
/// <item><term><c>.CueAuthoring.cs</c> / <c>.CueEditing.cs</c> / <c>.MultiEditing.cs</c> /
///   <c>.CompositionEditing.cs</c> / <c>.Clipboard.cs</c> / <c>.Search.cs</c> /
///   <c>.Persistence.cs</c></term><description>authoring, as before.</description></item>
/// </list>
/// </summary>
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
        ResolveAllBindingLineRefs();
        // Line-up changes re-derive the preview preselect (no-op once the operator picked a device):
        // the first configured PortAudio line's device beats the implicit "Default device".
        ApplyAutomaticPreviewDeviceSelection();
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

    /// <summary>Whether the placement editor pulls a dragged layer's edges/centre onto the composition's
    /// edges/centre. Editor preference, not show data - it changes how dragging FEELS, never what is saved,
    /// so it is deliberately not persisted with the cue list.</summary>
    [ObservableProperty]
    private bool _snapPlacementsToEdges = true;

    /// <summary>Aspect ratio (w/h) of the composition the placement editor canvas mirrors - ALWAYS the
    /// composition's own resolution.
    /// <para>It used to follow the live aspect of a resizable local window the composition fed, on the
    /// reasoning that the canvas should look like what the operator sees on that output. That was wrong: a
    /// placement's DestX/Y/Width/Height are normalized against the COMPOSITION, so a canvas drawn at the
    /// window's aspect draws every placement at the wrong shape and position - and resizing the output
    /// window visibly moved rectangles that had not changed. Where the composition then lands inside a
    /// mismatched output is a separate, later mapping step, and it has its own editor
    /// (<c>CompositionOutputLayoutDialog</c> / <c>OutputLayoutCanvas</c>) - which is the right place to see
    /// and author letterboxing.</para></summary>
    public double PlacementCanvasAspect
    {
        get
        {
            var comp = SelectedVideoPlacement is { } p
                ? SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                : null;
            comp ??= SelectedComposition ?? SelectedCueList?.Compositions.FirstOrDefault();
            return comp is { Width: > 0, Height: > 0 } ? (double)comp.Width / comp.Height : 16.0 / 9.0;
        }
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
    /// <param name="playlistRunsToArm">Collects every NESTED playlist/armed-list group this plan fires as
    /// one of its items. Those groups own a run (armed pick, pass counters, natural-end advance) and the
    /// caller must <see cref="ConsumePlaylistPick"/> each one, exactly as it does for a playlist group fired
    /// directly - an unarmed run has a null <see cref="PlaylistRunState.CurrentItemId"/>, so
    /// <see cref="FindPlaylistRunForEndedCue"/> swallows its item's end and the list never advances. Null for
    /// read-only callers (standby pre-roll), which must not consume anything.</param>
    internal List<(CueNodeViewModel Cue, int DelayMs, bool Independent)> BuildTriggerPlan(
        CueNodeViewModel target, List<CueNodeViewModel>? playlistRunsToArm = null)
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
            foreach (var child in children)
                AppendOverlapLane(plan, child, groupPreWait, playlistRunsToArm);
            plan.Sort(static (a, b) => a.DelayMs.CompareTo(b.DelayMs));
            return plan;
        }

        if (mode == CueGroupFireMode.Timeline)
        {
            // Same delay-sorted plan as FireAllSimultaneously, with the authored lane start added on the
            // group's plan epoch, so both overlap modes expand a lane through the SAME rules.
            foreach (var child in children)
                AppendOverlapLane(
                    plan, child, checked(groupPreWait + Math.Max(0, child.TimelineStartMs)), playlistRunsToArm);
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
                // step's own independence). The pick is NOT added to playlistRunsToArm even when it is
                // itself a playlist group: ConsumePlaylistPick recurses into its own pick, so recording
                // it here would consume that inner run twice and skip an item.
                foreach (var (cue, delayMs, independent) in BuildTriggerPlan(pick, playlistRunsToArm))
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

    /// <summary>Expands ONE lane of an overlap-mode group (FireAllSimultaneously / Timeline) at
    /// <paramref name="laneBase"/>. Shared by both so their nesting rules cannot drift.
    /// <para>A nested group is expanded by WHAT IT OWNS, and there are two cases:</para>
    /// <para><b>Playlist / ArmedList</b> fire through their own <see cref="BuildTriggerPlan"/> - one armed
    /// pick, not every item. These modes own run STATE, so flattening them was not merely "fires extra
    /// cues": it fired items the run never armed and left the run unarmed, so the list could never advance
    /// (the testproject.haplayproj bug - a fire-all group over a playlist started both songs at once). The
    /// inner step's own <c>Independent</c> flag is preserved deliberately: a playlist's picks must REPLACE
    /// each other in one shared transport group as the list advances, which is exactly what marking them
    /// independent would break. (Known limitation: two playlist lanes under one overlap parent therefore
    /// share that transport group and would replace each other - it needs a runtime group per LANE, not per
    /// cue. One playlist lane beside any number of non-playlist lanes is fine.)</para>
    /// <para><b>Any other group</b> keeps the historical flattening to its fireable descendants: it holds no
    /// run state, so "fire all together" reaching through it is a scoping choice rather than a correctness
    /// bug, and <c>TimelineGroupTests</c> pins it. Its own pre-wait now offsets its descendants (it always
    /// did in Timeline mode; sim mode used to drop it, which was the two branches disagreeing).</para></summary>
    private void AppendOverlapLane(
        List<(CueNodeViewModel Cue, int DelayMs, bool Independent)> plan,
        CueNodeViewModel node,
        int laneBase,
        List<CueNodeViewModel>? playlistRunsToArm)
    {
        if (node.Kind == CueNodeKind.Comment)
            return;

        if (node.Kind != CueNodeKind.Group)
        {
            plan.Add((node, checked(laneBase + Math.Max(0, node.PreWaitMs)), true));
            return;
        }

        if (IsPlaylistGroup(node))
        {
            playlistRunsToArm?.Add(node);
            // BuildTriggerPlan already applies the nested group's own pre-wait, so laneBase alone here.
            foreach (var (cue, delayMs, independent) in BuildTriggerPlan(node, playlistRunsToArm))
                plan.Add((cue, checked(laneBase + delayMs), independent));
            return;
        }

        var nestedBase = checked(laneBase + Math.Max(0, node.PreWaitMs));
        foreach (var child in node.Children)
            AppendOverlapLane(plan, child, nestedBase, playlistRunsToArm);
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

    /// <summary>Raised after <see cref="ApplyCueLists"/> has fully replaced the authored cue
    /// document. Session-scoped schedulers/triggers use it to discard state tied to the old tree.</summary>
    public event EventHandler? CueDocumentReplaced;

    public void ApplyCueLists(IReadOnlyList<CueList> lists, string? collectionPath = null)
    {
        // Replacing the document invalidates every node captured by a pre-wait/auto-continue run. Without
        // this, an old project's delayed cue can fire after the new lists (and ShowSession document) load.
        CancelTransportRun();
        CancelForeignListRuns();
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
        CueDocumentReplaced?.Invoke(this, EventArgs.Empty);
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
