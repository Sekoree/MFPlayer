using System.Globalization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Core.Timeline;
using HaCue2.Engine;
using HaCue2.Presentation;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>Screens 02, 03 and 05 — the cue tree, the Active panel, the right column, the timeline.</summary>
public partial class CuesViewModel : ObservableObject
{
    public const string PropertiesTab = "CUE PROPERTIES";
    public const string ListsTab = "LISTS & GROUPS";

    private readonly ProjectJournal _journal;
    private readonly ShowRuntime _runtime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPanelWidth))]
    [NotifyPropertyChangedFor(nameof(RightSplitterWidth))]
    [NotifyPropertyChangedFor(nameof(RightPanelToggleLabel))]
    private bool _isRightPanelOpen = true;

    public GridLength RightPanelWidth => new(IsRightPanelOpen ? 316 : 0);
    public GridLength RightSplitterWidth => new(IsRightPanelOpen ? 4 : 0);
    public string RightPanelToggleLabel => IsRightPanelOpen ? "HIDE INSPECTOR" : "SHOW INSPECTOR";

    public CuesViewModel(ProjectJournal journal, ShowRuntime runtime)
    {
        _journal = journal;
        _runtime = runtime;

        Inspector = new InspectorViewModel(journal)
        {
            // Where a waveform scan is cached, honouring a machine's override. Set here rather than
            // read inside the inspector: which folder derived files live in is an APP setting, and the
            // inspector is a projection of the document.
            CacheRoot = HaCue2.Machine.MediaCache.RootFor(App.Settings),
        };
        Inspector.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(RightPanelHeader));
            OnPropertyChanged(nameof(RightPanelHint));
        };

        _scopes = BuildScopes();
        // The first list, not a group: the app opens showing everything in it, and scoping is
        // something the operator does on purpose (register item 7).
        _selectedScope = _scopes.FirstOrDefault(scope => scope.IsList);

        Timeline = new TimelineViewModel(Project, runtime, journal) { Owner = this };

        Cues = [];
        ActiveCues = [.. runtime.ActiveCues];

        // The source exists BEFORE the first Rebuild: Rebuild clears the tree selection before it
        // touches the rows, and it cannot do that against a source that has not been built yet. The
        // source observes the collection, so filling it afterwards is what it expects.
        CueSource = BuildSource();
        Rebuild();

        CueSource.RowSelection!.SelectionChanged += (_, _) =>
        {
            // The tree is the authority on what is selected — SelectedCue follows it rather than the
            // other way round, so a click, a keyboard move and a programmatic set all take one path.
            SetProperty(ref _selectedCue, CueSource.RowSelection.SelectedItem, nameof(SelectedCue));
            OnPropertyChanged(nameof(CanEditSource));
            OnPropertyChanged(nameof(CanOpenTimeline));
            OnPropertyChanged(nameof(HasSelection));
            Inspector.Facts = FactsFor(CueSource.RowSelection.SelectedItem);
            Inspector.Show([.. CueSource.RowSelection.SelectedItems.OfType<CueRow>().Select(row => row.Id)]);
            OnPropertyChanged(nameof(CanModifySelection));
            OnPropertyChanged(nameof(ToggleEnabledLabel));

            if (!_restoringSelection
                && Project.Settings.ClickMovesStandby
                && CueSource.RowSelection.SelectedItem is { } selected
                && ScopedList is { } list)
                SetStandby(list, selected.Id);
        };

        RestoreSelection(Cues.FirstOrDefault());
    }

    private HaCueProject Project => _journal.Project;

    /// <summary>The project file used to resolve a relative media root.</summary>
    public Func<string?>? ProjectPath { get; set; }

    /// <summary>Media import failures. Import continues with the other selected files.</summary>
    /// <remarks>
    /// It was written and never shown anywhere — a file that could not be copied into the media root
    /// produced no cue and no word about why. The add row carries it now.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMediaImportProblem))]
    private string _mediaImportProblem = "";

    public bool HasMediaImportProblem => MediaImportProblem.Length > 0;

    /// <summary>
    /// Whether a source discovery scan is running.
    /// </summary>
    /// <remarks>
    /// NDI discovery is a two-second network scan. It moved off the UI thread so the transport stays
    /// live through it, which left the click looking like it had done nothing — and let a second click
    /// start a second scan and stack a second dialog. This says it is happening and gates the verb.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTransportHint))]
    [NotifyPropertyChangedFor(nameof(ShowsPreviewHint))]
    private bool _isScanningSources;

    /// <summary>The add row shows one line at a time: a scan, an audition, or the standing hint.</summary>
    public bool ShowsTransportHint => !IsPreviewing && !IsScanningSources;

    public bool ShowsPreviewHint => IsPreviewing && !IsScanningSources;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>The cue list the tree is currently showing, whether scoped to it or to a group in it.</summary>
    public CueList? ScopedList => SelectedScope is not { } scope
        ? Project.CueLists.FirstOrDefault()
        : scope.IsList
            ? Project.CueLists.FirstOrDefault(list => list.Id == scope.Id)
            : Project.CueLists.FirstOrDefault(list => list.Flatten().Any(cue => cue.Id == scope.Id));

    // ── the tree ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The TOP-LEVEL rows. A group's cues hang off <see cref="CueRow.Children"/>.</summary>
    public ObservableCollection<CueRow> Cues { get; }

    /// <summary>Every row in fire order, groups included — what a flat question asks.</summary>
    public IEnumerable<CueRow> AllRows => CuePresentation.Flatten(Cues);

    /// <summary>
    /// What the tree control binds to.
    /// </summary>
    /// <remarks>
    /// Built here rather than declared in XAML because the app needs the selection model: multi-select
    /// (screen 04's tab intersection) and the inspector both hang off <c>RowSelection</c>, and the
    /// XAML-only form does not hand it over.
    /// </remarks>
    public HierarchicalTreeDataGridSource<CueRow> CueSource { get; }

    private HierarchicalTreeDataGridSource<CueRow> BuildSource()
    {
        var source = new HierarchicalTreeDataGridSource<CueRow>(Cues)
        {
            Columns =
            {
                // The state stripe is its OWN column, before the expander, so it stays flush with the
                // left edge — indentation must not push a cue's most urgent state out of line.
                new TemplateColumn<CueRow>(null, "StripeCell", width: new GridLength(3)),
                new HierarchicalExpanderColumn<CueRow>(
                    new TemplateColumn<CueRow>("CUE", "NumberCell", width: new GridLength(112)),
                    row => row.Children,
                    row => row.HasChildren,
                    row => row.IsExpanded),
                new TemplateColumn<CueRow>("LABEL", "LabelCell", width: new GridLength(1, GridUnitType.Star)),
                new TemplateColumn<CueRow>("SOURCE / TARGET", "SourceCell", width: new GridLength(148)),
                new TemplateColumn<CueRow>("FADE", "FadeCell", width: new GridLength(52)),
                new TemplateColumn<CueRow>("LEN", "LengthCell", width: new GridLength(52)),
                new TemplateColumn<CueRow>("DB", "LevelCell", width: new GridLength(48)),
                new TemplateColumn<CueRow>(null, "BadgeCell", width: new GridLength(66)),
            },
        };

        // Screen 04's tab table is the intersection of the selected kinds', which needs more than one.
        source.RowSelection!.SingleSelect = false;
        return source;
    }

    private CueRow? _selectedCue;
    private bool _restoringSelection;

    /// <summary>
    /// The lead selected row. Setting it drives the TREE's selection, which then reports back.
    /// </summary>
    /// <remarks>
    /// One direction only. Assigning the field here and telling the inspector separately would let the
    /// two disagree the moment the tree changed selection on its own — which it does whenever a row is
    /// clicked, or the keyboard moves, or a rebuild drops the row that was selected. Selecting a cue
    /// must NOT flip the right panel to Cue properties (register item 7), and it does not.
    /// </remarks>
    public CueRow? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (ReferenceEquals(_selectedCue, value))
                return;

            if (value is null)
                CueSource.RowSelection!.Clear();
            else if (IndexOf(value) is { } path)
                CueSource.RowSelection!.SelectedIndex = path;
        }
    }

    /// <summary>
    /// Restores an app-selected row without treating it as an operator click. Rebuilds replace row
    /// objects, so their selection event is unavoidable; suppressing its transport side effect keeps
    /// an ordinary edit from moving standby (and from recursively journalling another refresh).
    /// </summary>
    private void RestoreSelection(CueRow? value)
    {
        _restoringSelection = true;
        try
        {
            SelectedCue = value;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    /// <summary>
    /// Restores a WHOLE selection after a rebuild, lead first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single-row overload sets <see cref="ITreeDataGridRowSelectionModel{T}.SelectedIndex"/>,
    /// which REPLACES the selection. Refresh used it with only the lead row's id, so every edit made
    /// on a multi-selection collapsed that selection to one cue as soon as it was applied: the first
    /// change reached all eleven stems and every change after it reached only the lead, which reads as
    /// multi-edit simply not working.
    /// </para>
    /// <para>
    /// The lead is selected first so it stays <c>SelectedItem</c> — the inspector's single-value fields
    /// read from it, and having those jump to a different cue after every keystroke would be its own
    /// bug. Batched so the tree raises one selection change rather than one per row.
    /// </para>
    /// </remarks>
    private void RestoreSelection(IReadOnlyList<Guid> ids)
    {
        if (ids.Count <= 1)
        {
            RestoreSelection(ids.Count == 0 ? null : AllRows.FirstOrDefault(row => row.Id == ids[0]));
            return;
        }

        var paths = ids
            .Select(id => AllRows.FirstOrDefault(row => row.Id == id))
            .OfType<CueRow>()
            .Select(IndexOf)
            .OfType<IndexPath>()
            .ToList();

        _restoringSelection = true;
        try
        {
            var selection = CueSource.RowSelection!;
            selection.BeginBatchUpdate();

            try
            {
                selection.Clear();
                foreach (var path in paths)
                    selection.Select(path);
            }
            finally
            {
                selection.EndBatchUpdate();
            }

            // The batch suppressed the event the single-row path relies on to publish the lead.
            SetProperty(ref _selectedCue, selection.SelectedItem, nameof(SelectedCue));
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    /// <summary>Where a row sits in the tree, as the index path the selection model speaks in.</summary>
    private IndexPath? IndexOf(CueRow target)
    {
        static IndexPath? Search(IReadOnlyList<CueRow> rows, CueRow target, IndexPath prefix)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var here = prefix.Append(index);

                if (ReferenceEquals(rows[index], target))
                    return here;

                if (Search(rows[index].Children, target, here) is { } found)
                    return found;
            }

            return null;
        }

        return Search(Cues, target, default);
    }

    /// <summary>Where the track lists come from. Null until the shell supplies one.</summary>
    public Func<MediaCueNode, MediaFacts?>? MediaFacts { get; set; }

    private MediaFacts? FactsFor(CueRow? row) =>
        row is not null && MediaFacts is { } lookup && Project.FindCue(row.Id) is MediaCueNode media
            ? lookup(media)
            : null;

    // ── transport (register item 3: GO always works, nothing gates playback) ───────────────────

    /// <summary>
    /// Fires standby and moves it to the next cue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Next" is the next ENABLED cue in list order — a disabled cue stays visible and is stepped over
    /// rather than deleted, which is the whole reason disabling exists (dropping one for a single
    /// performance by deleting it is how shows lose cues).
    /// </para>
    /// <para>
    /// No engine yet, so nothing sounds. The CURSOR is real, and it is the half that has to be right:
    /// where standby lands after a GO is the thing an operator watches all night.
    /// </para>
    /// </remarks>
    public void Go() => _ = GoCoreAsync();

    private async Task GoCoreAsync()
    {
        if (ScopedList is not { } list)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastGoTicks != 0
            && Stopwatch.GetElapsedTime(_lastGoTicks, now) < DoubleGoGuard)
            return;
        _lastGoTicks = now;

        // With a session, GO is the session's: it fires standby, advances its own cursor and reports
        // back through the poll. Moving the cursor here as well would fight it.
        if (Engine is { } host)
        {
            if (await host.GoAsync(list).ConfigureAwait(true) is not null
                && AfterGo is { } afterGo)
                await afterGo().ConfigureAwait(true);
            return;
        }

        // The same rule the running transport uses, from the same place: a cursor that behaved one way
        // with a session and another way without one is a rehearsal that does not match the show.
        SetStandby(list, CueOrder.NextEnabled(list, list.StandbyCueId)?.Id);
        if (AfterGo is { } afterCursorGo)
            await afterCursorGo().ConfigureAwait(true);
    }

    private long _lastGoTicks;

    /// <summary>Minimum interval between operator GO gestures; cue-driven follows do not pass here.</summary>
    public TimeSpan DoubleGoGuard { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Optional persistence hook used by Save-on-GO.</summary>
    public Func<Task>? AfterGo { get; set; }

    private ShowHost? _engine;

    /// <summary>The running show, when there is one. Set by the shell after it starts the engine.</summary>
    public ShowHost? Engine
    {
        get => _engine;
        set
        {
            _engine = value;
            Inspector.Host = value;
            SyncActiveCueList();
        }
    }

    private void SyncActiveCueList() => Engine?.SetActiveCueList(ScopedList?.Id);

    /// <summary>What the transport row says about itself.</summary>
    public string TransportHint => Engine is null
        ? "GO always works — editing never blocks playback"
        : "live · editing never blocks playback";

    /// <summary>
    /// Stops the selected active cue — the bare STOP.
    /// </summary>
    /// <remarks>
    /// One cue, not the show. On a night where a music bed is under a video, the operator reaching for
    /// STOP because the video has to go almost never wants the bed to go with it — and the one who
    /// does wants <see cref="StopAll"/>, which is a deliberate second gesture rather than the same
    /// button meaning two things.
    /// <para>
    /// "Selected" is the selected cue when that cue is sounding, and otherwise the cue that has been
    /// running longest — which is what somebody means by "stop that" when they have not clicked
    /// anything.
    /// </para>
    /// </remarks>
    public void Stop()
    {
        if (Engine is not { } host)
            return;

        // The runtime's list, not the tree's: the tree is scoped and a sounding cue outside the current
        // scope is exactly the one an operator cannot see and most needs to be able to stop.
        var target = SelectedCue is { } row && _runtime.Sounding.Contains(row.Id)
            ? row.Id
            : _runtime.ActiveCues.FirstOrDefault()?.CueId;

        if (target is { } cueId)
            _ = host.StopCueAsync(cueId);
    }

    /// <summary>Stops one named cue — the × on its Active row.</summary>
    /// <remarks>
    /// By id rather than "the selected one", because the row IS the selection here: an operator
    /// pressing × on the third row means the third row, whatever the cue tree has highlighted.
    /// </remarks>
    public void StopCue(Guid cueId) => _ = Engine?.StopCueAsync(cueId);

    /// <summary>
    /// Stops everything a group is holding.
    /// </summary>
    /// <remarks>
    /// Scoped to the group, never to the show: an operator pressing × on a playlist header means that
    /// playlist, not the bed running underneath it from another list. Only what is actually SOUNDING is
    /// stopped, so a group whose chain has not reached a cue does not get a stop it never started.
    /// </remarks>
    public void StopGroup(Guid groupId)
    {
        if (Engine is not { } host || Project.FindCue(groupId) is not GroupCueNode group)
            return;

        foreach (var child in group.Children.Where(child => _runtime.Sounding.Contains(child.Id)))
            _ = host.StopCueAsync(child.Id);
    }

    /// <summary>
    /// Moves a sounding cue's playhead to a fraction of its length.
    /// </summary>
    /// <remarks>
    /// A fraction rather than a time, because that is what the bar knows — it is as wide as the clip is
    /// long and nothing more. Refusals are shown rather than swallowed: seeking a cue that has just
    /// ended is an ordinary near-miss, and a bar that appeared to work and did not would send somebody
    /// looking for a broken file.
    /// </remarks>
    public async Task SeekActiveAsync(Guid cueId, double fraction)
    {
        if (Engine is not { } host)
            return;

        // The lock is enforced HERE as well as on the bar. The control refuses the gesture, and this
        // refuses the command — a seek that arrived from anywhere else must meet the same latch.
        if (!CanSeekActive)
        {
            TransportProblem = "seeking is locked — unlock it above the Active panel first";
            return;
        }

        if (_runtime.ActiveCues.FirstOrDefault(row => row.CueId == cueId) is not { Duration: { } length })
            return;

        var target = length * Math.Clamp(fraction, 0, 1);

        if (await host.SeekCueAsync(cueId, target).ConfigureAwait(true) is { } problem)
            TransportProblem = problem;
    }

    /// <summary>The last refusal from a transport gesture, or nothing.</summary>
    [ObservableProperty]
    private string _transportProblem = "";

    /// <summary>
    /// The explicit seek latch used while the document is Locked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default. Editing mode permits the bars independently; in Locked/performance mode the bars
    /// sit under the pointer for the whole show, a drag is instantly audible in the room, and there is
    /// no undo for it — an operator reaching past the panel must not move an on-air playhead by accident.
    /// </para>
    /// <para>
    /// A latch rather than a modifier key: seeking is something an operator does deliberately during a
    /// rehearsal or a fix, and holding a key down while dragging is a two-handed gesture on a surface
    /// meant to be driven with one.
    /// </para>
    /// <para>
    /// Machine-scope rather than in the document: whether THIS operator wants the bars live is a
    /// preference, not a property of the show, and a show carried to another booth should not arrive
    /// with somebody else's answer.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeekActive))]
    [NotifyPropertyChangedFor(nameof(SeekLockLabel))]
    [NotifyPropertyChangedFor(nameof(SeekLockHint))]
    private bool _seekUnlocked;

    /// <summary>
    /// Whether a live cue can be seeked from the Active panel.
    /// </summary>
    /// <remarks>
    /// Editing mode already permits document-changing gestures throughout the cue editor, so making an
    /// operator discover a second, unrelated lock there turns the bar into an apparent dead control.
    /// Locked/performance mode keeps the explicit safety latch because a seek is immediate and cannot
    /// be undone. The setter exists for the two-way toggle and only writes that latch in locked mode.
    /// </remarks>
    public bool CanSeekActive
    {
        get => CanEditDocument || SeekUnlocked;
        set
        {
            if (!CanEditDocument)
                SeekUnlocked = value;
        }
    }

    public string SeekLockLabel => CanEditDocument
        ? "SEEK ENABLED"
        : SeekUnlocked ? "SEEK UNLOCKED" : "SEEK LOCKED";

    public string SeekLockHint => CanEditDocument
        ? "editing mode — drag an Active bar to seek a playing cue"
        : SeekUnlocked
            ? "the bars in Active can be dragged — a drag moves a cue that is on air"
            : "click to allow dragging the bars in Active";

    /// <summary>Stops everything, over the project's stop fade. The split-menu half of STOP.</summary>
    public void StopAll() => _ = Engine?.StopAllAsync();

    /// <summary>Whether the operator-facing Stop All command should ask before firing.</summary>
    public bool StopAllNeedsConfirmation =>
        ConfirmStopAllThreshold > 0 && _runtime.ActiveCues.Count >= ConfirmStopAllThreshold;

    public int ConfirmStopAllThreshold { get; set; } = 3;

    /// <summary>
    /// Stops everything as fast as the project's panic fade allows.
    /// </summary>
    /// <remarks>
    /// Reached only by HOLDING the button, because the one button an operator hits without reading is
    /// the one that must not fire on a mis-click.
    /// </remarks>
    public void Panic()
    {
        CancelPanic();
        _ = Engine?.PanicAsync();
    }

    /// <summary>How long PANIC must be held before it fires.</summary>
    /// <remarks>Long enough that a brush against the button does nothing, short enough that somebody
    /// who means it does not have to wait and wonder whether it is working.</remarks>
    private static readonly TimeSpan PanicHold = TimeSpan.FromMilliseconds(400);

    private DispatcherTimer? _panic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanicLabel))]
    private bool _isPanicArming;

    /// <summary>The button says what it is doing, so a hold that is working looks like one.</summary>
    public string PanicLabel => IsPanicArming ? "HOLD…" : "PANIC";

    /// <summary>Starts the hold. Fires once it completes; nothing happens if the pointer leaves first.</summary>
    public void BeginPanic()
    {
        if (_panic is not null)
            return;

        IsPanicArming = true;
        _panic = new DispatcherTimer(PanicHold, DispatcherPriority.Input, (_, _) => Panic());
        _panic.Start();
    }

    /// <summary>Abandons a hold that was released early — a mis-click, and nothing happens.</summary>
    public void CancelPanic()
    {
        _panic?.Stop();
        _panic = null;
        IsPanicArming = false;
    }

    /// <summary>Pauses or resumes — one button, and it reports which it would do.</summary>
    public void TogglePause()
    {
        if (Engine is { } host)
            _ = host.SetPausedAsync(!_runtime.IsPaused);
    }

    public bool IsPaused => _runtime.IsPaused;

    public string PauseLabel => IsPaused ? "RESUME" : "PAUSE";

    /// <summary>
    /// Auditions the selected cue — "Preview on audition outputs" (register item 15).
    /// </summary>
    /// <remarks>
    /// Monitoring, never program: it enters the bay as a monitor input, so it cannot be heard by the
    /// audience and never appears in the Active list. That is what makes it safe to press mid-show,
    /// and why it is offered on every cue's context menu rather than hidden behind a mode.
    /// </remarks>
    public void PreviewSelected()
    {
        if (Engine is { } host && SelectedCue is { } row)
            _ = host.PreviewAsync(row.Id);
    }

    public void StopPreview() => _ = Engine?.StopPreviewAsync();

    /// <summary>Whether anything is being auditioned, for the transport's own readout.</summary>
    public bool IsPreviewing => Engine?.Previewing is not null;

    public string PreviewHint
    {
        get
        {
            if (Engine?.Previewing is not { } id)
                return "";

            return _journal.Project.FindCue(id) is { } cue
                ? $"auditioning Q{CuePresentation.Number(cue.Number)} · {cue.Label}"
                : "auditioning";
        }
    }

    /// <summary>Fires the selected cue directly, whatever the cursor is doing.</summary>
    public void FireSelected()
    {
        if (SelectedCue is { } row)
            _ = Engine?.FireAsync(row.Id);
    }

    /// <summary>
    /// Moves standby without firing — the ↑/↓ keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk is over the WHOLE list and only lands on enabled cues. Standby can legally rest on a
    /// disabled one — clicking it puts it there when <c>ClickMovesStandby</c> is on, and so does "move
    /// standby here" — and searching an enabled-only list for it found nothing and clamped to index 0.
    /// So ↓ STBY from a disabled cue jumped to the TOP of the list, which during a show moves the
    /// cursor most of an act away from where the operator is.
    /// </para>
    /// <para>
    /// Past either end the cursor HOLDS rather than wrapping: running off the bottom of the list and
    /// silently arriving back at cue one is not something an operator can see happen.
    /// </para>
    /// </remarks>
    public void StepStandby(int delta)
    {
        if (ScopedList is not { } list || delta == 0)
            return;

        var order = list.Flatten().ToList();
        var at = list.StandbyCueId is { } standby
            ? order.FindIndex(cue => cue.Id == standby)
            : -1;

        // No cursor yet, or one pointing outside this list: either key lands on the first enabled cue.
        if (at < 0)
        {
            if (order.FindIndex(cue => cue.Enabled) is >= 0 and var first)
                SetStandby(list, order[first].Id);

            return;
        }

        var step = Math.Sign(delta);
        var landed = at;

        for (var remaining = Math.Abs(delta); remaining > 0; remaining--)
        {
            var index = landed + step;

            while (index >= 0 && index < order.Count && !order[index].Enabled)
                index += step;

            if (index < 0 || index >= order.Count)
                break;

            landed = index;
        }

        if (landed != at)
            SetStandby(list, order[landed].Id);
    }

    /// <summary>
    /// Puts standby on the selected cue without firing it.
    /// </summary>
    /// <remarks>
    /// This remains an explicit row/menu verb. Escape is reserved for the conventional Stop, then
    /// double-Escape Panic safety sequence.
    /// </remarks>
    public void StandbyHere()
    {
        if (ScopedList is { } list && SelectedCue is { } row)
            SetStandby(list, row.Id);
    }

    private void SetStandby(CueList list, Guid? id)
    {
        if (list.StandbyCueId == id)
            return;

        // The session owns the cursor once it is running; the document follows it, not the reverse.
        if (Engine is { } host)
        {
            _ = host.StandbyAsync(list, id);
            return;
        }

        var target = list;

        _journal.Do(new SetValueCommand<Guid?>(
            list.Id, "standby", "cues",
            () => target.StandbyCueId, value => target.StandbyCueId = value, id,
            id is null ? "clear standby" : "move standby"));
        _journal.CloseGroup();

        Refresh();
    }

    // ── building a show ───────────────────────────────────────────────────────────────────────

    public bool CanEditDocument => !_journal.IsReadOnly;

    public bool CanModifySelection => CanEditDocument && Inspector.Selected.Count > 0;

    /// <summary>
    /// Whether the row verbs have a cue to act on.
    /// </summary>
    /// <remarks>
    /// Fire, preview and "move standby here" are not EDITS, so they stay available under Lock — but
    /// with nothing selected they silently did nothing, which reads as a broken menu rather than an
    /// empty selection.
    /// </remarks>
    public bool HasSelection => SelectedCue is not null;

    public string ToggleEnabledLabel => Inspector.Selected.Any(cue => !cue.Enabled)
        ? "Enable for this performance"
        : "Disable for this performance";

    /// <summary>
    /// Adds a cue of the given kind after the selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AFTER the selected cue rather than at the end: an operator adding a cue is almost always adding
    /// it where they are looking. Inside the selected group when a group is selected, for the same
    /// reason.
    /// </para>
    /// <para>
    /// Numbered by <see cref="AutoNumber"/>, which honours the project's auto-renumber setting
    /// (register item 20) — the new cue lands between its neighbours rather than at 0.
    /// </para>
    /// </remarks>
    public CueNode? AddCue(CueKind kind, string mediaPath = "")
    {
        if (!CanEditDocument || ScopedList is not { } list)
            return null;

        return Insert(list, NewCue(kind, mediaPath), kind.ToString().ToLowerInvariant());
    }

    /// <summary>A cue of the given kind, with the project's defaults on it and nowhere yet to live.</summary>
    private CueNode NewCue(CueKind kind, string mediaPath = "")
    {
        CueNode cue = kind switch
        {
            CueKind.Group => new GroupCueNode { Label = "Group" },
            CueKind.Action => new ActionCueNode { Label = "Action" },
            CueKind.Fade => new FadeCueNode { Label = "Fade" },
            CueKind.Jump => new JumpCueNode { Label = "Jump" },
            CueKind.Patch => new PatchCueNode { Label = "Patch" },
            CueKind.Visualizer => new VisualizerCueNode { Label = "Visualizer" },
            CueKind.Comment => new CommentCueNode { Label = "" },
            CueKind.Text => new TextCueNode { Label = "Text", Text = "" },
            _ => new MediaCueNode
            {
                Label = mediaPath.Length > 0
                    ? Path.GetFileNameWithoutExtension(mediaPath)
                    : "Media",
                MediaPath = mediaPath,
                FadeInMs = Math.Max(0, Project.Settings.DefaultFadeInMs),
                FadeOutMs = Math.Max(0, Project.Settings.DefaultFadeOutMs),
            },
        };

        return cue;
    }

    /// <summary>
    /// Adds a cue that plays a SOURCE — an NDI camera, a capture device, a prepared YouTube video.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ordinary media cue with a URI where the filename goes, because everything else about it is
    /// the same: level, sends, placements, fades and effects all mean exactly what they mean for a
    /// file, and a separate cue type would fork all of them to change one field.
    /// </para>
    /// <para>
    /// Pre-roll is off for a LIVE source. Pre-roll opens the next few cues' media early so the next GO
    /// is instant — which for a camera means claiming the network connection, and for a capture device
    /// means claiming the device, minutes before anybody asked. The cue still fires normally; it opens
    /// at the moment it is fired, which is when an operator expects a camera to go live.
    /// </para>
    /// </remarks>
    /// <param name="durationMs">What the source said it runs for, or 0 when it cannot say.</param>
    public MediaCueNode? AddSourceCue(
        string uri,
        string label,
        int durationMs = 0,
        IReadOnlyList<SubtitleSelection>? subtitles = null)
    {
        if (!CanEditDocument || ScopedList is not { } list || uri.Trim().Length == 0)
            return null;

        var trimmed = uri.Trim();

        var cue = new MediaCueNode
        {
            Label = label.Trim().Length > 0 ? label.Trim() : SourceUri.Describe(trimmed),
            MediaPath = trimmed,
            SourceDurationMs = Math.Max(0, durationMs),
            FadeInMs = Math.Max(0, Project.Settings.DefaultFadeInMs),
            FadeOutMs = Math.Max(0, Project.Settings.DefaultFadeOutMs),
            DisablePreRoll = SourceUri.IsLive(trimmed),
            Subtitles = CopySubtitles(subtitles),
        };

        return Insert(list, cue, "source") as MediaCueNode;
    }

    /// <summary>
    /// Points an existing cue at a different source, keeping everything else about it.
    /// </summary>
    /// <remarks>
    /// The edit path for the same dialogs. Re-adding the cue would lose its number, its placements and
    /// its sends — which is the whole cue — so changing which camera it watches is one field.
    /// </remarks>
    public bool SetSource(
        Guid cueId,
        string uri,
        string label,
        int durationMs = 0,
        IReadOnlyList<SubtitleSelection>? subtitles = null)
    {
        if (!CanEditDocument
            || Project.FindCue(cueId) is not MediaCueNode cue
            || uri.Trim().Length == 0)
            return false;

        var trimmed = uri.Trim();

        using (_journal.Composite("edit source", "cues"))
        {
            _journal.Do(new SetValueCommand<string>(
                cueId, "mediaPath", "cues",
                () => cue.MediaPath, value => cue.MediaPath = value, trimmed, "edit source"));

            if (label.Trim().Length > 0)
                _journal.Do(new SetValueCommand<string>(
                    cueId, "label", "cues",
                    () => cue.Label, value => cue.Label = value, label.Trim(), "rename cue"));

            _journal.Do(new SetValueCommand<int>(
                cueId, "sourceDuration", "cues",
                () => cue.SourceDurationMs, value => cue.SourceDurationMs = value,
                Math.Max(0, durationMs), "edit source"));

            // null means "this source editor did not manage subtitles"; an empty list is an explicit
            // choice to remove the old subtitle. Keeping this inside the same composite makes editing
            // a prepared YouTube source one undoable operation.
            if (subtitles is not null)
                _journal.Do(new SetValueCommand<List<SubtitleSelection>>(
                    cueId, "subtitles", "cues",
                    () => cue.Subtitles, value => cue.Subtitles = value,
                    CopySubtitles(subtitles), "edit source subtitles"));
        }

        Refresh();
        return true;
    }

    private static List<SubtitleSelection> CopySubtitles(IReadOnlyList<SubtitleSelection>? subtitles) =>
        subtitles is null
            ? []
            : [.. subtitles.Select(item => new SubtitleSelection
            {
                Path = item.Path,
                StreamIndex = item.StreamIndex,
                Signature = item.Signature,
            })];

    /// <summary>
    /// The selected cue, when it plays a SOURCE rather than a file.
    /// </summary>
    /// <remarks>
    /// Null for a file cue, which has no source dialog to reopen — its media is chosen with the file
    /// picker, and offering "edit source…" over it would open a box that could not describe it.
    /// </remarks>
    public MediaCueNode? SelectedSourceCue =>
        SelectedCue is { } row && Project.FindCue(row.Id) is MediaCueNode media
        && SourceUri.IsSource(media.MediaPath)
            ? media
            : null;

    public bool CanEditSource => SelectedSourceCue is not null;

    /// <summary>What kind of source the selection is, so the view opens the right dialog.</summary>
    public SourceKind SelectedSourceKind => SourceUri.KindOf(SelectedSourceCue?.MediaPath);

    /// <summary>Numbers a new cue, files it where the operator is looking, and selects it.</summary>
    private CueNode Insert(CueList list, CueNode cue, string what)
    {
        InsertCore(list, cue, what);
        Refresh();
        RestoreSelection(AllRows.FirstOrDefault(row => row.Id == cue.Id));
        return cue;
    }

    /// <summary>
    /// The DOCUMENT half of an insert, without the view rebuild.
    /// </summary>
    /// <remarks>
    /// Split out for bulk import. <see cref="Refresh"/> rebuilds the scope list, the whole cue tree,
    /// the inspector and the timeline, and running it once per file made importing an album quadratic
    /// — measured at 5 ms a file into a small list and 26 ms a file into a large one, all of it on the
    /// UI thread. A caller adding many cues rebuilds once at the end.
    /// </remarks>
    /// <param name="after">
    /// The cue the new one follows, for a run that is adding several. Null means "where the operator
    /// is looking", which is the single-cue case and cannot be reused mid-run: without a refresh the
    /// selection has not moved, so every file would land in the same place and the album would come
    /// out backwards.
    /// </param>
    /// <param name="renumber">
    /// False for a bulk run, which renumbers ONCE at the end instead. Auto-renumber rewrites the whole
    /// sibling level, so doing it per file is the other half of what made importing an album quadratic.
    /// </param>
    private CueNode InsertCore(
        CueList list, CueNode cue, string what, CueNode? after = null, bool renumber = true)
    {
        cue.Trigger = Project.Settings.NewCueTrigger;

        var (siblings, at) = InsertionPoint(list, after);
        cue.Number = AutoNumber(siblings, at);

        using (_journal.Composite($"add {what} cue", "cues"))
        {
            _journal.Do(new AddItemCommand<CueNode>(siblings, cue, at, "cues", $"add {what} cue"));

            if (renumber && Project.Settings.AutoRenumberOnInsert)
                Renumber(list, siblings);
        }

        return cue;
    }

    /// <summary>Adds one media cue per file, as one undo step.</summary>
    /// <remarks>
    /// ONE rebuild for the whole run, and each cue anchored on the one before it so the files land in
    /// the order they were chosen. Rebuilding per file was quadratic in the size of the list.
    /// </remarks>
    public void AddMedia(IReadOnlyList<string> paths)
    {
        if (!CanEditDocument || paths.Count == 0 || ScopedList is not { } list)
            return;

        CueNode? last = null;
        var failed = new List<string>();

        // Quiet: an import is a batch, not a gesture. Every journal command otherwise re-runs the
        // shell's whole project status pass, so a hundred files cost a hundred validation passes over
        // a project growing under each one.
        using (_journal.Composite(
            paths.Count == 1 ? "add media cue" : $"add {paths.Count} media cues", "cues", quiet: true))
        {
            MediaImportProblem = "";

            foreach (var path in paths)
            {
                try
                {
                    var cue = NewCue(CueKind.Media, ImportMedia(path));

                    // A still is an ordinary media cue that would otherwise vanish after one frame:
                    // the decoder delivers a single picture and the clip reaches its end immediately.
                    // Holding it is what makes a title card a title card, so an imported image starts
                    // that way rather than needing the operator to discover the setting.
                    if (cue is MediaCueNode still && IsStill(path))
                        still.EndBehavior = CueEndBehavior.FreezeLastFrame;

                    last = InsertCore(list, cue, "media", last, renumber: false);
                }
                catch (Exception failure) when (
                    failure is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failed.Add($"{Path.GetFileName(path)} — {failure.Message}");
                }
            }

            // Once for the whole run. Auto-renumber rewrites every sibling, so per file it is O(list)
            // work repeated per file — the same quadratic the rebuild was.
            if (last is not null && Project.Settings.AutoRenumberOnInsert)
                Renumber(list, Owner(list.Cues, last.Id) ?? list.Cues);
        }

        // Every failure, not just the last one: importing forty files and being told about one is how
        // an operator discovers the other three at the top of a show.
        MediaImportProblem = failed.Count switch
        {
            0 => "",
            1 => $"could not import {failed[0]}",
            _ => $"could not import {failed.Count} files — {string.Join("; ", failed)}",
        };

        Refresh();

        if (last is not null)
            RestoreSelection(AllRows.FirstOrDefault(row => row.Id == last.Id));
    }

    /// <summary>
    /// Whether a path is a still picture rather than something with a duration.
    /// </summary>
    /// <remarks>
    /// By EXTENSION, because this runs at import — before anything has probed the file, and the point
    /// is to get the new cue's default right at the moment it is created. A wrong guess costs one
    /// setting the operator can change; probing first would cost them the wait.
    /// </remarks>
    private static bool IsStill(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff";

    private string ImportMedia(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = MediaPaths.RootOf(Project, ProjectPath?.Invoke());

        if (root is null)
            return absolute;

        root = Path.GetFullPath(root);
        if (IsWithin(absolute, root))
            return MediaPaths.Store(Project, absolute, ProjectPath?.Invoke());

        if (Project.Settings.OutsideMedia == OutsideMediaPolicy.KeepInPlace)
            return absolute;

        Directory.CreateDirectory(root);
        var target = AvailableDestination(root, Path.GetFileName(absolute));

        if (Project.Settings.OutsideMedia == OutsideMediaPolicy.MoveToRoot)
            File.Move(absolute, target);
        else
            File.Copy(absolute, target);

        return MediaPaths.Store(Project, target, ProjectPath?.Invoke());
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string AvailableDestination(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target))
            return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            target = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(target))
                return target;
        }
    }

    /// <summary>
    /// Renumbers the level a cue was just inserted into, under whatever owns it.
    /// </summary>
    /// <remarks>
    /// Through <see cref="CueRenumber"/>, which is also what the Renumber dialog runs. This used to
    /// assign bare integers at every depth, so adding one cue inside a group rewrote its children from
    /// <c>1.1, 1.2, 1.3</c> to <c>1, 2, 3</c> — numbers that then collided with the top level. The
    /// prefix is what keeps a dotted show dotted.
    /// </remarks>
    private void Renumber(CueList list, List<CueNode> siblings) =>
        CueRenumber.Apply(_journal, siblings, ParentOf(list.Cues, siblings)?.Number ?? CueNumber.Empty);

    /// <summary>The group whose children this list IS, or null when it is a cue list's top level.</summary>
    private static GroupCueNode? ParentOf(IReadOnlyList<CueNode> cues, List<CueNode> children)
    {
        foreach (var group in cues.OfType<GroupCueNode>())
        {
            if (ReferenceEquals(group.Children, children))
                return group;

            if (ParentOf(group.Children, children) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Removes every selected cue — a group takes its children with it.</summary>
    public void RemoveSelected()
    {
        var selected = Inspector.Selected;
        if (!CanEditDocument || selected.Count == 0 || ScopedList is not { } list)
            return;

        using (_journal.Composite(
            selected.Count == 1 ? "remove cue" : $"remove {selected.Count} cues", "cues",
            quiet: true))
        {
            foreach (var cue in selected)
            {
                if (Owner(list.Cues, cue.Id) is { } owner)
                    _journal.Do(new RemoveItemCommand<CueNode>(owner, cue, "cues", "remove cue"));
            }
        }

        Refresh();
    }

    /// <summary>
    /// Copies the selected cues in place.
    /// </summary>
    /// <remarks>
    /// New IDS all the way down — a duplicated group whose children kept their ids would be two cues
    /// claiming to be the same cue, and every reference to one would reach both.
    /// </remarks>
    public void DuplicateSelected()
    {
        var selected = Inspector.Selected;
        if (!CanEditDocument || selected.Count == 0 || ScopedList is not { } list)
            return;

        using (_journal.Composite(
            selected.Count == 1 ? "duplicate cue" : $"duplicate {selected.Count} cues", "cues",
            quiet: true))
        {
            foreach (var cue in selected)
            {
                if (Owner(list.Cues, cue.Id) is not { } owner)
                    continue;

                var at = owner.IndexOf(cue) + 1;
                var copy = Copy(cue);
                copy.Number = AutoNumber(owner, at);

                _journal.Do(new AddItemCommand<CueNode>(
                    owner, copy, at, "cues", $"duplicate {cue.Label}"));
            }
        }

        Refresh();
    }

    /// <summary>Where a new cue goes: inside the selected group, or after the selected cue.</summary>
    /// <param name="after">
    /// An explicit anchor, for a bulk run. A group anchor is only descended INTO when it is the
    /// operator's selection — a run anchored on the cue it just added must stay beside it.
    /// </param>
    private (List<CueNode> Siblings, int At) InsertionPoint(CueList list, CueNode? after = null)
    {
        if (after is { } previous)
        {
            var host = Owner(list.Cues, previous.Id) ?? list.Cues;
            return (host, host.IndexOf(previous) + 1);
        }

        if (SelectedCue is not { } row || Project.FindCue(row.Id) is not { } selected)
            return (list.Cues, list.Cues.Count);

        if (selected is GroupCueNode group)
            return (group.Children, group.Children.Count);

        var owner = Owner(list.Cues, selected.Id) ?? list.Cues;
        return (owner, owner.IndexOf(selected) + 1);
    }

    /// <summary>The list a cue actually lives in, at whatever depth.</summary>
    private static List<CueNode>? Owner(List<CueNode> cues, Guid id)
    {
        if (cues.Any(cue => cue.Id == id))
            return cues;

        foreach (var group in cues.OfType<GroupCueNode>())
        {
            if (Owner(group.Children, id) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// A number that sits where the cue was inserted.
    /// </summary>
    /// <remarks>
    /// Between its neighbours when there is room (12 and 13 → 12.5), otherwise one past the one
    /// before. A new cue numbered 0 at the bottom of the list is the thing auto-renumber exists to
    /// prevent (register item 20).
    /// </remarks>
    private static CueNumber AutoNumber(IReadOnlyList<CueNode> siblings, int at)
    {
        var before = at > 0 && at - 1 < siblings.Count ? siblings[at - 1].Number : CueNumber.Empty;
        var after = at < siblings.Count ? siblings[at].Number : CueNumber.Empty;

        if (before.IsEmpty)
            return after.IsEmpty ? new CueNumber("1") : after.Child(1);

        // Room between them: take a decimal step down from the one before.
        if (!after.IsEmpty && before.CompareTo(after) < 0)
            return Between(before, after);

        var segments = before.Text.Split('.');
        return int.TryParse(segments[^1], out var last)
            ? new CueNumber(string.Join('.', segments[..^1].Append((last + 1).ToString())))
            : before.Child(1);
    }

    /// <summary>
    /// A number strictly between two neighbours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious answer is a child of the cue before: 12 and 13 give 12.1. When that child is already
    /// the cue AFTER — 12 followed by 12.1 — the level is full, and the answer is one level deeper:
    /// 12.0.1 still sorts between them.
    /// </para>
    /// <para>
    /// It used to return <paramref name="before"/> itself when the level was full, which gave the new
    /// cue the SAME number as the one above it. Two cues answering to Q12 is the one thing a cue number
    /// exists to prevent, and nothing downstream reported it.
    /// </para>
    /// <para>
    /// The descent always terminates while <paramref name="before"/> sorts below
    /// <paramref name="after"/>: each step moves the candidate strictly closer to
    /// <paramref name="before"/>, so it passes below <paramref name="after"/> within that number's
    /// depth. The bound is a guard against a malformed number, not a real limit.
    /// </remarks>
    private static CueNumber Between(CueNumber before, CueNumber after)
    {
        var candidate = before.Child(1);

        for (var depth = 0; depth < 16 && candidate >= after; depth++)
        {
            before = before.Child(0);
            candidate = before.Child(1);
        }

        return candidate;
    }

    private static CueNode Copy(CueNode cue)
    {
        // Cue records contain mutable lists several levels deep. A record `with` only copies the
        // record shell, so changing a duplicated send, subtitle, placement, curve or lane used to
        // change the original as well. The project serializer is already the canonical deep-copy
        // boundary used by the runtime; use the same boundary here, then give every local identity a
        // new id.
        var envelope = new HaCueProject
        {
            CueLists = [new CueList { Cues = [cue] }],
        };
        var copy = ProjectSnapshot.Copy(envelope).CueLists[0].Cues[0];
        var cueIds = new Dictionary<Guid, Guid>();

        void Renew(CueNode node)
        {
            var oldId = node.Id;
            node.Id = Guid.NewGuid();
            cueIds[oldId] = node.Id;

            var lanes = node switch
            {
                MediaCueNode media => media.EffectLanes,
                TextCueNode text => text.EffectLanes,
                VisualizerCueNode visualizer => visualizer.EffectLanes,
                GroupCueNode group => group.EffectLanes,
                _ => [],
            };
            foreach (var lane in lanes)
                lane.Id = Guid.NewGuid();

            foreach (var placement in CuePlacements.Of(node))
                foreach (var section in placement.VideoFx)
                    section.Id = Guid.NewGuid();

            if (node is GroupCueNode groupNode)
                foreach (var child in groupNode.Children)
                    Renew(child);
        }

        Renew(copy);

        // References that stay inside the duplicated subtree should stay inside the copy. References
        // to cues elsewhere in the show deliberately remain external.
        foreach (var node in Flatten(copy))
        {
            if (node is JumpCueNode jump)
                jump.TargetCueIds = [.. jump.TargetCueIds.Select(id => cueIds.GetValueOrDefault(id, id))];
            if (node is FadeCueNode fade)
                fade.TargetCueIds = [.. fade.TargetCueIds.Select(id => cueIds.GetValueOrDefault(id, id))];
        }

        return copy;

        static IEnumerable<CueNode> Flatten(CueNode root)
        {
            yield return root;

            if (root is not GroupCueNode group)
                yield break;

            foreach (var child in group.Children)
                foreach (var descendant in Flatten(child))
                    yield return descendant;
        }
    }

    /// <summary>
    /// Disables or re-enables the selected cues for this performance.
    /// </summary>
    /// <remarks>
    /// A disabled cue stays VISIBLE and struck through, and GO steps over it. Deleting a cue to drop
    /// it for one night is how shows lose cues.
    /// </remarks>
    public void ToggleEnabled()
    {
        var selected = Inspector.Selected;
        if (!CanEditDocument || selected.Count == 0)
            return;

        var enable = selected.Any(cue => !cue.Enabled);

        using (_journal.Composite(enable ? "enable cues" : "disable cues", "cues", quiet: true))
        {
            foreach (var cue in selected)
            {
                var target = cue;
                _journal.Do(new SetValueCommand<bool>(
                    target.Id, "enabled", "cues",
                    () => target.Enabled, value => target.Enabled = value, enable,
                    enable ? "enable" : "disable for this performance"));
            }
        }

        Refresh();
    }

    /// <summary>
    /// Re-reads only what moves continuously — the Active panel's clocks and the pause latch.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Refresh"/> because it runs four times a second: rebuilding the whole
    /// cue tree at that rate would drop the selection under the operator's pointer every 250 ms.
    /// </remarks>
    public void Tick()
    {
        ActiveCues.Clear();

        foreach (var row in _runtime.ActiveCues)
            ActiveCues.Add(row);

        RebuildActivePanel();

        OnPropertyChanged(nameof(ActivePanelHint));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(IsPreviewing));
        OnPropertyChanged(nameof(PreviewHint));
        OnPropertyChanged(nameof(ShowsTransportHint));
        OnPropertyChanged(nameof(ShowsPreviewHint));
    }

    /// <summary>Called when the document changes under us — an undo, or an edit from another view.</summary>
    public void Refresh()
    {
        // The WHOLE selection, lead first — not just the lead. An edit applied to eleven selected cues
        // refreshes, and a refresh that remembered one of them left the next edit landing on one cue.
        var selected = new List<Guid>();

        if (SelectedCue?.Id is { } lead)
            selected.Add(lead);

        selected.AddRange(CueSource.RowSelection!.SelectedItems
            .OfType<CueRow>()
            .Select(row => row.Id)
            .Where(id => id != SelectedCue?.Id));

        // Scopes FIRST: the rows are built for whichever scope is selected, and a scope whose group was
        // just deleted has to be resolved before anything tries to list its contents.
        RebuildScopes();
        Rebuild();
        // By ID, not by reference: Rebuild replaces every row object, so the old instance is gone even
        // though the cue it stood for is still there.
        RestoreSelection(selected);
        Inspector.Facts = FactsFor(SelectedCue);
        Inspector.Reload();
        Timeline.Refresh();
        Tick();
        OnPropertyChanged(nameof(TreeHint));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ListSelector));
        OnPropertyChanged(nameof(CanOpenTimeline));
        OnPropertyChanged(nameof(CanEditDocument));
        OnPropertyChanged(nameof(CanSeekActive));
        OnPropertyChanged(nameof(SeekLockLabel));
        OnPropertyChanged(nameof(SeekLockHint));
        OnPropertyChanged(nameof(CanModifySelection));
        OnPropertyChanged(nameof(ToggleEnabledLabel));
    }

    private void Rebuild()
    {
        // The selection model addresses rows by INDEX PATH into this collection, and it does not learn
        // that they have gone. Clearing it AFTER the rows were replaced left it walking paths into a
        // tree that no longer had them, and it threw ArgumentOutOfRangeException out of the
        // selection-changed handler.
        //
        // That is worse than it sounds: a scope change is driven by a two-way binding, so the throw
        // surfaced inside a binding setter — where Avalonia turns it into a validation error and the
        // rest of the refresh silently never ran. Clearing first is safe because the paths are still
        // valid at this point.
        CueSource.RowSelection!.Clear();
        Cues.Clear();

        var rows = SelectedScope switch
        {
            { IsList: true } scope when Project.CueLists.FirstOrDefault(l => l.Id == scope.Id) is { } list =>
                CuePresentation.Rows(list, Project, _runtime),
            { IsList: false } scope when Project.FindCue(scope.Id) is { } cue =>
                CuePresentation.Subtree(cue, Project, _runtime),
            _ => [],
        };

        foreach (var row in rows)
            Cues.Add(row);
    }

    public bool IsEmpty => Cues.Count == 0;

    public bool IsScoped => SelectedScope is { IsList: false };

    public string Breadcrumb => SelectedScope is null
        ? "no list"
        : IsScoped
            ? $"{ListNameOf(SelectedScope.Id)}  ›  {SelectedScope.Name}"
            : $"{SelectedScope.Name}  ›  all cues";

    public string TreeHint
    {
        get
        {
            var total = Project.AllCues().Count();
            // Counted over the whole tree, not the top level: after the move to a hierarchy, Cues.Count
            // is the number of ROOTS, and "3 cues in scope" for a scope holding thirty would be a lie.
            var shown = AllRows.Count();

            return IsScoped
                ? $"{shown} cues in scope · {total} in show"
                : $"{shown} cues · {total} in show";
        }
    }

    // ── scope (right panel, Lists & groups tab) ───────────────────────────────────────────────

    /// <summary>
    /// Every list and group the tree can be scoped to.
    /// </summary>
    /// <remarks>
    /// Rebuilt by <see cref="Refresh"/> rather than captured once. It used to be built in the
    /// constructor and never again, so adding, removing or renaming a group left the navigator
    /// describing a show that no longer existed until the app was restarted.
    /// </remarks>
    private IReadOnlyList<ScopeEntry> _scopes;

    public IReadOnlyList<ScopeEntry> Scopes => _scopes;

    public IReadOnlyList<ScopeEntry> CueLists => [.. _scopes.Where(scope => scope.IsList)];

    public IReadOnlyList<ScopeEntry> Groups => [.. _scopes.Where(scope => !scope.IsList)];

    /// <summary>Whose groups the navigator is listing, for its own heading.</summary>
    public string GroupsHeader =>
        ScopedList is { } list ? $"GROUPS IN {list.Name.ToUpperInvariant()}" : "GROUPS";

    /// <summary>
    /// Re-reads the scope roots, keeping the operator where they were.
    /// </summary>
    /// <remarks>
    /// Selection is restored BY ID: every entry is a new record, so the old instance no longer matches
    /// anything even though the list or group it stood for is still there. A scope whose group was
    /// deleted falls back to that group's list rather than to nothing, because dropping the operator at
    /// the show root mid-edit loses their place for no reason.
    /// </remarks>
    private void RebuildScopes()
    {
        var wanted = SelectedScope?.Id;
        _scopes = BuildScopes();

        OnPropertyChanged(nameof(Scopes));
        OnPropertyChanged(nameof(CueLists));
        OnPropertyChanged(nameof(Groups));

        // Through the property, so the tree, the breadcrumb and the hints all follow it. Assigning the
        // backing field would leave the navigator's highlight pointing at a row nobody is looking at.
        SelectedScope = _scopes.FirstOrDefault(scope => scope.Id == wanted)
                        ?? _scopes.FirstOrDefault(scope => scope.IsList);

        OnPropertyChanged(nameof(GroupsHeader));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScoped))]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    [NotifyPropertyChangedFor(nameof(TreeHint))]
    [NotifyPropertyChangedFor(nameof(ActivePanelHint))]
    [NotifyPropertyChangedFor(nameof(ListSelector))]
    [NotifyPropertyChangedFor(nameof(GroupsHeader))]
    private ScopeEntry? _selectedScope;

    partial void OnSelectedScopeChanged(ScopeEntry? value)
    {
        SyncActiveCueList();
        Rebuild();
        SelectedCue = null;
        OnPropertyChanged(nameof(TreeHint));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Every list and every group, as scope roots.
    /// </summary>
    /// <remarks>
    /// The tallies are counts of the real subtree, so a group that gains a cue gains a number here
    /// without anyone updating a string. Groups are indented by depth for the same reason the tree is.
    /// </remarks>
    private IReadOnlyList<ScopeEntry> BuildScopes()
    {
        var entries = new List<ScopeEntry>();

        foreach (var list in Project.CueLists)
        {
            entries.Add(new ScopeEntry(list.Id, list.Name, list.Flatten().Count(), IsList: true, 0));

            foreach (var (group, depth) in GroupsIn(list.Cues, 0))
                entries.Add(new ScopeEntry(
                    group.Id,
                    $"{CuePresentation.Number(group.Number)} · {group.Label}",
                    CountIn(group),
                    IsList: false,
                    depth));
        }

        return entries;
    }

    private static IEnumerable<(GroupCueNode Group, int Depth)> GroupsIn(
        IEnumerable<CueNode> cues, int depth)
    {
        foreach (var cue in cues)
        {
            if (cue is not GroupCueNode group)
                continue;

            yield return (group, depth);
            foreach (var nested in GroupsIn(group.Children, depth + 1))
                yield return nested;
        }
    }

    private static int CountIn(GroupCueNode group) =>
        group.Children.Count + group.Children.OfType<GroupCueNode>().Sum(CountIn);

    private string ListNameOf(Guid groupId) =>
        Project.CueLists.FirstOrDefault(list => list.Flatten().Any(cue => cue.Id == groupId))?.Name
        ?? "show";

    // ── the Active panel ──────────────────────────────────────────────────────────────────────
    // Scope is a view filter, never a transport boundary: this list always shows everything sounding.
    public ObservableCollection<ActiveCueRow> ActiveCues { get; }

    /// <summary>
    /// The same cues, with a group's sounding children gathered under one header.
    /// </summary>
    /// <remarks>
    /// A separate collection from <see cref="ActiveCues"/>, which stays flat because the transport
    /// reads it — bare STOP wants "the one running longest" and the seek wants a cue by id, and
    /// neither should have to walk a tree to find one.
    /// </remarks>
    public ObservableCollection<object> ActivePanelRows { get; } = [];

    /// <summary>Shows sounding cues without group headers when the operator prefers a flat run list.</summary>
    public bool FlatActiveList { get; set; }

    /// <summary>
    /// Rebuilds the panel, keeping each group's expander where the operator left it.
    /// </summary>
    /// <remarks>
    /// The rows are rebuilt four times a second, so a header replaced wholesale would spring back open
    /// — or shut — under the pointer every 250 ms. Only the EXPANDER is operator state; everything else
    /// on the row is a measurement and is meant to be replaced.
    /// </remarks>
    private void RebuildActivePanel()
    {
        var expansion = ActivePanelRows
            .OfType<ActiveGroupRow>()
            .ToDictionary(group => group.GroupId, group => group.IsExpanded);

        IReadOnlyList<object> rebuilt = FlatActiveList
            ? [.. ActiveCues.Cast<object>()]
            : CuePresentation.ActivePanel(Project, [.. ActiveCues], _runtime.MediaDurations);

        foreach (var group in rebuilt.OfType<ActiveGroupRow>())
        {
            if (expansion.TryGetValue(group.GroupId, out var open))
                group.IsExpanded = open;
        }

        ActivePanelRows.Clear();

        foreach (var row in rebuilt)
            ActivePanelRows.Add(row);
    }

    public string ActivePanelHint => IsScoped
        ? "includes cues outside the scope"
        : $"{ActiveCues.Count} sounding · scope never hides these";

    // ── the right column ──────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> RightTabs { get; } = [PropertiesTab, ListsTab];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPropertiesTab))]
    [NotifyPropertyChangedFor(nameof(RightPanelHeader))]
    [NotifyPropertyChangedFor(nameof(RightPanelHint))]
    private string _selectedRightTab = PropertiesTab;

    public bool IsPropertiesTab => SelectedRightTab == PropertiesTab;

    public string RightPanelHeader => IsPropertiesTab ? Inspector.Title : "Lists & groups";

    public string RightPanelHint =>
        IsPropertiesTab ? Inspector.KindLabel : "pick a root to scope the tree";

    public InspectorViewModel Inspector { get; }

    // ── the transport row ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Multi-list transport is v1 (register item 5): the transport acts on the SELECTED list, and each
    /// list keeps its own standby.
    /// </summary>
    public string ListSelector
    {
        get
        {
            var list = ScopedList;

            var standby = list?.StandbyCueId is { } id ? Project.FindCue(id) : null;
            return standby is null
                ? $"{list?.Name ?? "no list"} · at top ▾"
                : $"{list!.Name} · stby Q{CuePresentation.Number(standby.Number)} ▾";
        }
    }

    /// <summary>Switches the transport and tree to a cue list chosen from the transport itself.</summary>
    [RelayCommand]
    private void SelectTransportList(ScopeEntry? scope)
    {
        if (scope is { IsList: true })
            SelectedScope = _scopes.FirstOrDefault(candidate => candidate.Id == scope.Id) ?? scope;
    }

    public string ChaseReadout => _runtime.ChaseReadout;

    /// <summary>Register item 18 — a bottom sheet by default, undockable to its own window.</summary>
    [ObservableProperty]
    private bool _isTimelineOpen;

    /// <summary>Whether the selected cue is a group the timeline sheet can show.</summary>
    public bool CanOpenTimeline =>
        SelectedCue is { } row && Project.FindCue(row.Id) is GroupCueNode { FireMode: GroupFireMode.Timeline };

    /// <summary>
    /// Opens the sheet on the SELECTED group.
    /// </summary>
    /// <remarks>
    /// It used to draw whichever timeline group came first in the show, whoever was looking at what.
    /// A sheet that shows a different group from the one selected is worse than no sheet.
    /// </remarks>
    public void OpenTimeline()
    {
        if (SelectedCue is not { } row
            || Project.FindCue(row.Id) is not GroupCueNode { FireMode: GroupFireMode.Timeline } group)
            return;

        Timeline.Show(group);
        IsTimelineOpen = true;
    }

    public TimelineViewModel Timeline { get; }
}

/// <summary>A scope root: a cue list, or a group inside one.</summary>
/// <param name="Depth">Nesting, so a song inside "Songs" is indented under it.</param>
public sealed record ScopeEntry(Guid Id, string Name, int Count, bool IsList, int Depth) : INavRow
{
    public string Tally => Count.ToString(CultureInfo.CurrentCulture);
    public bool HasTally => true;

    // A scope tally is a count of cues, never a warning — those belong to the settings navs.
    public bool TallyIsBad => false;
    public bool TallyIsOverride => false;

    public Thickness Indent => new(12 + (Depth * 12), 0, 0, 0);
}

/// <summary>Screen 05 — the timeline sheet over whichever group is open.</summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    /// <summary>The snap the sheet's own hint promises, in milliseconds.</summary>
    private const int SnapMs = 500;

    private readonly HaCueProject _project;
    private readonly ShowRuntime _runtime;
    private readonly ProjectJournal? _journal;
    /// <summary>Which group the sheet is showing. Settable so the operator can point it somewhere.</summary>
    private GroupCueNode? _group;
    private IDisposable? _drag;

    /// <summary>
    /// The WINDOW's length as it was when the drag started, held for the whole gesture.
    /// </summary>
    /// <remarks>
    /// Dragging a clip to the right can extend the group, which re-clamps the window — and if each
    /// motion event divided by the new one, the lane would rescale under the pointer and the clip would
    /// never catch up with it. Fractions are read against the picture the operator grabbed.
    /// </remarks>
    private double _dragSpan;

    public TimelineViewModel(HaCueProject project, ShowRuntime runtime, ProjectJournal? journal = null)
    {
        _project = project;
        _runtime = runtime;
        _journal = journal;

        // The first timeline group in the show: what the sheet opens onto until a cue is chosen.
        Show(project.AllCues().OfType<GroupCueNode>()
            .FirstOrDefault(candidate => candidate.FireMode == GroupFireMode.Timeline));
    }

    /// <summary>
    /// Points the sheet at a group, or at nothing.
    /// </summary>
    /// <remarks>
    /// It used to draw whichever timeline group came first in the show, whoever was looking at what —
    /// and nothing could change that. A sheet showing a different group from the selected one is worse
    /// than no sheet.
    /// </remarks>
    public void Show(GroupCueNode? group)
    {
        _group = group;

        if (group is null)
        {
            Title = "Timeline";
            Hint = "no timeline group in this show";
            Lanes = [];
            Ruler = [];
            return;
        }

        Title = $"Timeline · Q{CuePresentation.Number(group.Number)} {group.Label}";
        Hint = HintFor(group);
        Refresh();
    }

    [ObservableProperty]
    private string _title = "Timeline";

    [ObservableProperty]
    private string _hint = "";

    /// <summary>
    /// Whether the sheet is in its own window.
    /// </summary>
    /// <remarks>
    /// Lives on the view-model rather than the window so the button's label is right in BOTH places:
    /// the docked sheet offers "undock" and the floating one offers "dock", and they are the same
    /// view-model, so the label has to come from the state rather than from which XAML is showing.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DockLabel))]
    private bool _isUndocked;

    public string DockLabel => IsUndocked ? "DOCK ↙" : "UNDOCK ↗";

    /// <summary>
    /// The cue view this sheet belongs to.
    /// </summary>
    /// <remarks>
    /// A back-reference rather than an ancestor walk, because once the sheet is UNDOCKED there is no
    /// CuesView above it — and "which cue is selected" and "put me back" are both questions only the
    /// cue view can answer.
    /// </remarks>
    public CuesViewModel? Owner { get; init; }
    [ObservableProperty]
    private IReadOnlyList<string> _ruler = [];

    [ObservableProperty]
    private IReadOnlyList<TimelineLane> _lanes = [];

    /// <summary>The snap/free segment picker in the transport row.</summary>
    public IReadOnlyList<string> SnapModes { get; } = ["snap", "free"];

    /// <summary>
    /// Whether drags land on the grid.
    /// </summary>
    /// <remarks>
    /// A VIEW setting, not a document one: it is how somebody is working right now, and carrying it to
    /// the next venue inside a show file would be carrying the wrong thing. Same reasoning as the
    /// appearance pane, and the reason it is not journaled either — undoing back through "I turned
    /// snapping off" would bury the edits the operator actually wants back.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSnapping))]
    private string _snapMode = "snap";

    public bool IsSnapping => SnapMode != "free";

    /// <summary>The sheet's hint reads what the grid is doing, so turning it off has to rewrite it.</summary>
    partial void OnSnapModeChanged(string value)
    {
        if (_group is { } group)
            Hint = HintFor(group);
    }

    private string HintFor(GroupCueNode group) =>
        $"{group.Children.Count} cues · "
        + (IsSnapping ? $"snap {SnapMs / 1000d:0.#} s" : "free") + " · zoom fit";

    /// <summary>
    /// A drag on a clip: moves it, or trims either end.
    /// </summary>
    /// <remarks>
    /// Dragging the LEFT edge moves the clip and trims the same amount into the file, so the frame
    /// under the cursor does not slide — the behaviour every timeline editor has, and the reason
    /// position and trim are separate numbers on the model.
    /// </remarks>
    public void ApplyClipGesture(ClipGesture gesture)
    {
        if (_group is null || _journal is null || _project.FindCue(gesture.SubjectId) is not { } cue)
            return;

        if (_drag is null)
        {
            _dragSpan = _view.LengthMs;
            _drag = _journal.Composite(
                gesture.Edge == ClipEdge.Body ? "move clip" : "trim clip", "cues");
        }

        // Fractions of the WINDOW, so a drag means the same distance on screen however far in the
        // operator has zoomed — which is the whole reason to zoom before making a fine adjustment.
        var left = Snap(Math.Max(0, _view.StartMs + (gesture.Left * _dragSpan)));
        var width = Snap(Math.Max(0, gesture.Width) * _dragSpan);

        switch (gesture.Edge)
        {
            case ClipEdge.Body:
                SetOffset(cue, left);
                break;

            case ClipEdge.Start when cue is MediaCueNode media:
                // The clip's left edge and the media's in-point move together by the same amount.
                var moved = left - cue.TimelineOffsetMs;
                SetOffset(cue, left);
                SetTrim(media, "trimIn", () => media.TrimInMs, value => media.TrimInMs = value,
                    (int)Math.Max(0, media.TrimInMs + moved), "trim clip start");
                break;

            case ClipEdge.End when cue is MediaCueNode media2:
                SetTrim(media2, "trimOut", () => media2.TrimOutMs, value => media2.TrimOutMs = value,
                    (int)Math.Max(SnapMs, media2.TrimInMs + width), "trim clip end");
                break;

            // A group or an action cue has no file to trim, so an edge drag on one moves it instead of
            // silently doing nothing.
            default:
                SetOffset(cue, left);
                break;
        }

        Refresh();
    }

    /// <summary>Ends the gesture, closing its undo step.</summary>
    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    /// <summary>
    /// Ducks the selected clip under everything else that overlaps it.
    /// </summary>
    /// <remarks>
    /// An AUTHORING helper: it writes ordinary keyframes into the bed's own volume lane, so the
    /// operator can see exactly what will happen and drag it afterwards. A live side-chain would be
    /// invisible during the show, which is when it matters.
    /// </remarks>
    public PromptViewModel? Duck(Guid bedId)
    {
        // The bed must be IN this group. FindCue searches the whole show, and ducking a cue that is
        // not on this timeline under cues that are is a sentence with no meaning.
        if (_group is null
            || _journal is null
            || _group.Children.FirstOrDefault(child => child.Id == bedId) is not MediaCueNode bed)
            return null;

        var voices = _group.Children
            .Where(child => child.Id != bedId && child is MediaCueNode or GroupCueNode)
            .Select(Span)
            .Where(span => span.LengthMs > 0)
            .ToList();

        if (voices.Count == 0)
            return null;

        return new PromptViewModel(
            $"Duck “{bed.Label}”",
            $"under {voices.Count} overlapping cue(s) · writes keyframes you can drag",
            [
                new PromptField { Label = "Depth", Kind = PromptFieldKind.Number, Value = "-12", Hint = "dB" },
                new PromptField { Label = "Ramp", Kind = PromptFieldKind.Number, Value = "500", Hint = "ms each side" },
                new PromptField
                {
                    Label = "Lead",
                    Kind = PromptFieldKind.Number,
                    Value = "250",
                    Hint = "ms · how early it dips and how late it recovers",
                },
            ],
            prompt =>
            {
                var lane = bed.EffectLanes.FirstOrDefault(item => item.Kind == EffectLaneKind.Volume);
                var span = Span(bed);

                var ducked = DuckMath.ApplyDucks(
                    lane?.Points ?? [],
                    span.StartMs,
                    span.LengthMs,
                    voices,
                    prompt["Depth"].Decimal(-12),
                    prompt["Ramp"].Number(500),
                    prompt["Lead"].Number(250));

                using var scope = _journal.Composite($"duck “{bed.Label}”", "cues");

                if (lane is null)
                {
                    // The bed has no volume lane yet. Adding one is part of the same edit — an undo
                    // that left an empty lane behind would leave the cue changed in a way nobody asked
                    // for (register item 18: lanes are hidden until added).
                    var added = new EffectLane { Kind = EffectLaneKind.Volume, Points = [.. ducked] };
                    _journal.Do(new AddItemCommand<EffectLane>(
                        bed.EffectLanes, added, bed.EffectLanes.Count, "cues", "add volume lane"));
                }
                else
                {
                    var target = lane;
                    _journal.Do(new SetValueCommand<List<LanePoint>>(
                        bed.Id, $"lane:{lane.Id}", "cues",
                        () => target.Points, points => target.Points = points, [.. ducked],
                        "duck under the voice-over"));
                }

                Refresh();
            },
            confirm: "DUCK");
    }

    /// <summary>Where a child sits on the group's timeline, and for how long.</summary>
    private TimelineSpan Span(CueNode cue)
    {
        var length = cue is MediaCueNode media
            ? media.TrimmedLength(_runtime.MediaDurations.TryGetValue(cue.Id, out var probed) ? probed : null)
            : cue is TextCueNode { DurationMs: > 0 } text
                ? TimeSpan.FromMilliseconds(text.DurationMs)
                : null;

        // An unprobed, untrimmed cue has no honest length. Eight seconds is the same nominal the
        // timeline DRAWS with, so the dip matches the block the operator is looking at.
        var lengthMs = (int)(length?.TotalMilliseconds ?? 8_000);

        return new TimelineSpan(cue.TimelineOffsetMs, cue.TimelineOffsetMs + lengthMs);
    }

    /// <summary>Re-reads the lanes and the ruler from the document.</summary>
    public void Refresh()
    {
        if (_group is null)
            return;

        // A clip dragged past the old end makes the group longer, so the window is re-clamped before
        // anything is drawn against it — a view wider than the group would draw the lanes squashed
        // into part of the sheet with nothing beside them.
        _view = Clamp(_view);

        Lanes = TimelinePresentation.Lanes(_group, _project, _runtime, _view);
        Ruler = TimelinePresentation.Ruler(_group, _runtime, _view);

        OnPropertyChanged(nameof(Playhead));
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    // ── the view window (screen 05's zoom controls) ───────────────────────────────────────────

    private TimelineView _view = TimelineView.Whole(60_000);

    /// <summary>The group's whole length, which is what FIT fits to and what zoom is bounded by.</summary>
    private double SpanMs => _group is { } group ? TimelinePresentation.SpanMs(group, _runtime) : 60_000;

    /// <summary>Keeps a window inside the group it is a window ONTO.</summary>
    private TimelineView Clamp(TimelineView view)
    {
        var span = Math.Max(TimelineView.MinimumLengthMs, SpanMs);
        var length = Math.Clamp(view.LengthMs, TimelineView.MinimumLengthMs, span);

        return new TimelineView(
            Math.Clamp(view.StartMs, 0, Math.Max(0, span - length)), length);
    }

    /// <summary>Halves what is on screen, about its centre.</summary>
    public void ZoomIn() => Show(_view.Zoom(0.5, SpanMs));

    /// <summary>Doubles what is on screen, about its centre.</summary>
    public void ZoomOut() => Show(_view.Zoom(2, SpanMs));

    /// <summary>The whole group, edge to edge.</summary>
    public void ZoomFit() => Show(TimelineView.Whole(SpanMs));

    private void Show(TimelineView view)
    {
        _view = Clamp(view);
        Refresh();
    }

    /// <summary>How much of the group is on screen, for the transport row.</summary>
    public string ZoomLabel =>
        _view.LengthMs >= SpanMs
            ? "fit"
            : $"{TimeSpan.FromMilliseconds(_view.LengthMs).TotalSeconds:0.#} s";

    // ── the playhead ──────────────────────────────────────────────────────────────────────────

    private double _playheadMs;

    /// <summary>
    /// Where the playhead sits IN THE WINDOW, as a fraction — which is what the sheet draws.
    /// </summary>
    /// <remarks>
    /// Was a runtime fact that nothing wrote, so it sat at zero forever. It is an AUTHORING position:
    /// where the operator has decided to start from, which is a question the show cannot answer.
    /// </remarks>
    public double Playhead => _view.Fraction(_playheadMs);

    /// <summary>Where the playhead is, in the group's own time.</summary>
    public TimeSpan PlayheadAt => TimeSpan.FromMilliseconds(_playheadMs);

    /// <summary>The group the sheet is showing, for the verbs that act on the whole of it.</summary>
    public GroupCueNode? Group => _group;

    /// <summary>
    /// The last refusal from the transport row, or nothing.
    /// </summary>
    /// <remarks>
    /// Held beside the buttons rather than shown in a dialog: "the show is not running" is the answer
    /// most of the time somebody presses ▶ on a laptop, and a modal for it is a modal they learn to
    /// dismiss without reading.
    /// </remarks>
    [ObservableProperty]
    private string _transportProblem = "";

    public string PlayheadLabel =>
        $"{(int)PlayheadAt.TotalMinutes}:{PlayheadAt.Seconds:00}.{PlayheadAt.Milliseconds / 100}";

    /// <summary>
    /// Puts the playhead where the operator clicked the ruler.
    /// </summary>
    /// <remarks>
    /// Snapped like a clip drag is, and by the same toggle: a playhead half a frame off a cue's start
    /// would play the first few milliseconds of a clip the operator meant to skip.
    /// </remarks>
    public void PlacePlayhead(double fractionOfWindow)
    {
        _playheadMs = Math.Clamp(Snap(_view.At(Math.Clamp(fractionOfWindow, 0, 1))), 0, SpanMs);

        OnPropertyChanged(nameof(Playhead));
        OnPropertyChanged(nameof(PlayheadLabel));
    }

    private void SetOffset(CueNode cue, double milliseconds) =>
        _journal!.Do(new SetValueCommand<int>(
            cue.Id, "timelineOffset", "cues",
            () => cue.TimelineOffsetMs, value => cue.TimelineOffsetMs = value,
            (int)Math.Max(0, milliseconds), "move clip"));

    private void SetTrim(
        MediaCueNode media, string property, Func<int> read, Action<int> write,
        int value, string description) =>
        _journal!.Do(new SetValueCommand<int>(
            media.Id, property, "cues", read, write, value, description));

    /// <summary>
    /// Rounds to the snap grid, unless the operator has turned snapping off.
    /// </summary>
    /// <remarks>
    /// Half a second is the right grid for laying a show out and the wrong one for landing a stab on a
    /// frame, which is why the toggle exists rather than a smaller constant: a grid fine enough for the
    /// second case does not help with the first.
    /// </remarks>
    private double Snap(double milliseconds) =>
        IsSnapping ? Math.Round(milliseconds / SnapMs) * SnapMs : Math.Round(milliseconds);
}
