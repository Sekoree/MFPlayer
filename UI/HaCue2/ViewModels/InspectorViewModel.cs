using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Machine;
using HaCue2.Controls;
using HaCue2.Presentation;
using HaCue2.Sample;
using S.Media.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// The cue editor in the right column (screen 02) and its per-kind variants (screen 04).
/// </summary>
/// <remarks>
/// <para>
/// Every field here WRITES THROUGH THE JOURNAL. There is no path from this panel to the document that
/// undo cannot reverse, which is the property the plan insists on: "a surface that mutates directly is
/// a surface whose undo silently does nothing".
/// </para>
/// <para>
/// The tab SET is a function of the cue kind and the selected tab is remembered PER KIND. An operator
/// working on sends all evening should not land on General every time they click a different media
/// cue, but should still land on Patch when they click a patch cue.
/// </para>
/// </remarks>
public partial class InspectorViewModel : ObservableObject
{
    private static readonly IReadOnlyList<string> NoTabs = [];
    private const string Mixed = "—";

    private readonly ProjectJournal _journal;

    /// <summary>Open for the duration of one canvas drag, so the gesture is a single undo step.</summary>
    private IDisposable? _drag;
    private readonly Dictionary<CueKind, string> _rememberedTab = [];
    private IReadOnlyList<Guid> _selection = [];

    public InspectorViewModel(ProjectJournal journal) => _journal = journal;

    private HaCueProject Project => _journal.Project;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>The lead cue — the one whose values the single-selection fields show.</summary>
    public CueNode? Cue => _selection.Count > 0 ? Project.FindCue(_selection[0]) : null;

    public IReadOnlyList<CueNode> Selected =>
        [.. _selection.Select(Project.FindCue).OfType<CueNode>()];

    [ObservableProperty]
    private IReadOnlyList<string> _tabs = NoTabs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralPane))]
    [NotifyPropertyChangedFor(nameof(IsAudioPane))]
    [NotifyPropertyChangedFor(nameof(IsVideoPane))]
    [NotifyPropertyChangedFor(nameof(IsEffectsPane))]
    [NotifyPropertyChangedFor(nameof(IsNotePane))]
    [NotifyPropertyChangedFor(nameof(IsPreviewPane))]
    [NotifyPropertyChangedFor(nameof(IsGroupPane))]
    [NotifyPropertyChangedFor(nameof(IsPatchPane))]
    [NotifyPropertyChangedFor(nameof(IsActionPane))]
    [NotifyPropertyChangedFor(nameof(IsFadePane))]
    [NotifyPropertyChangedFor(nameof(IsJumpPane))]
    [NotifyPropertyChangedFor(nameof(IsVisualizerPane))]
    private string? _selectedTab;

    public int SelectionCount => _selection.Count;
    public bool HasSelection => Cue is not null;
    public bool IsMultiSelection => SelectionCount > 1;

    public string Title => IsMultiSelection
        ? $"{SelectionCount} cues selected"
        : Cue is null
            ? "Cue properties"
            : $"Q{CuePresentation.Number(Cue.Number)} · {Cue.Label}";

    public string KindLabel => IsMultiSelection ? "multi-selection" : Cue is null
        ? "select a cue to edit"
        : KindOf() switch
        {
            CueKind.Media => "media cue",
            CueKind.Video => "media cue · video",
            CueKind.Group => "group",
            CueKind.Action => "action cue",
            CueKind.Fade => "fade cue",
            CueKind.Jump => "jump cue",
            CueKind.Visualizer => "visualizer cue",
            CueKind.Patch => "patch cue",
            _ => "comment",
        };

    /// <summary>
    /// Shows a selection. Reopening with nothing selected is specified behaviour, not a fallback: the
    /// panel never shows a stale cue after the selection is cleared (register item 7).
    /// </summary>
    public void Show(IReadOnlyList<Guid> cueIds)
    {
        if (SelectedTab is { } previous && Cue is { } old && SelectionCount <= 1)
            _rememberedTab[KindOf(old)] = previous;

        _selection = cueIds;
        Reload();
    }

    /// <summary>Re-reads the document after an edit or an undo, keeping the selection.</summary>
    public void Reload()
    {
        var lead = Cue;

        // The tab set for a multi-selection is the INTERSECTION of the kinds', so an edit can never
        // land on an arbitrary subset of what is selected.
        Tabs = lead is null
            ? NoTabs
            : Selected
                .Select(cue => TabsFor(KindOf(cue)))
                .Aggregate((IEnumerable<string>)TabsFor(KindOf(lead)), (common, next) => common.Intersect(next))
                .ToList();

        SelectedTab = lead is null
            ? null
            : _rememberedTab.TryGetValue(KindOf(lead), out var remembered) && Tabs.Contains(remembered)
                ? remembered
                // Second tab, not the first: General is the same on every kind, so landing on it tells
                // the operator nothing about what they just selected.
                : Tabs.Skip(1).FirstOrDefault() ?? Tabs.FirstOrDefault();

        OnPropertyChanged(nameof(Cue));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsMultiSelection));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(MultiSelectionNote));
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(LabelValue));
        OnPropertyChanged(nameof(LevelValue));
        OnPropertyChanged(nameof(FadeValue));
        OnPropertyChanged(nameof(FadeOutValue));
        OnPropertyChanged(nameof(LoopValue));
        OnPropertyChanged(nameof(TrimInValue));
        OnPropertyChanged(nameof(TrimOutValue));
        OnPropertyChanged(nameof(TriggerIndex));
        OnPropertyChanged(nameof(PreWaitValue));
        OnPropertyChanged(nameof(PostWaitValue));
        OnPropertyChanged(nameof(NoteValue));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(SendColumns));
        OnPropertyChanged(nameof(SendRows));
        OnPropertyChanged(nameof(RouteChain));
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(HasPlacement));
        OnPropertyChanged(nameof(PlacementList));
        OnPropertyChanged(nameof(PlacementNames));
        OnPropertyChanged(nameof(HasSeveralPlacements));
        OnPropertyChanged(nameof(PlacementCompositions));
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacityValue));
        OnPropertyChanged(nameof(EffectLanes));
        OnPropertyChanged(nameof(FadeInCurve));
        OnPropertyChanged(nameof(FadeOutCurve));
        OnPropertyChanged(nameof(CrossfadeCurve));
        OnPropertyChanged(nameof(FadeCurve));
        OnPropertyChanged(nameof(PatchCurve));
        OnPropertyChanged(nameof(AudioTrack));
        OnPropertyChanged(nameof(VideoTrack));
        OnPropertyChanged(nameof(SubtitleTracks));
        OnPropertyChanged(nameof(HasSubtitleTracks));
        OnPropertyChanged(nameof(SubtitleSummary));
        OnPropertyChanged(nameof(CanBePlaced));
        OnPropertyChanged(nameof(IsCoverArtOnly));
        OnPropertyChanged(nameof(CanBePlaced));
        OnPropertyChanged(nameof(CanChooseSubtitles));
        OnPropertyChanged(nameof(FireModeIndex));
        OnPropertyChanged(nameof(IsTimelineGroup));
        OnPropertyChanged(nameof(IsPlaylistGroup));
        OnPropertyChanged(nameof(ChildCount));
        OnPropertyChanged(nameof(ShuffleValue));
        OnPropertyChanged(nameof(ReshuffleValue));
        OnPropertyChanged(nameof(CrossfadeValue));
        OnPropertyChanged(nameof(AtEndIndex));
        OnPropertyChanged(nameof(SubtitlePicker));
    }

    private CueKind KindOf() => Cue is null ? CueKind.Comment : CuePresentation.KindOf(Cue);

    private static CueKind KindOf(CueNode cue) => CuePresentation.KindOf(cue);

    /// <summary>
    /// The tab table from screen 04. "Note" (singular) is on every kind and a comment cue collapses to
    /// it (register item 19).
    /// </summary>
    private static IReadOnlyList<string> TabsFor(CueKind kind) => kind switch
    {
        CueKind.Media or CueKind.Video => ["GENERAL", "AUDIO", "VIDEO", "EFFECTS", "NOTE", "PREVIEW"],
        CueKind.Group => ["GENERAL", "GROUP", "NOTE"],
        CueKind.Action => ["GENERAL", "ACTION", "NOTE"],
        CueKind.Fade => ["GENERAL", "FADE", "NOTE"],
        CueKind.Jump => ["GENERAL", "JUMP", "NOTE"],
        CueKind.Visualizer => ["GENERAL", "VISUALIZER", "VIDEO", "EFFECTS", "NOTE"],
        CueKind.Patch => ["GENERAL", "PATCH", "NOTE"],
        _ => ["GENERAL", "NOTE"],
    };

    public bool IsGeneralPane => SelectedTab == "GENERAL";
    public bool IsAudioPane => SelectedTab == "AUDIO";
    public bool IsVideoPane => SelectedTab == "VIDEO";
    public bool IsEffectsPane => SelectedTab == "EFFECTS";
    public bool IsNotePane => SelectedTab == "NOTE";
    public bool IsPreviewPane => SelectedTab == "PREVIEW";
    public bool IsGroupPane => SelectedTab == "GROUP";
    public bool IsPatchPane => SelectedTab == "PATCH";
    public bool IsActionPane => SelectedTab == "ACTION";
    public bool IsFadePane => SelectedTab == "FADE";
    public bool IsJumpPane => SelectedTab == "JUMP";
    public bool IsVisualizerPane => SelectedTab == "VISUALIZER";

    // ── editable fields ───────────────────────────────────────────────────────────────────────
    // Across a multi-selection a differing value reads "—" and stays that way until the operator types
    // into it: showing the lead cue's value instead would invite an edit that silently overwrote the
    // others with something nobody read.

    public string MultiSelectionNote =>
        $"{SelectionCount} cues · mixed values read — and only write when touched";

    public string NumberValue
    {
        get => Shared(cue => CuePresentation.Number(cue.Number));
        set
        {
            // A number that is not dot-separated digits is REFUSED, not coerced: the field keeps
            // showing the old value, which is the one the running order on paper still says.
            if (CueNumber.TryParse(value, out var number))
                Edit("number", cue => cue.Number, (cue, parsed) => cue.Number = parsed, number);
        }
    }

    public string LabelValue
    {
        get => Shared(cue => cue.Label);
        set => Edit("label", cue => cue.Label, (cue, text) => cue.Label = text, value);
    }

    public string NoteValue
    {
        get => Shared(cue => cue.Note);
        set => Edit("note", cue => cue.Note, (cue, text) => cue.Note = text, value);
    }

    public bool IsEnabled
    {
        get => Cue?.Enabled ?? true;
        set => Edit("enabled", cue => cue.Enabled, (cue, flag) => cue.Enabled = flag, value);
    }

    public string LevelValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Db(media.LevelDb) : "—");
        set
        {
            if (TryParseDb(value, out var db))
                EditMedia("level", media => media.LevelDb, (media, parsed) => media.LevelDb = parsed, db);
        }
    }

    public string FadeValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Seconds(media.FadeInMs) : "—");
        set
        {
            if (TryParseSeconds(value, out var ms))
                EditMedia("fadeIn", media => media.FadeInMs, (media, set) => media.FadeInMs = set, ms);
        }
    }

    public string FadeOutValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Seconds(media.FadeOutMs) : "—");
        set
        {
            if (TryParseSeconds(value, out var ms))
                EditMedia("fadeOut", media => media.FadeOutMs, (media, set) => media.FadeOutMs = set, ms);
        }
    }

    /// <summary>
    /// Whether the cue loops. A document fact the operator sets, not something the engine decides.
    /// </summary>
    public bool LoopValue
    {
        get => Cue is MediaCueNode { Loop: true };
        set => EditMedia("loop", media => media.Loop, (media, on) => media.Loop = on, value);
    }

    public string TrimInValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Seconds(media.TrimInMs) : "—");
        set
        {
            if (TryParseSeconds(value, out var ms))
                EditMedia("trimIn", media => media.TrimInMs, (media, set) => media.TrimInMs = set, ms);
        }
    }

    /// <summary>The out-point, or "end" when the cue plays through.</summary>
    /// <remarks>
    /// Zero means "to the end" in the model, and showing that as "0.0" would read as an out-point at
    /// the very start — a cue that plays nothing. The word is the honest rendering.
    /// </remarks>
    public string TrimOutValue
    {
        get => Shared(cue => cue is MediaCueNode media
            ? media.TrimOutMs <= 0 ? "end" : CuePresentation.Seconds(media.TrimOutMs)
            : "—");
        set
        {
            if (value.Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                EditMedia("trimOut", media => media.TrimOutMs, (media, set) => media.TrimOutMs = set, 0);
                return;
            }

            if (TryParseSeconds(value, out var ms))
                EditMedia("trimOut", media => media.TrimOutMs, (media, set) => media.TrimOutMs = set, ms);
        }
    }

    /// <summary>
    /// How this cue is reached — manual, follow, or continue.
    /// </summary>
    /// <remarks>
    /// On the BASE cue type, so it is offered for every kind. "Wait two seconds then tell the lighting
    /// desk" is an ordinary thing to author onto an action cue, and the transport honours the trigger
    /// on control-flow cues exactly as it does on media ones.
    /// </remarks>
    public int TriggerIndex
    {
        get => Cue is { } cue ? (int)cue.Trigger : -1;
        set
        {
            if (value is < 0 or > 2)
                return;

            Edit("trigger", cue => cue.Trigger, (cue, mode) => cue.Trigger = mode, (CueTrigger)value);
        }
    }

    public string PreWaitValue
    {
        get => Shared(cue => CuePresentation.Seconds(cue.PreWaitMs));
        set
        {
            if (TryParseSeconds(value, out var ms))
                Edit("preWait", cue => cue.PreWaitMs, (cue, set) => cue.PreWaitMs = set, ms);
        }
    }

    public string PostWaitValue
    {
        get => Shared(cue => CuePresentation.Seconds(cue.PostWaitMs));
        set
        {
            if (TryParseSeconds(value, out var ms))
                Edit("postWait", cue => cue.PostWaitMs, (cue, set) => cue.PostWaitMs = set, ms);
        }
    }

    /// <summary>
    /// Reads a duration typed as seconds, accepting the unit the field renders.
    /// </summary>
    /// <remarks>
    /// The display writes "4.0 s"; a parser that only accepted bare numbers would refuse to read back
    /// the value it had just written, which is the commonest way a field appears not to work. Negative
    /// durations are refused rather than clamped — the field keeps the old value so nothing silently
    /// becomes zero.
    /// </remarks>
    private static bool TryParseSeconds(string text, out int milliseconds)
    {
        milliseconds = 0;

        var cleaned = text.Replace("s", "", StringComparison.OrdinalIgnoreCase)
            .Replace('−', '-')
            .Trim();

        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds))
            return false;

        if (seconds < 0 || double.IsNaN(seconds))
            return false;

        milliseconds = (int)Math.Round(seconds * 1000);
        return true;
    }

    /// <summary>
    /// Ends the current coalescing group — called when a field loses focus.
    /// </summary>
    /// <remarks>
    /// Without an explicit boundary two separate edits of the same field merge into one undo step, and
    /// an edit made after a save merges into the pre-save command. The UI owns the boundary because
    /// only it knows when the gesture ended.
    /// </remarks>
    public void EndEdit() => _journal.CloseGroup();

    // ── the Audio pane ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<CurveOption> Curves { get; } = CurveLibrary.Curves;

    public IReadOnlyList<MatrixColumn> SendColumns => AudioPresentation.SendColumns(Project);

    public IReadOnlyList<MatrixRow> SendRows => Cue is MediaCueNode media
        ? AudioPresentation.SendRows(Project, media, picked: 1)
        : [];

    /// <summary>
    /// The effective route for the picked source channel, read from the middle.
    /// </summary>
    /// <remarks>
    /// This is the answer to "why is this silent" and to "why is it coming out twice", and it is
    /// computed from the two matrices rather than described — so it cannot disagree with them.
    /// </remarks>
    public IReadOnlyList<RouteHop> RouteChain => Cue is MediaCueNode media
        ? AudioPresentation.RouteChain(Project, media, sourceChannel: 1)
        : [];

    public bool HasRoute => RouteChain.Count > 0;

    /// <summary>
    /// Applies one pointer gesture to this cue's sends — the same click/drag/right-click as the patch.
    /// </summary>
    /// <remarks>
    /// No group linking here. An Output Group links the PATCH, where a stereo pair's two cells are the
    /// same trim on the same speaker system; a cue's sends are where the operator decides what goes
    /// where, and mirroring one into its partner would undo that decision.
    /// </remarks>
    public void ApplySendGesture(MatrixGesture gesture)
    {
        if (Cue is not MediaCueNode cue
            || gesture.Row >= SendRows.Count
            || gesture.Column >= SendColumns.Count)
            return;

        var source = SendRows[gesture.Row].LineChannel;
        var channelId = SendColumns[gesture.Column].ChannelId;
        var existing = cue.Sends.FirstOrDefault(
            send => send.SourceChannel == source && send.LogicalChannelId == channelId);

        switch (gesture.Kind)
        {
            case MatrixGestureKind.Toggle:
                _journal.Do(new SetCueSendCommand(
                    cue, source, channelId,
                    existing is null ? 0 : null,
                    existing is null ? false : null,
                    existing is null ? "route send at unity" : "remove send"));
                _journal.CloseGroup();
                break;

            case MatrixGestureKind.Adjust when existing is not null:
                _journal.Do(new SetCueSendCommand(
                    cue, source, channelId, existing.GainDb + gesture.DeltaDb, existing.Muted,
                    "set send gain"));
                break;

            case MatrixGestureKind.Mute when existing is not null:
                _journal.Do(new SetCueSendCommand(
                    cue, source, channelId, existing.GainDb, !existing.Muted,
                    existing.Muted ? "unmute send" : "mute send"));
                _journal.CloseGroup();
                break;
        }

        Reload();
    }

    // ── the Video pane ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<PlacementBox> Placements
    {
        get
        {
            // The canvas the SELECTED placement is on — a cue can be on several at once, and the
            // preview shows the one being edited.
            if (Placement is not { } placement)
                return [];

            var composition = Project.Compositions
                .FirstOrDefault(item => item.Id == placement.CompositionId);

            return composition is null ? [] : VideoPresentation.Layers(Project, composition, Cue?.Id);
        }
    }

    /// <summary>Every canvas the cue appears on.</summary>
    public IReadOnlyList<LayerPlacement> PlacementList =>
        Cue is null ? [] : CuePlacements.Of(Cue);

    /// <summary>Which one the fields edit. A cue can be on several canvases at once.</summary>
    [ObservableProperty]
    private int _selectedPlacement;

    private LayerPlacement? Placement =>
        SelectedPlacement >= 0 && SelectedPlacement < PlacementList.Count
            ? PlacementList[SelectedPlacement]
            : PlacementList.FirstOrDefault();

    /// <summary>
    /// Whether this cue is on a canvas at all.
    /// </summary>
    /// <remarks>
    /// A media cue always OFFERS the Video tab, because putting one on a canvas is a thing you do to
    /// an existing cue. Without this the pane draws an empty canvas and four fields describing a
    /// placement that does not exist, which reads exactly like a layer sized to nothing.
    /// </remarks>
    public bool HasPlacement => Placement is not null;

    public IReadOnlyList<string> PlacementCompositions =>
        [.. Project.Compositions.Select(composition =>
            $"{composition.Name} · {composition.Width}×{composition.Height}")];

    public int PlacementCompositionIndex
    {
        get => Placement is { } placement
            ? Project.Compositions.FindIndex(composition => composition.Id == placement.CompositionId)
            : -1;
        set
        {
            if (Placement is not { } placement || value < 0 || value >= Project.Compositions.Count)
                return;

            var composition = Project.Compositions[value];
            if (composition.Id == placement.CompositionId)
                return;

            Edit("composition", "video",
                () => placement.CompositionId, id => placement.CompositionId = id, composition.Id,
                $"move to {composition.Name}");
        }
    }

    public string LayerValue
    {
        get => Placement is { } placement ? placement.LayerIndex.ToString(CultureInfo.CurrentCulture) : "—";
        set
        {
            if (Placement is not { } placement
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var layer))
                return;

            Edit("layer", "video",
                () => placement.LayerIndex, index => placement.LayerIndex = Math.Max(0, index), layer,
                "set layer");
        }
    }

    public IReadOnlyList<string> FitModes { get; } = ["contain", "cover", "stretch"];

    public int FitIndex
    {
        get => Placement is { } placement ? (int)placement.Fit : -1;
        set
        {
            if (Placement is not { } placement || value < 0 || (LayerFit)value == placement.Fit)
                return;

            Edit("fit", "video",
                () => placement.Fit, fit => placement.Fit = fit, (LayerFit)value,
                $"set fit {FitModes[value]}");
        }
    }

    public string PlacementOpacityValue
    {
        get => Placement is { } placement ? $"{placement.Opacity * 100:0.#} %" : "—";
        set
        {
            if (Placement is not { } placement
                || !double.TryParse(
                    new string([.. value.Where(c => char.IsDigit(c) || c is '.' or ',')]),
                    NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
                return;

            Edit("opacity", "video",
                () => placement.Opacity, opacity => placement.Opacity = opacity,
                Math.Clamp(typed / 100, 0, 1), "set layer opacity");
        }
    }

    private void Edit<T>(
        string property, string domain, Func<T> read, Action<T> write, T value, string description)
    {
        if (Cue is not { } cue)
            return;

        _journal.Do(new SetValueCommand<T>(cue.Id, property, domain, read, write, value, description));
        _journal.CloseGroup();
        Reload();
    }

    /// <summary>
    /// A drag on the inspector's placement preview.
    /// </summary>
    /// <remarks>
    /// The preview shows the WHOLE composition, not just the selected cue, so a drag here can move any
    /// layer on it — which is the point of showing the neighbours at all. The command is the same one
    /// the Video view builds, keyed to the cue, so the two canvases share one undo step rather than
    /// producing two that disagree.
    /// </remarks>
    public void ApplyPlacementGesture(PlacementGesture gesture)
    {
        if (Project.FindCue(gesture.SubjectId) is not { } cue)
            return;

        if (CuePlacements.Of(cue).FirstOrDefault(item => item.LayerIndex == gesture.Layer)
            is not { } placement)
            return;

        _drag ??= _journal.Composite("move layer", "video");
        _journal.Do(RectEdits.Placement(cue, placement, gesture.Rect));
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(HasPlacement));
        OnPropertyChanged(nameof(PlacementList));
        OnPropertyChanged(nameof(PlacementNames));
        OnPropertyChanged(nameof(HasSeveralPlacements));
        OnPropertyChanged(nameof(PlacementCompositions));
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacityValue));
    }

    /// <summary>Closes the drag's undo step. Called when the pointer is released or a nudge lands.</summary>
    public void EndPlacementGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    // ── the Group pane ────────────────────────────────────────────────────────────────────────

    private GroupCueNode? Group => Cue as GroupCueNode;

    public IReadOnlyList<string> FireModes { get; } = ["all together", "playlist", "timeline"];

    /// <summary>
    /// How this group fires. Was a hard-coded index, so a timeline group read "playlist".
    /// </summary>
    public int FireModeIndex
    {
        get => Group is { } group ? (int)group.FireMode : -1;
        set
        {
            if (Group is not { } group || value < 0 || (GroupFireMode)value == group.FireMode)
                return;

            Edit("fireMode", "cues",
                () => group.FireMode, mode => group.FireMode = mode, (GroupFireMode)value,
                $"fire {FireModes[value]}");
        }
    }

    public bool IsTimelineGroup => Group is { FireMode: GroupFireMode.Timeline };

    /// <summary>Playlist-only options; a timeline group has no "next item" to cross into.</summary>
    public bool IsPlaylistGroup => Group is { FireMode: GroupFireMode.Playlist };

    public string ChildCount => Group is { } group
        ? $"{group.Children.Count} cue{(group.Children.Count == 1 ? "" : "s")}"
        : "—";

    public bool ShuffleValue
    {
        get => Group is { Shuffle: true };
        set
        {
            if (Group is { } group && value != group.Shuffle)
                Edit("shuffle", "cues", () => group.Shuffle, on => group.Shuffle = on, value,
                    value ? "shuffle" : "play in order");
        }
    }

    public bool ReshuffleValue
    {
        get => Group is { ReshuffleEachPass: true };
        set
        {
            if (Group is { } group && value != group.ReshuffleEachPass)
                Edit("reshuffle", "cues",
                    () => group.ReshuffleEachPass, on => group.ReshuffleEachPass = on, value,
                    "reshuffle each pass");
        }
    }

    public string CrossfadeValue
    {
        get => Group is { } group ? $"{group.CrossfadeMs / 1000d:0.##} s" : "—";
        set
        {
            if (Group is not { } group
                || !double.TryParse(
                    new string([.. value.Where(c => char.IsAsciiDigit(c) || c is '.' or ',')]),
                    NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds))
                return;

            Edit("crossfade", "cues",
                () => group.CrossfadeMs, ms => group.CrossfadeMs = ms,
                (int)Math.Clamp(seconds * 1000, 0, 60_000), "set crossfade");
        }
    }

    public IReadOnlyList<string> AtEndOptions { get; } = ["hold last", "loop", "next list"];

    public int AtEndIndex
    {
        get => Group is { } group ? (int)group.AtEnd : -1;
        set
        {
            if (Group is not { } group || value < 0 || (AtListEnd)value == group.AtEnd)
                return;

            Edit("atEnd", "cues", () => group.AtEnd, at => group.AtEnd = at, (AtListEnd)value,
                $"at end: {AtEndOptions[value]}");
        }
    }

    // ── media tracks ──────────────────────────────────────────────────────────────────────────
    // The options are a MACHINE fact and the choice is a DOCUMENT one, so the picker takes both: the
    // probe's track list and the cue it writes to.

    /// <summary>What the selected cue's file turned out to contain. Set by the shell as probes land.</summary>
    public MediaFacts? Facts { get; set; }

    public TrackPickerViewModel AudioTrack => Track(TrackKind.Audio);
    public TrackPickerViewModel VideoTrack => Track(TrackKind.Video);

    private TrackPickerViewModel Track(TrackKind kind) =>
        new(_journal, Cue as MediaCueNode, kind, Facts, Reload);

    /// <summary>
    /// Whether this cue has anything a composition could show.
    /// </summary>
    /// <remarks>
    /// True for cover art as well as moving video: an audio cue putting the album art on a canvas for
    /// the length of the track is a normal thing to want, and the Video tab is where that is done.
    /// </remarks>
    public bool CanBePlaced => Facts is { HasPlaceableVideo: true };

    /// <summary>The only video is a still frame — worth saying before somebody places it.</summary>
    public bool IsCoverArtOnly => Facts is { IsCoverArtOnly: true };

    public string CoverArtNote =>
        "this file's only video is cover art — a placement shows that still frame for the cue's length";

    /// <summary>Which composition a new placement would go on. Index into <see cref="PlacementCompositions"/>.</summary>
    [ObservableProperty]
    private int _placementTarget;

    /// <summary>
    /// Puts this cue on a composition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full-canvas at the next free layer, which is what somebody placing a cue almost always wants
    /// and is trivial to drag from. The canvas is right there to adjust it on.
    /// </para>
    /// <para>
    /// <b>Cover art is selected explicitly here.</b> The decoder's automatic election deliberately
    /// SKIPS attached pictures, so an MP3 placed with no video-track choice would put nothing on the
    /// canvas — the operator would see an empty layer and no reason for it. Naming the stream is what
    /// makes "place it and the album art appears" true.
    /// </para>
    /// </remarks>
    public void PlaceOnComposition()
    {
        if (Cue is not MediaCueNode and not VisualizerCueNode
            || Cue is null
            || PlacementTarget < 0
            || PlacementTarget >= Project.Compositions.Count)
            return;

        var composition = Project.Compositions[PlacementTarget];
        var cue = Cue;
        var list = CuePlacements.ListOf(cue);
        var still = Facts is { IsCoverArtOnly: true } ? Facts.PlaceableVideoTrack : null;

        if (list is null)
            return;

        using (_journal.Composite($"place on {composition.Name}", "video"))
        {
            // APPENDED, not assigned: a cue can be on several canvases at once and the engine fans one
            // decoded source to all of them. Replacing would silently drop the mirror somebody added.
            _journal.Do(new AddItemCommand<LayerPlacement>(
                list,
                new LayerPlacement
                {
                    CompositionId = composition.Id,
                    LayerIndex = NextLayer(composition),
                },
                list.Count,
                "video",
                $"place on {composition.Name}"));

            if (still is { } track && cue is MediaCueNode media && media.VideoTrackIndex is null)
            {
                _journal.Do(new SetValueCommand<int?>(
                    cue.Id, "videoTrack", "video",
                    () => media.VideoTrackIndex, index => media.VideoTrackIndex = index, track.Index,
                    "show the cover art"));

                _journal.Do(new SetValueCommand<string>(
                    cue.Id, "videoSignature", "video",
                    () => media.VideoTrackSignature,
                    signature => media.VideoTrackSignature = signature, track.Signature,
                    "remember which track"));
            }
        }

        SelectedPlacement = list.Count - 1;
        Reload();
    }

    /// <summary>Removes the SELECTED placement — the cue may still appear on other canvases.</summary>
    public void RemovePlacement()
    {
        if (Cue is not { } cue
            || CuePlacements.ListOf(cue) is not { } list
            || Placement is not { } placement)
            return;

        _journal.Do(new RemoveItemCommand<LayerPlacement>(
            list, placement, "video", "remove from composition"));
        _journal.CloseGroup();

        SelectedPlacement = Math.Clamp(SelectedPlacement, 0, Math.Max(0, list.Count - 1));
        Reload();
    }

    /// <summary>One above whatever is already there, so a new layer lands on top rather than under.</summary>
    private int NextLayer(CompositionDefinition composition) =>
        Project.AllCues()
            .SelectMany(CuePlacements.Of)
            .Where(placement => placement.CompositionId == composition.Id)
            .Select(placement => placement.LayerIndex + 1)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>The canvases this cue is on, as the picker beside the preview lists them.</summary>
    public IReadOnlyList<string> PlacementNames =>
    [
        .. PlacementList.Select(placement =>
            (Project.Compositions.FirstOrDefault(c => c.Id == placement.CompositionId)?.Name ?? "?")
            + $" · L{placement.LayerIndex}"),
    ];

    /// <summary>More than one canvas: the picker only earns its space then.</summary>
    public bool HasSeveralPlacements => PlacementList.Count > 1;





    /// <summary>The subtitle tracks in the file, as the picker lists them.</summary>
    public IReadOnlyList<string> SubtitleTracks =>
        Facts is null ? [] : [.. Facts.SubtitleTracks.Select(track => track.Label)];

    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;

    /// <summary>What the cue currently shows — empty is the default, subtitles are never on by accident.</summary>
    public string SubtitleSummary => Cue is MediaCueNode { Subtitles.Count: > 0 } media
        ? string.Join(" · ", media.Subtitles.Select(Describe))
        : "none";

    /// <summary>The picker for this cue's subtitles, or null when there is no cue to edit.</summary>
    public SubtitlePickerViewModel? SubtitlePicker =>
        Cue is MediaCueNode media ? new SubtitlePickerViewModel(_journal, media, Facts) : null;

    /// <summary>
    /// A cue can always be given a SIDECAR, even when its file carries no tracks.
    /// </summary>
    /// <remarks>
    /// Gating the button on embedded tracks would hide the commonest case: a file with no subtitles at
    /// all and a hand-made .srt beside it.
    /// </remarks>
    public bool CanChooseSubtitles => Cue is MediaCueNode { MediaPath.Length: > 0 };

    private static string Describe(SubtitleSelection selection) =>
        selection.Path.Length > 0 ? Path.GetFileName(selection.Path) : $"track #{selection.StreamIndex}";

    // ── curve pickers ─────────────────────────────────────────────────────────────────────────
    // One per curve a cue can carry. Built on demand from the same (which, cue) lookup the editor
    // uses, so a picker and the "✎" beside it can never address different curves.

    public CurvePickerViewModel FadeInCurve => Picker("fadeIn");
    public CurvePickerViewModel FadeOutCurve => Picker("fadeOut");
    public CurvePickerViewModel CrossfadeCurve => Picker("crossfade");
    public CurvePickerViewModel FadeCurve => Picker("fade");
    public CurvePickerViewModel PatchCurve => Picker("patch");

    private CurvePickerViewModel Picker(string which) =>
        new(_journal, Cue, SpecOf(which).Spec, Reload);

    private (CurveSpec? Spec, string Label) SpecOf(string which) => (which, Cue) switch
    {
        ("fadeIn", MediaCueNode media) => (media.FadeInCurve, "fade in"),
        ("fadeOut", MediaCueNode media) => (media.FadeOutCurve, "fade out"),
        ("crossfade", GroupCueNode group) => (group.CrossfadeCurve, "crossfade"),
        ("fade", FadeCueNode fade) => (fade.Curve, "fade"),
        ("patch", PatchCueNode patch) => (patch.FadeCurve, "patch ramp"),
        _ => (null, ""),
    };

    /// <summary>
    /// The editor for one of this cue's curves, or null when the cue has none of that name.
    /// </summary>
    /// <remarks>
    /// Register item 16 — one editor for fades, crossfades and patch-cue ramps alike. Building the
    /// TARGET here rather than handing the window a cue is what makes that possible: the window knows
    /// only "a curve", so a lane or a preset can use it unchanged.
    /// </remarks>
    public CurveEditorViewModel? CurveEditor(string which)
    {
        if (Cue is not { } cue)
            return null;

        var (spec, label) = SpecOf(which);

        if (spec is null)
            return null;

        return new CurveEditorViewModel(
            _journal,
            new CurveSpecTarget(cue.Id, which, spec),
            $"Q{CuePresentation.Number(cue.Number)} · {label}");
    }

    // ── the Effects pane (register item 18: lanes, hidden until added) ────────────────────────
    public IReadOnlyList<string> EffectLanes
    {
        get
        {
            var lanes = Cue switch
            {
                MediaCueNode media => media.EffectLanes,
                GroupCueNode group => group.EffectLanes,
                VisualizerCueNode visualizer => visualizer.EffectLanes,
                _ => [],
            };

            return
            [
                .. lanes.Select(lane => $"{lane.Kind.ToString().ToLowerInvariant()} · {lane.Points.Count} points"),
            ];
        }
    }

    // ── edit plumbing ─────────────────────────────────────────────────────────────────────────

    /// <summary>A value all selected cues agree on, or <see cref="Mixed"/> when they do not.</summary>
    private string Shared(Func<CueNode, string> read)
    {
        var values = Selected.Select(read).Distinct().ToList();
        return values.Count == 1 ? values[0] : values.Count == 0 ? "" : Mixed;
    }

    private void Edit<T>(string property, Func<CueNode, T> read, Action<CueNode, T> write, T value)
    {
        var targets = Selected;
        if (targets.Count == 0)
            return;

        // A multi-selection edit is one thing the operator did, so it is one thing to undo.
        if (targets.Count > 1)
        {
            using (_journal.Composite($"set {property} on {targets.Count} cues", "cues"))
                foreach (var cue in targets)
                    _journal.Do(Command(cue, property, read, write, value));
        }
        else
        {
            _journal.Do(Command(targets[0], property, read, write, value));
        }

        Reload();
    }

    private void EditMedia<T>(
        string property, Func<MediaCueNode, T> read, Action<MediaCueNode, T> write, T value) =>
        Edit(
            property,
            cue => cue is MediaCueNode media ? read(media) : default!,
            (cue, parsed) =>
            {
                if (cue is MediaCueNode media)
                    write(media, parsed);
            },
            value);

    private static SetValueCommand<T> Command<T>(
        CueNode cue, string property, Func<CueNode, T> read, Action<CueNode, T> write, T value) =>
        new(cue.Id, property, "cues", () => read(cue), parsed => write(cue, parsed), value,
            $"set {property} on Q{CuePresentation.Number(cue.Number)}");

    /// <summary>
    /// Parses a level, accepting the U+2212 MINUS SIGN the app renders as well as a plain hyphen.
    /// </summary>
    /// <remarks>
    /// The display uses a true minus because it aligns in a tabular column; a parser that only knew
    /// hyphens would refuse to read back the value it had just written.
    /// </remarks>
    private static bool TryParseDb(string text, out double value) =>
        double.TryParse(
            text.Replace('−', '-').Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
}

/// <summary>
/// One curve picker: the four built-in laws, plus "custom" for a shape somebody drew.
/// </summary>
/// <remarks>
/// "custom" is not a fifth law — it is what the list reads when an INLINE POINT LIST exists, because
/// <see cref="CurveSpec.Resolve"/> prefers those points over the law. Which is also why choosing a law
/// has to clear them: without that, picking "linear" over a drawn curve would change the reading and
/// nothing else, and the picker would be describing a fade that does not happen.
/// </remarks>
public sealed class CurvePickerViewModel(
    ProjectJournal journal, CueNode? cue, CurveSpec? spec, Action reload)
{
    /// <summary>The index the list shows for a drawn shape — one past the last law.</summary>
    public const int CustomIndex = 4;

    public IReadOnlyList<CurveOption> Curves { get; } = CurveLibrary.Curves;

    public bool HasCurve => spec is not null && cue is not null;

    public int SelectedIndex
    {
        get => spec is null
            ? -1
            : spec.Points is { Count: > 1 }
                ? CustomIndex
                : (int)spec.Law;

        set
        {
            // Selecting "custom" is not an edit: it is where the ✎ lives, and the shape arrives when
            // the editor is used. Choosing it without drawing anything would store a straight line.
            if (spec is null || cue is null || value is < 0 or >= CustomIndex || value == SelectedIndex)
                return;

            using (journal.Composite("set fade curve", "cues"))
            {
                journal.Do(new SetValueCommand<FadeCurve>(
                    cue.Id, "curveLaw", "cues",
                    () => spec.Law, law => spec.Law = law, (FadeCurve)value,
                    $"set curve {Curves[value].Name}"));

                if (spec.Points is { Count: > 1 })
                {
                    journal.Do(new SetValueCommand<List<FadeCurvePoint>?>(
                        cue.Id, "curvePoints", "cues",
                        () => spec.Points, points => spec.Points = points, null,
                        "drop the custom shape"));
                }
            }

            reload();
        }
    }
}
