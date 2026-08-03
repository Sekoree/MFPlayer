using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
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
        Rebuild();

        CueSource = BuildSource();
        CueSource.RowSelection!.SelectionChanged += (_, _) =>
        {
            // The tree is the authority on what is selected — SelectedCue follows it rather than the
            // other way round, so a click, a keyboard move and a programmatic set all take one path.
            SetProperty(ref _selectedCue, CueSource.RowSelection.SelectedItem, nameof(SelectedCue));
            Inspector.Facts = FactsFor(CueSource.RowSelection.SelectedItem);
            Inspector.Show([.. CueSource.RowSelection.SelectedItems.OfType<CueRow>().Select(row => row.Id)]);
        };

        SelectedCue = Cues.FirstOrDefault();
    }

    private HaCueProject Project => _journal.Project;

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
    public void Go()
    {
        if (ScopedList is not { } list)
            return;

        // With a session, GO is the session's: it fires standby, advances its own cursor and reports
        // back through the poll. Moving the cursor here as well would fight it.
        if (Engine is { } host)
        {
            _ = host.GoAsync(list);
            return;
        }

        // The same rule the running transport uses, from the same place: a cursor that behaved one way
        // with a session and another way without one is a rehearsal that does not match the show.
        SetStandby(list, CueOrder.NextEnabled(list, list.StandbyCueId)?.Id);
    }

    /// <summary>The running show, when there is one. Set by the shell after it starts the engine.</summary>
    public ShowHost? Engine { get; set; }

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

    /// <summary>Stops everything, over the project's stop fade. The split-menu half of STOP.</summary>
    public void StopAll() => _ = Engine?.StopAllAsync();

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

    /// <summary>Moves standby without firing — the ↑/↓ keys.</summary>
    public void StepStandby(int delta)
    {
        if (ScopedList is not { } list)
            return;

        var order = list.Flatten().Where(cue => cue.Enabled).ToList();
        if (order.Count == 0)
            return;

        var at = list.StandbyCueId is { } standby
            ? order.FindIndex(cue => cue.Id == standby)
            : -1;

        var next = Math.Clamp(at + delta, 0, order.Count - 1);
        SetStandby(list, order[next].Id);
    }

    /// <summary>
    /// Puts standby on the selected cue — the Esc key, and "move standby here".
    /// </summary>
    /// <remarks>
    /// Esc is "get me back to where I am looking", which during a show is the selected row. It never
    /// stops anything: there is a PANIC button for that, and an Esc that stopped the show because
    /// somebody reached for it out of habit would be unforgivable.
    /// </remarks>
    public void StandbyHere()
    {
        if (ScopedList is { } list && SelectedCue is { } row)
            SetStandby(list, row.Id);
    }

    private void SetStandby(CueList list, Guid? id)
    {
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
        if (ScopedList is not { } list)
            return null;

        CueNode cue = kind switch
        {
            CueKind.Group => new GroupCueNode { Label = "Group" },
            CueKind.Action => new ActionCueNode { Label = "Action" },
            CueKind.Fade => new FadeCueNode { Label = "Fade" },
            CueKind.Jump => new JumpCueNode { Label = "Jump" },
            CueKind.Patch => new PatchCueNode { Label = "Patch" },
            CueKind.Visualizer => new VisualizerCueNode { Label = "Visualizer" },
            CueKind.Comment => new CommentCueNode { Label = "" },
            _ => new MediaCueNode
            {
                Label = mediaPath.Length > 0
                    ? Path.GetFileNameWithoutExtension(mediaPath)
                    : "Media",
                MediaPath = mediaPath,
            },
        };

        var (siblings, at) = InsertionPoint(list);
        cue.Number = AutoNumber(siblings, at);

        _journal.Do(new AddItemCommand<CueNode>(
            siblings, cue, at, "cues", $"add {kind.ToString().ToLowerInvariant()} cue"));
        _journal.CloseGroup();

        Refresh();
        SelectedCue = AllRows.FirstOrDefault(row => row.Id == cue.Id);
        return cue;
    }

    /// <summary>Adds one media cue per file, as one undo step.</summary>
    public void AddMedia(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || ScopedList is null)
            return;

        using (_journal.Composite(
            paths.Count == 1 ? "add media cue" : $"add {paths.Count} media cues", "cues"))
        {
            foreach (var path in paths)
                AddCue(CueKind.Media, path);
        }

        Refresh();
    }

    /// <summary>Removes every selected cue — a group takes its children with it.</summary>
    public void RemoveSelected()
    {
        var selected = Inspector.Selected;
        if (selected.Count == 0 || ScopedList is not { } list)
            return;

        using (_journal.Composite(
            selected.Count == 1 ? "remove cue" : $"remove {selected.Count} cues", "cues"))
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
        if (selected.Count == 0 || ScopedList is not { } list)
            return;

        using (_journal.Composite(
            selected.Count == 1 ? "duplicate cue" : $"duplicate {selected.Count} cues", "cues"))
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
    private (List<CueNode> Siblings, int At) InsertionPoint(CueList list)
    {
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
            return before.Child(1) < after ? before.Child(1) : before;

        var segments = before.Text.Split('.');
        return int.TryParse(segments[^1], out var last)
            ? new CueNumber(string.Join('.', segments[..^1].Append((last + 1).ToString())))
            : before.Child(1);
    }

    private static CueNode Copy(CueNode cue)
    {
        var copy = cue with { Id = Guid.NewGuid() };

        if (copy is GroupCueNode group)
            group.Children = [.. group.Children.Select(Copy)];

        return copy;
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
        if (selected.Count == 0)
            return;

        var enable = selected.Any(cue => !cue.Enabled);

        using (_journal.Composite(enable ? "enable cues" : "disable cues", "cues"))
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

        OnPropertyChanged(nameof(ActivePanelHint));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(IsPreviewing));
        OnPropertyChanged(nameof(PreviewHint));
    }

    /// <summary>Called when the document changes under us — an undo, or an edit from another view.</summary>
    public void Refresh()
    {
        var selected = SelectedCue?.Id;

        // Scopes FIRST: the rows are built for whichever scope is selected, and a scope whose group was
        // just deleted has to be resolved before anything tries to list its contents.
        RebuildScopes();
        Rebuild();
        // By ID, not by reference: Rebuild replaces every row object, so the old instance is gone even
        // though the cue it stood for is still there.
        SelectedCue = AllRows.FirstOrDefault(row => row.Id == selected);
        Inspector.Facts = FactsFor(SelectedCue);
        Inspector.Reload();
        Timeline.Refresh();
        Tick();
        OnPropertyChanged(nameof(TreeHint));
        OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ListSelector));
        OnPropertyChanged(nameof(CanOpenTimeline));
    }

    private void Rebuild()
    {
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
    private ScopeEntry? _selectedScope;

    partial void OnSelectedScopeChanged(ScopeEntry? value)
    {
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
            var list = SelectedScope is { IsList: true } scope
                ? Project.CueLists.FirstOrDefault(item => item.Id == scope.Id)
                : Project.CueLists.FirstOrDefault();

            var standby = list?.StandbyCueId is { } id ? Project.FindCue(id) : null;
            return standby is null
                ? $"{list?.Name ?? "no list"} · at top ▾"
                : $"{list!.Name} · stby Q{CuePresentation.Number(standby.Number)} ▾";
        }
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
        if (SelectedCue is { } row && Project.FindCue(row.Id) is GroupCueNode group)
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
