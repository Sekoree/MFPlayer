using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Engine;
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

    /// <summary>When false each selection starts on its cue-kind default instead of recalling a prior tab.</summary>
    public bool RememberTabs { get; set; } = true;

    private HaCueProject Project => _journal.Project;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>The running show, used only for hot geometry edits while a cue is sounding.</summary>
    public ShowHost? Host { get; set; }

    /// <summary>The lead cue — the one whose values the single-selection fields show.</summary>
    public CueNode? Cue => _selection.Count > 0 ? Project.FindCue(_selection[0]) : null;

    public IReadOnlyList<CueNode> Selected =>
        [.. _selection.Select(Project.FindCue).OfType<CueNode>()];

    /// <summary>Whether document-authoring controls should be interactive under the shell Lock.</summary>
    public bool CanAuthor => !_journal.IsReadOnly;

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
    [NotifyPropertyChangedFor(nameof(IsTextPane))]
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
        if (RememberTabs && SelectedTab is { } previous && Cue is { } old && SelectionCount <= 1)
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
                .Select(TabsFor)
                .Aggregate((IEnumerable<string>)TabsFor(lead), (common, next) => common.Intersect(next))
                .ToList();

        SelectedTab = lead is null
            ? null
            : RememberTabs && _rememberedTab.TryGetValue(KindOf(lead), out var remembered) && Tabs.Contains(remembered)
                ? remembered
                // General carries the complete clip window for finite media, so it is the useful
                // starting pane there. Other kinds still open on their kind-specific second tab.
                : lead is MediaCueNode media && !SourceUri.IsLive(media.MediaPath)
                    ? "GENERAL"
                    : Tabs.Skip(1).FirstOrDefault() ?? Tabs.FirstOrDefault();

        OnPropertyChanged(nameof(Cue));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanAuthor));
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
        OnPropertyChanged(nameof(TrimHint));
        OnPropertyChanged(nameof(CanTrimMedia));
        OnPropertyChanged(nameof(CanEditClip));
        OnPropertyChanged(nameof(ClipEditorHint));
        OnPropertyChanged(nameof(TriggerIndex));
        OnPropertyChanged(nameof(PreWaitValue));
        OnPropertyChanged(nameof(PostWaitValue));
        OnPropertyChanged(nameof(NoteValue));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsSourceCue));
        OnPropertyChanged(nameof(SourceNote));
        OnPropertyChanged(nameof(HasMediaCue));
        OnPropertyChanged(nameof(EndTargetOptions));
        OnPropertyChanged(nameof(EndTargetIndex));
        OnPropertyChanged(nameof(SendToVisualizerValue));
        OnPropertyChanged(nameof(SendColumns));
        OnPropertyChanged(nameof(SendRows));
        OnPropertyChanged(nameof(RouteChain));
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(PlacementGuidesX));
        OnPropertyChanged(nameof(PlacementGuidesY));
        OnPropertyChanged(nameof(PlacementAspect));
        OnPropertyChanged(nameof(HasPlacement));
        OnPropertyChanged(nameof(PlacementList));
        OnPropertyChanged(nameof(PlacementHeaders));
        OnPropertyChanged(nameof(PlacementCompositions));
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacity));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
        OnPropertyChanged(nameof(PlacementRotation));
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
        OnPropertyChanged(nameof(EffectLanes));
        OnPropertyChanged(nameof(HasEffectLanes));
        OnPropertyChanged(nameof(CanCarryLanes));
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
        OnPropertyChanged(nameof(IsSequencedGroup));
        OnPropertyChanged(nameof(ChildCount));
        OnPropertyChanged(nameof(ShuffleValue));
        OnPropertyChanged(nameof(ReshuffleValue));
        OnPropertyChanged(nameof(AvoidRepeatValue));
        OnPropertyChanged(nameof(LoopCountValue));
        OnPropertyChanged(nameof(PlayCountValue));
        OnPropertyChanged(nameof(EndBehaviourIndex));
        OnPropertyChanged(nameof(IsLooping));
        OnPropertyChanged(nameof(LoopCrossfadeValue));
        OnPropertyChanged(nameof(ColorTagIndex));
        OnPropertyChanged(nameof(PreRollValue));
        OnPropertyChanged(nameof(CardText));
        OnPropertyChanged(nameof(CardFont));
        OnPropertyChanged(nameof(CardSize));
        OnPropertyChanged(nameof(CardBold));
        OnPropertyChanged(nameof(CardItalic));
        OnPropertyChanged(nameof(CardInk));
        OnPropertyChanged(nameof(CardGround));
        OnPropertyChanged(nameof(CardAlignIndex));
        OnPropertyChanged(nameof(CardAnchorIndex));
        OnPropertyChanged(nameof(CardOutline));
        OnPropertyChanged(nameof(CardOutlineInk));
        OnPropertyChanged(nameof(CardDuration));
        OnPropertyChanged(nameof(CardFadeIn));
        OnPropertyChanged(nameof(CardFadeOut));
        OnPropertyChanged(nameof(CardHint));
        OnPropertyChanged(nameof(CrossfadeValue));
        OnPropertyChanged(nameof(AtEndIndex));
        OnPropertyChanged(nameof(SubtitlePicker));

        // The per-kind panes. Every one of these reads straight off the selected cue, so they all have
        // to be re-announced whenever the selection or the document changes.
        OnPropertyChanged(nameof(FadeTargets));
        OnPropertyChanged(nameof(FadeToLevelValue));
        OnPropertyChanged(nameof(FadeDurationValue));
        OnPropertyChanged(nameof(FadeEverythingValue));
        OnPropertyChanged(nameof(FadeStopsTargetsValue));
        OnPropertyChanged(nameof(FadeTargetHint));
        OnPropertyChanged(nameof(JumpTargets));
        OnPropertyChanged(nameof(JumpTargetIndex));
        OnPropertyChanged(nameof(JumpConditionIndex));
        OnPropertyChanged(nameof(JumpCountValue));
        OnPropertyChanged(nameof(IsCountedJump));
        OnPropertyChanged(nameof(JumpPickAtRandomValue));
        OnPropertyChanged(nameof(JumpFiresOnArrivalValue));
        OnPropertyChanged(nameof(JumpHint));
        OnPropertyChanged(nameof(ActionEndpoints));
        OnPropertyChanged(nameof(ActionEndpointIndex));
        OnPropertyChanged(nameof(ActionAddressValue));
        OnPropertyChanged(nameof(ActionArgumentsValue));
        OnPropertyChanged(nameof(ActionHint));
        OnPropertyChanged(nameof(PatchSnapshots));
        OnPropertyChanged(nameof(PatchSnapshotIndex));
        OnPropertyChanged(nameof(PatchFadeValue));
        OnPropertyChanged(nameof(PatchLevelChanges));
        OnPropertyChanged(nameof(HasPatchLevelChanges));
        OnPropertyChanged(nameof(PatchHint));
        OnPropertyChanged(nameof(VisualizerPresetPackValue));
        OnPropertyChanged(nameof(VisualizerHoldValue));
        OnPropertyChanged(nameof(VisualizerBlendValue));
        OnPropertyChanged(nameof(VisualizerLocksPresetValue));
        OnPropertyChanged(nameof(VisualizerFeedAllValue));
        OnPropertyChanged(nameof(VisualizerFeedCueNumbers));
        OnPropertyChanged(nameof(VisualizerFeedHint));
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
        // A card is words plus where they sit, so it takes VIDEO for the placement and EFFECTS for
        // an opacity lane — it is a picture from the moment it is drawn.
        CueKind.Text => ["GENERAL", "TEXT", "VIDEO", "EFFECTS", "NOTE"],
        _ => ["GENERAL", "NOTE"],
    };

    private static IReadOnlyList<string> TabsFor(CueNode cue) => TabsFor(KindOf(cue));

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
    public bool IsTextPane => SelectedTab == "TEXT";

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

    /// <summary>Whether this cue plays a source URI rather than a file on disk.</summary>
    public bool IsSourceCue => Cue is MediaCueNode media && SourceUri.IsSource(media.MediaPath);
    public bool HasMediaCue => Cue is MediaCueNode;

    public IReadOnlyList<string> EndTargetOptions => Cue is not MediaCueNode media
        ? []
        : ["— normal follow —", .. EndTargetCandidates(media)
            .Select(cue => $"Q{CuePresentation.Number(cue.Number)} · {cue.Label}")];

    public int EndTargetIndex
    {
        get
        {
            if (Cue is not MediaCueNode { EndTargetCueId: { } targetId } media)
                return 0;
            var at = EndTargetCandidates(media).FindIndex(cue => cue.Id == targetId);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Cue is not MediaCueNode media || value < 0)
                return;
            var candidates = EndTargetCandidates(media);
            var selected = value == 0 || value > candidates.Count ? (Guid?)null : candidates[value - 1].Id;
            if (selected == media.EndTargetCueId)
                return;
            EditMedia("endTarget", cue => cue.EndTargetCueId,
                (cue, target) => cue.EndTargetCueId = target, selected);
        }
    }

    private List<CueNode> EndTargetCandidates(MediaCueNode media) =>
        [.. Project.AllCues().Where(cue => cue.Id != media.Id && cue is not CommentCueNode)];

    /// <summary>
    /// What the cue is pointed at, in words.
    /// </summary>
    /// <remarks>
    /// Named rather than shown raw: a URI is mostly punctuation, and what identifies the cue is the
    /// camera's name or the device's. Changing it is "Edit source…" on the cue's own menu, which
    /// reopens the dialog that built it — there is no path here a file picker could replace.
    /// </remarks>
    public string SourceNote => Cue is not MediaCueNode media || !SourceUri.IsSource(media.MediaPath)
        ? ""
        : SourceUri.Describe(media.MediaPath)
          + (SourceUri.IsLive(media.MediaPath)
              ? " · live — no length, no seeking, and it opens when the cue fires"
              : "");

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
        get => Cue is MediaCueNode { Loop: true } or MediaCueNode { EndBehavior: CueEndBehavior.Loop };
        set => EditMedia("loop", media => media.Loop, (media, on) => media.Loop = on, value);
    }

    public bool SendToVisualizerValue
    {
        get => Cue is MediaCueNode { SendToVisualizer: true };
        set => EditMedia("sendToVisualizer", cue => cue.SendToVisualizer,
            (cue, on) => cue.SendToVisualizer = on, value);
    }

    /// <summary>
    /// What the cue does at its trimmed end.
    /// </summary>
    /// <remarks>
    /// Every cue stopped before this existed. FREEZE is what a title card wants; FADE OUT gives a cue
    /// its own out-ramp without a fade cue aimed at it.
    /// </remarks>
    public IReadOnlyList<string> EndBehaviours { get; } =
        ["stop", "freeze last frame", "loop", "fade out and stop"];

    public int EndBehaviourIndex
    {
        get => Cue is MediaCueNode media ? (int)media.EndBehavior : -1;
        set
        {
            if (Cue is not MediaCueNode media || value < 0 || (CueEndBehavior)value == media.EndBehavior)
                return;

            EditMedia("endBehavior",
                item => item.EndBehavior, (item, behaviour) => item.EndBehavior = behaviour,
                (CueEndBehavior)value);
        }
    }

    /// <summary>Whether the loop-overlap field has anything to act on.</summary>
    public bool IsLooping =>
        Cue is MediaCueNode { Loop: true } or MediaCueNode { EndBehavior: CueEndBehavior.Loop };

    /// <summary>
    /// The overlap when a loop wraps, in milliseconds. Zero is a hard cut.
    /// </summary>
    /// <remarks>
    /// A seamless loop is made of this. Without it the only way to get one was to author the crossfade
    /// into the file.
    /// </remarks>
    public int LoopCrossfadeValue
    {
        get => Cue is MediaCueNode media ? media.LoopCrossfadeMs : 0;
        set
        {
            if (Cue is not MediaCueNode media || value < 0 || value == media.LoopCrossfadeMs)
                return;

            EditMedia("loopCrossfade",
                item => item.LoopCrossfadeMs, (item, ms) => item.LoopCrossfadeMs = ms,
                Math.Clamp(value, 0, 30_000));
        }
    }

    /// <summary>
    /// Whether pre-roll opens this cue's media ahead of time.
    /// </summary>
    /// <remarks>
    /// Phrased as the POSITIVE on screen — "pre-roll this cue" — because the document's flag is a
    /// disable and a checkbox called "disable" that is ticked to mean "do not" is one an operator has
    /// to read twice under pressure.
    /// </remarks>
    public bool PreRollValue
    {
        get => Cue is not MediaCueNode { DisablePreRoll: true };
        set
        {
            if (Cue is not MediaCueNode media || value != media.DisablePreRoll)
                return;

            EditMedia("preRoll",
                item => item.DisablePreRoll, (item, off) => item.DisablePreRoll = off, !value);
        }
    }

    /// <summary>
    /// A colour band on the cue's row: 0 is none, 1–8 index the palette.
    /// </summary>
    /// <remarks>
    /// The fastest thing to read in a list of six hundred rows — an operator finds "the blue block"
    /// before they find Q412.
    /// </remarks>
    public int ColorTagIndex
    {
        get => Cue?.ColorTag ?? 0;
        set
        {
            if (Cue is not { } cue || value < 0 || value == cue.ColorTag)
                return;

            // The multi-aware overload: a colour tag belongs to every cue kind, and "tag this block of
            // twelve as blue" is the reason the feature exists. It used to write only the lead cue.
            Edit(
                "colorTag",
                target => target.ColorTag,
                (target, tag) => target.ColorTag = tag,
                Math.Clamp(value, 0, CueColors.Names.Count - 1));
        }
    }

    public IReadOnlyList<string> ColorTags => CueColors.Names;

    /// <summary>What the trim fields accept, and what the file runs for.</summary>
    /// <remarks>
    /// The LENGTH is the number that was missing: the out-point is stored absolute, so "ten minutes off
    /// the end" was arithmetic against a figure the pane never showed. It shows it now, and the fields
    /// take <c>-10:00</c> so the arithmetic is not needed either.
    /// </remarks>
    public string TrimHint =>
        ClipDuration is { } length
            ? $"file {ClipTimes.Format((int)length.TotalMilliseconds)} · {ClipTimes.Syntax}"
            : $"not probed · {ClipTimes.Syntax}";

    public string TrimInValue
    {
        get => Shared(cue => cue is MediaCueNode media ? ClipTimes.Format(media.TrimInMs) : "—");
        set
        {
            // A clock reading, seconds, or a from-the-end time. Thirty minutes was 1800.0 here until
            // the parser learned 30:00, which is what the field exists to accept.
            if (ClipTimes.Parse(value, ClipDuration) is { } ms)
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
            ? media.TrimOutMs <= 0 ? "end" : ClipTimes.Format(media.TrimOutMs)
            : "—");
        set
        {
            if (value.Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                EditMedia("trimOut", media => media.TrimOutMs, (media, set) => media.TrimOutMs = set, 0);
                return;
            }

            if (ClipTimes.Parse(value, ClipDuration) is { } ms)
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

    /// <summary>
    /// The seams between the screens showing this canvas, as snap targets for the placement drag.
    /// </summary>
    /// <remarks>
    /// What makes dividing a composition between projectors worth more than a picture: a cue can be
    /// dropped exactly onto ONE screen of a wall without anybody working out what fraction that is.
    /// Scoped to the composition the SELECTED placement is on, which is the same canvas
    /// <see cref="Placements"/> draws — a cue can be on several at once, and the guides of a canvas it
    /// is not being dragged on would snap it to seams that are not there.
    /// </remarks>
    public IReadOnlyList<double> PlacementGuidesX =>
        Placement is { } placement
            ? VideoPresentation.SliceGuides(Project, placement.CompositionId, horizontal: true)
            : [];

    public IReadOnlyList<double> PlacementGuidesY =>
        Placement is { } placement
            ? VideoPresentation.SliceGuides(Project, placement.CompositionId, horizontal: false)
            : [];

    /// <summary>The selected placement's real composition shape.</summary>
    public double PlacementAspect =>
        Placement is { } placement
        && Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId) is { Height: > 0 } composition
            ? composition.Width / (double)composition.Height
            : 16d / 9d;

    /// <summary>Editor preferences, shared by this inspector's placement canvases and not project data.</summary>
    [ObservableProperty]
    private bool _preservePlacementAspect = true;

    [ObservableProperty]
    private bool _snapPlacement = true;

    /// <summary>Every canvas the cue appears on.</summary>
    public IReadOnlyList<LayerPlacement> PlacementList =>
        Cue is null ? [] : CuePlacements.Of(Cue);

    /// <summary>Which one the fields edit. A cue can be on several canvases at once.</summary>
    /// <remarks>
    /// Both the boxes and the guides follow it, because switching placement can switch COMPOSITION —
    /// and a canvas drawn from one composition with the seams of another is worse than no seams: it
    /// offers snap targets that do not exist on the screen the cue is actually going to.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placements))]
    [NotifyPropertyChangedFor(nameof(PlacementGuidesX))]
    [NotifyPropertyChangedFor(nameof(PlacementGuidesY))]
    [NotifyPropertyChangedFor(nameof(PlacementAspect))]
    [NotifyPropertyChangedFor(nameof(PlacementHeaders))]
    private int _selectedPlacement;

    partial void OnSelectedPlacementChanged(int value)
    {
        // Every field projects the selected list entry. An expander changes that entry without
        // changing the cue, so a normal document Reload is neither necessary nor desirable (it would
        // also reset the selected tab); announce the projection properties directly.
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(PlacementAspect));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacity));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
        OnPropertyChanged(nameof(PlacementRotation));
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
    }

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

    /// <summary>The six the compositor can do. It offered three, so half of them were unreachable.</summary>
    public IReadOnlyList<string> FitModes { get; } =
        ["contain", "cover", "stretch", "center", "fill width", "fill height"];

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
            ApplyLivePlacement(Cue, placement);
        }
    }

    public double PlacementOpacity
    {
        get => Placement?.Opacity ?? 1;
        set => Write("opacity", 0, 1,
            placement => placement.Opacity, (placement, number) => placement.Opacity = number, value,
            "set layer opacity");
    }

    // ── the destination rectangle, as numbers ─────────────────────────────────────────────────
    // The same four a drag moves. A canvas cannot hit exactly half, and half is what a split screen
    // is made of.

    public double PlacementX
    {
        get => Placement?.X ?? 0;
        set => Write("destX", -1, 2,
            placement => placement.X, (placement, number) => placement.X = number, value, "move layer");
    }

    public double PlacementY
    {
        get => Placement?.Y ?? 0;
        set => Write("destY", -1, 2,
            placement => placement.Y, (placement, number) => placement.Y = number, value, "move layer");
    }

    public double PlacementWidth
    {
        get => Placement?.Width ?? 1;
        set => Write("destWidth", 0.001, 2,
            placement => placement.Width, (placement, number) => placement.Width = number, value,
            "resize layer");
    }

    public double PlacementHeight
    {
        get => Placement?.Height ?? 1;
        set => Write("destHeight", 0.001, 2,
            placement => placement.Height, (placement, number) => placement.Height = number, value,
            "resize layer");
    }

    public double PlacementRotation
    {
        get => Placement?.RotationDegrees ?? 0;
        set => Write("rotation", -360, 360,
            placement => placement.RotationDegrees,
            (placement, number) => placement.RotationDegrees = number, value, "rotate layer");
    }

    // ── the source crop ───────────────────────────────────────────────────────────────────────
    // Which part of the PICTURE to use, which is a different question from where to put it. Baked-in
    // letterbox bars come off with this; shrinking the destination instead would move the picture too.

    public double CropLeft
    {
        get => Placement?.CropLeft ?? 0;
        set => Write("cropLeft", 0, 0.49,
            placement => placement.CropLeft, (placement, number) => placement.CropLeft = number, value,
            "crop layer");
    }

    public double CropTop
    {
        get => Placement?.CropTop ?? 0;
        set => Write("cropTop", 0, 0.49,
            placement => placement.CropTop, (placement, number) => placement.CropTop = number, value,
            "crop layer");
    }

    public double CropRight
    {
        get => Placement?.CropRight ?? 0;
        set => Write("cropRight", 0, 0.49,
            placement => placement.CropRight, (placement, number) => placement.CropRight = number, value,
            "crop layer");
    }

    public double CropBottom
    {
        get => Placement?.CropBottom ?? 0;
        set => Write("cropBottom", 0, 0.49,
            placement => placement.CropBottom, (placement, number) => placement.CropBottom = number,
            value, "crop layer");
    }

    /// <summary>Writes one placement number through the journal, clamped to what it can mean.</summary>
    private void Write(
        string property,
        double minimum,
        double maximum,
        Func<LayerPlacement, double> read,
        Action<LayerPlacement, double> write,
        double value,
        string description)
    {
        if (Placement is not { } placement || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);

        if (Math.Abs(read(placement) - clamped) < 0.000001)
            return;

        Edit(property, "video",
            () => read(placement), number => write(placement, number), clamped, description);
        ApplyLivePlacement(Cue, placement);
    }

    /// <summary>
    /// Quick destination layouts: the rectangles a split screen is actually made of.
    /// </summary>
    /// <remarks>
    /// Exact by construction. Half is 0.5 and a quadrant is 0.25, and neither is reachable by dragging
    /// — which is why HaPlay grew the same set of buttons and why they are the fastest correct route to
    /// a two-up or a four-up.
    /// </remarks>
    public void ApplyLayout(string preset)
    {
        if (Placement is not { } placement || Cue is null)
            return;

        var (x, y, width, height) = preset switch
        {
            "full" => (0d, 0d, 1d, 1d),
            "left" => (0d, 0d, 0.5d, 1d),
            "right" => (0.5d, 0d, 0.5d, 1d),
            "top" => (0d, 0d, 1d, 0.5d),
            "bottom" => (0d, 0.5d, 1d, 0.5d),
            "tl" => (0d, 0d, 0.5d, 0.5d),
            "tr" => (0.5d, 0d, 0.5d, 0.5d),
            "bl" => (0d, 0.5d, 0.5d, 0.5d),
            "br" => (0.5d, 0.5d, 0.5d, 0.5d),
            _ => (double.NaN, 0d, 0d, 0d),
        };

        if (double.IsNaN(x))
            return;

        // ONE undo step for the whole rectangle: a layout half-applied is a rectangle nobody chose.
        using (_journal.Composite($"layout: {preset}", "video"))
        {
            _journal.Do(RectEdits.Placement(Cue, placement, new NormalizedRect(x, y, width, height)));
        }

        _journal.CloseGroup();
        ApplyLivePlacement(Cue, placement);
        Reload();
    }

    /// <summary>Quick source crops, including the one that takes them all off again.</summary>
    public void ApplyCrop(string preset)
    {
        if (Placement is not { } placement)
            return;

        var (left, top, right, bottom) = preset switch
        {
            "none" => (0d, 0d, 0d, 0d),
            "wide" => (0.25d, 0d, 0.25d, 0d),
            "tall" => (0d, 0.25d, 0d, 0.25d),
            "centre" => (0.25d, 0.25d, 0.25d, 0.25d),
            _ => (double.NaN, 0d, 0d, 0d),
        };

        if (double.IsNaN(left))
            return;

        using (_journal.Composite($"crop: {preset}", "video"))
        {
            _journal.Do(new SetValueCommand<double>(
                Cue!.Id, "cropLeft", "video",
                () => placement.CropLeft, n => placement.CropLeft = n, left, "crop"));
            _journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropTop", "video",
                () => placement.CropTop, n => placement.CropTop = n, top, "crop"));
            _journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropRight", "video",
                () => placement.CropRight, n => placement.CropRight = n, right, "crop"));
            _journal.Do(new SetValueCommand<double>(
                Cue.Id, "cropBottom", "video",
                () => placement.CropBottom, n => placement.CropBottom = n, bottom, "crop"));
        }

        _journal.CloseGroup();
        ApplyLivePlacement(Cue, placement);
        Reload();
    }

    // ── the two layer effects ─────────────────────────────────────────────────────────────────
    // Both follow the app's rule for effects: OFF IS NOT DELETE. Switching a key off keeps the colour
    // and the tolerances, so checking a shot against an unkeyed one costs nothing to undo.

    public bool HasChromaKey => Placement?.ChromaKey is not null;

    public bool ChromaKeyOn
    {
        get => Placement is { ChromaKeyEnabled: true, ChromaKey: not null };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (_journal.Composite(value ? "chroma key on" : "chroma key off", "video"))
            {
                // Created on first use, and never destroyed by switching off.
                if (value && placement.ChromaKey is null)
                    _journal.Do(new SetValueCommand<ChromaKeySpec?>(
                        Cue.Id, "chromaKey", "video",
                        () => placement.ChromaKey, spec => placement.ChromaKey = spec,
                        new ChromaKeySpec(), "add chroma key"));

                _journal.Do(new SetValueCommand<bool>(
                    Cue.Id, "chromaKeyEnabled", "video",
                    () => placement.ChromaKeyEnabled, flag => placement.ChromaKeyEnabled = flag, value,
                    value ? "chroma key on" : "chroma key off"));
            }

            _journal.CloseGroup();
            Reload();
        }
    }

    public double ChromaSimilarity
    {
        get => Placement?.ChromaKey?.Similarity ?? 0.4;
        set => WriteKey("similarity", 0, 1, key => key.Similarity, (key, n) => key.Similarity = n, value);
    }

    public double ChromaSmoothness
    {
        get => Placement?.ChromaKey?.Smoothness ?? 0.1;
        set => WriteKey("smoothness", 0, 1, key => key.Smoothness, (key, n) => key.Smoothness = n, value);
    }

    public double ChromaSpill
    {
        get => Placement?.ChromaKey?.SpillReduction ?? 0.1;
        set => WriteKey("spill", 0, 1,
            key => key.SpillReduction, (key, n) => key.SpillReduction = n, value);
    }

    /// <summary>The key colour as "#RRGGBB" — what a designer is given on a call sheet.</summary>
    public string ChromaColour
    {
        get => Placement?.ChromaKey is { } key
            ? $"#{Channel(key.Red)}{Channel(key.Green)}{Channel(key.Blue)}"
            : "#00FF00";
        set
        {
            if (Placement?.ChromaKey is not { } key || Cue is null || !TryColour(value, out var rgb))
                return;

            using (_journal.Composite("chroma key colour", "video"))
            {
                _journal.Do(new SetValueCommand<double>(
                    Cue.Id, "keyRed", "video", () => key.Red, n => key.Red = n, rgb.Red, "key colour"));
                _journal.Do(new SetValueCommand<double>(
                    Cue.Id, "keyGreen", "video",
                    () => key.Green, n => key.Green = n, rgb.Green, "key colour"));
                _journal.Do(new SetValueCommand<double>(
                    Cue.Id, "keyBlue", "video", () => key.Blue, n => key.Blue = n, rgb.Blue, "key colour"));
            }

            _journal.CloseGroup();
            Reload();
        }
    }

    private static string Channel(double value) =>
        ((int)Math.Round(Math.Clamp(value, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture);

    private static bool TryColour(string text, out (double Red, double Green, double Blue) rgb)
    {
        rgb = default;
        var hex = (text ?? "").Trim().TrimStart('#');

        if (hex.Length != 6
            || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;

        rgb = (((packed >> 16) & 0xFF) / 255d, ((packed >> 8) & 0xFF) / 255d, (packed & 0xFF) / 255d);
        return true;
    }

    private void WriteKey(
        string property,
        double minimum,
        double maximum,
        Func<ChromaKeySpec, double> read,
        Action<ChromaKeySpec, double> write,
        double value)
    {
        if (Placement?.ChromaKey is not { } key || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);

        if (Math.Abs(read(key) - clamped) < 0.000001)
            return;

        Edit(property, "video", () => read(key), number => write(key, number), clamped, "adjust key");
    }

    public bool ColorAdjustOn
    {
        get => Placement is { ColorAdjustEnabled: true, ColorAdjust: not null };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (_journal.Composite(value ? "colour adjust on" : "colour adjust off", "video"))
            {
                if (value && placement.ColorAdjust is null)
                    _journal.Do(new SetValueCommand<ColorAdjustSpec?>(
                        Cue.Id, "colorAdjust", "video",
                        () => placement.ColorAdjust, spec => placement.ColorAdjust = spec,
                        new ColorAdjustSpec(), "add colour adjust"));

                _journal.Do(new SetValueCommand<bool>(
                    Cue.Id, "colorAdjustEnabled", "video",
                    () => placement.ColorAdjustEnabled, flag => placement.ColorAdjustEnabled = flag,
                    value, value ? "colour adjust on" : "colour adjust off"));
            }

            _journal.CloseGroup();
            Reload();
        }
    }

    public double LayerBrightness
    {
        get => Placement?.ColorAdjust?.Brightness ?? 0;
        set => WriteColour("brightness", -1, 1,
            colour => colour.Brightness, (colour, n) => colour.Brightness = n, value);
    }

    public double LayerContrast
    {
        get => Placement?.ColorAdjust?.Contrast ?? 1;
        set => WriteColour("contrast", 0, 4,
            colour => colour.Contrast, (colour, n) => colour.Contrast = n, value);
    }

    private void WriteColour(
        string property,
        double minimum,
        double maximum,
        Func<ColorAdjustSpec, double> read,
        Action<ColorAdjustSpec, double> write,
        double value)
    {
        if (Placement?.ColorAdjust is not { } colour || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);

        if (Math.Abs(read(colour) - clamped) < 0.000001)
            return;

        Edit(property, "video", () => read(colour), number => write(colour, number), clamped,
            "adjust layer colour");
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

        var compositionId = Placement?.CompositionId;
        if (CuePlacements.Of(cue).FirstOrDefault(item => item.LayerIndex == gesture.Layer
                                                  && item.CompositionId == compositionId)
            is not { } placement)
            return;

        // The view-model raises the placement properties below, so journal observers do not need to
        // rebuild the entire shell for every pointer pixel. They see one finished edit on release.
        _drag ??= _journal.Composite("move layer", "video", quiet: true);
        _journal.Do(RectEdits.Placement(cue, placement, gesture.Rect));
        ApplyLivePlacement(cue, placement);
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(PlacementGuidesX));
        OnPropertyChanged(nameof(PlacementGuidesY));
        OnPropertyChanged(nameof(PlacementAspect));
        OnPropertyChanged(nameof(HasPlacement));
        OnPropertyChanged(nameof(PlacementList));
        OnPropertyChanged(nameof(PlacementHeaders));
        OnPropertyChanged(nameof(PlacementCompositions));
        OnPropertyChanged(nameof(PlacementCompositionIndex));
        OnPropertyChanged(nameof(LayerValue));
        OnPropertyChanged(nameof(FitIndex));
        OnPropertyChanged(nameof(PlacementOpacity));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
        OnPropertyChanged(nameof(PlacementRotation));
        OnPropertyChanged(nameof(CropLeft));
        OnPropertyChanged(nameof(CropTop));
        OnPropertyChanged(nameof(CropRight));
        OnPropertyChanged(nameof(CropBottom));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
    }

    /// <summary>Closes the drag's undo step. Called when the pointer is released or a nudge lands.</summary>
    public void EndPlacementGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    private static async Task ObserveLivePlacementAsync(
        ShowHost? host,
        Guid cueId,
        LayerPlacement placement)
    {
        if (host is null)
            return;

        try
        {
            await host.UpdateActivePlacementAsync(cueId, placement).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A pointer release can race application/engine shutdown; the persisted edit still stands.
        }
    }

    private void ApplyLivePlacement(CueNode? cue, LayerPlacement placement)
    {
        if (cue is not null)
            _ = ObserveLivePlacementAsync(Host, cue.Id, placement);
    }

    // ── the Group pane ────────────────────────────────────────────────────────────────────────

    private GroupCueNode? Group => Cue as GroupCueNode;

    public IReadOnlyList<string> FireModes { get; } =
        ["all together", "playlist", "timeline", "first cue only", "armed list · one per GO"];

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
    public bool IsSequencedGroup => Group is
        { FireMode: GroupFireMode.Playlist or GroupFireMode.ArmedList };

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

    /// <summary>
    /// Never open a pass with the item that closed the previous one.
    /// </summary>
    /// <remarks>
    /// Only meaningful while shuffling — an in-order playlist repeats by construction, and hiding the
    /// checkbox is clearer than offering one that does nothing.
    /// </remarks>
    public bool AvoidRepeatValue
    {
        get => Group is not { AvoidImmediateRepeat: false };
        set
        {
            if (Group is { } group && value != group.AvoidImmediateRepeat)
                Edit("avoidRepeat", "cues",
                    () => group.AvoidImmediateRepeat, on => group.AvoidImmediateRepeat = on, value,
                    value ? "avoid immediate repeats" : "allow immediate repeats");
        }
    }

    /// <summary>Passes through the list. Zero is forever, which is what the field's zero means.</summary>
    public int LoopCountValue
    {
        get => Group?.LoopCount ?? 1;
        set
        {
            if (Group is { } group && value >= 0 && value != group.LoopCount)
                Edit("loopCount", "cues",
                    () => group.LoopCount, count => group.LoopCount = count, Math.Clamp(value, 0, 999),
                    value == 0 ? "loop forever" : $"play {value} pass(es)");
        }
    }

    /// <summary>Blank means every enabled child; otherwise a subset per pass.</summary>
    public decimal? PlayCountValue
    {
        get => Group?.PlayCount;
        set
        {
            if (Group is not { } group)
                return;
            var count = value is null ? (int?)null : Math.Max(1, (int)value.Value);
            if (count == group.PlayCount)
                return;
            Edit("playCount", "cues", () => group.PlayCount, set => group.PlayCount = set,
                count, count is null ? "play every item per pass" : $"play {count} item(s) per pass");
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

    // ── the per-kind panes ────────────────────────────────────────────────────────────────────
    // Every control-flow kind EXECUTES (the transport resolves them app-side), so every one of them
    // has to be authorable. Until this landed the only working fade, jump, patch and action cues in
    // existence were the ones the fixture generator wrote — the panes were literals, so the transport
    // could fire a cue the editor could not create.

    private FadeCueNode? Fade => Cue as FadeCueNode;
    private JumpCueNode? Jump => Cue as JumpCueNode;
    private ActionCueNode? Action => Cue as ActionCueNode;
    private PatchCueNode? Patch => Cue as PatchCueNode;
    // ── text cues ─────────────────────────────────────────────────────────────────────────────
    // The document stores WORDS; the FRAMEWORK's text source draws them. Everything here is part of
    // the render spec the compiler packs into the cue's `text:` URI, so an edit here changes the URI
    // and the next fire opens the new card. There is no cache in the app to invalidate.

    private TextCueNode? Card => Cue as TextCueNode;

    public string CardText
    {
        get => Card?.Text ?? "";
        set => EditCard("text", card => card.Text, (card, text) => card.Text = text, value ?? "");
    }

    /// <summary>
    /// The face, or empty for the app's own.
    /// </summary>
    /// <remarks>
    /// A hint, matched the way an audio line's device name is: a booth machine may not have the face a
    /// show was authored with, and falling back to something readable beats refusing to draw.
    /// </remarks>
    public string CardFont
    {
        get => Card?.FontFamily ?? "";
        set => EditCard("font",
            card => card.FontFamily, (card, name) => card.FontFamily = name, (value ?? "").Trim());
    }

    /// <summary>Cap height as a fraction of the frame, so the card survives a canvas resize.</summary>
    public double CardSize
    {
        get => Card?.FontScale ?? 0.12;
        set => EditCard("fontScale",
            card => card.FontScale, (card, scale) => card.FontScale = scale,
            Math.Clamp(value, 0.01, 1));
    }

    public bool CardBold
    {
        get => Card?.Bold == true;
        set => EditCard("bold", card => card.Bold, (card, on) => card.Bold = on, value);
    }

    public bool CardItalic
    {
        get => Card?.Italic == true;
        set => EditCard("italic", card => card.Italic, (card, on) => card.Italic = on, value);
    }

    public string CardInk
    {
        get => Card?.Foreground ?? "#FFFFFF";
        set => EditCard("foreground",
            card => card.Foreground, (card, hex) => card.Foreground = hex, Hex(value, "#FFFFFF"));
    }

    /// <summary>The ground, or empty for a transparent card that sits over whatever is underneath.</summary>
    public string CardGround
    {
        get => Card?.Background ?? "";
        set => EditCard("background",
            card => card.Background, (card, hex) => card.Background = hex, Hex(value, ""));
    }

    public IReadOnlyList<string> CardAligns { get; } = ["left", "centre", "right"];

    public int CardAlignIndex
    {
        get => Card is { } card ? (int)card.Align : -1;
        set
        {
            if (value >= 0)
                EditCard("align", card => card.Align, (card, align) => card.Align = align,
                    (TextAlign)value);
        }
    }

    public IReadOnlyList<string> CardAnchors { get; } = ["top", "middle", "bottom"];

    public int CardAnchorIndex
    {
        get => Card is { } card ? (int)card.Anchor : -1;
        set
        {
            if (value >= 0)
                EditCard("anchor", card => card.Anchor, (card, anchor) => card.Anchor = anchor,
                    (TextAnchor)value);
        }
    }

    /// <summary>An outline behind the ink — what makes a caption readable over picture.</summary>
    public double CardOutline
    {
        get => Card?.OutlineWidth ?? 0;
        set => EditCard("outlineWidth",
            card => card.OutlineWidth, (card, width) => card.OutlineWidth = width,
            Math.Clamp(value, 0, 0.1));
    }

    public string CardOutlineInk
    {
        get => Card?.Outline ?? "#000000";
        set => EditCard("outline",
            card => card.Outline, (card, hex) => card.Outline = hex, Hex(value, "#000000"));
    }

    /// <summary>How long the card is held. Zero holds it until something stops it.</summary>
    public int CardDuration
    {
        get => Card?.DurationMs ?? 0;
        set => EditCard("duration",
            card => card.DurationMs, (card, ms) => card.DurationMs = ms,
            Math.Clamp(value, 0, 3_600_000));
    }

    public int CardFadeIn
    {
        get => Card?.FadeInMs ?? 0;
        set => EditCard("fadeIn", card => card.FadeInMs, (card, ms) => card.FadeInMs = ms,
            Math.Clamp(value, 0, 60_000));
    }

    public int CardFadeOut
    {
        get => Card?.FadeOutMs ?? 0;
        set => EditCard("fadeOut", card => card.FadeOutMs, (card, ms) => card.FadeOutMs = ms,
            Math.Clamp(value, 0, 60_000));
    }

    /// <summary>What the card will do, said on the pane rather than discovered on stage.</summary>
    public string CardHint => Card is not { } card
        ? ""
        : card.Text.Trim().Length == 0
            ? "no words yet — the cue will fire and show nothing"
            : card.Placements.Count == 0
                ? "not on any canvas yet — add a placement on the Video tab"
                : card.DurationMs > 0
                    ? $"held for {card.DurationMs / 1000d:0.##} s, then ends on its own"
                    : "held on screen until something stops it";

    /// <summary>
    /// A typed colour as "#RRGGBB", or the fallback when it is not one.
    /// </summary>
    /// <remarks>
    /// The DIGITS are checked, not just the length. Anything seven characters long used to be accepted,
    /// so "#ZZZZZZ" was stored and shown back as though it were a colour — while the compiler quietly
    /// fell back to white, and the card came up a colour the inspector never displayed.
    /// </remarks>
    private static string Hex(string? value, string fallback)
    {
        var text = (value ?? "").Trim().TrimStart('#');

        return text.Length == 6 && text.All(char.IsAsciiHexDigit)
            ? '#' + text.ToUpperInvariant()
            : fallback;
    }

    private void EditCard<T>(
        string property, Func<TextCueNode, T> read, Action<TextCueNode, T> write, T value)
    {
        if (Card is not { } card || EqualityComparer<T>.Default.Equals(read(card), value))
            return;

        Edit(property, "cues", () => read(card), number => write(card, number), value, "edit text cue");
    }

    private VisualizerCueNode? Visualizer => Cue as VisualizerCueNode;

    // ── FADE ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The logical outputs this fade acts on, each with its own checkbox.
    /// </summary>
    /// <remarks>
    /// A live list rather than a picker dialog: choosing which outputs a fade covers is something an
    /// operator does WHILE looking at the patch, and a modal that hides the rest of the pane to ask
    /// one question is the wrong shape for it.
    /// </remarks>
    public IReadOnlyList<TargetToggle> FadeTargets =>
        Fade is not { } fade
            ? []
            : [.. Project.AudioPatch.LogicalChannels
                .OrderBy(channel => channel.SortOrder)
                .Select(channel => new TargetToggle(
                    channel.Name,
                    fade.TargetChannelIds.Contains(channel.Id),
                    on => ToggleFadeTarget(fade, channel.Id, on)))];

    private void ToggleFadeTarget(FadeCueNode fade, Guid channelId, bool on)
    {
        if (on == fade.TargetChannelIds.Contains(channelId))
            return;

        var target = fade;
        var next = new List<Guid>(fade.TargetChannelIds);

        if (on)
            next.Add(channelId);
        else
            next.Remove(channelId);

        Edit("fadeTargets", "cues",
            () => target.TargetChannelIds, ids => target.TargetChannelIds = ids, next,
            on ? "add fade target" : "remove fade target");
    }

    public string FadeToLevelValue
    {
        get => Fade is { } fade
            ? fade.ToLevelDb <= GainRange.SilenceFloorDb ? "−inf" : CuePresentation.Db(fade.ToLevelDb)
            : "—";
        set
        {
            if (Fade is not { } fade)
                return;

            // "−inf", "-inf" and "off" all mean the silence floor. An operator typing the word is the
            // commonest way to author a fade-out, and refusing it would send them to look up a number.
            var level = value.Trim().Replace('−', '-');

            var db = level.Equals("-inf", StringComparison.OrdinalIgnoreCase)
                     || level.Equals("inf", StringComparison.OrdinalIgnoreCase)
                     || level.Equals("off", StringComparison.OrdinalIgnoreCase)
                ? GainRange.SilenceFloorDb
                : TryParseDb(value, out var parsed) ? parsed : double.NaN;

            if (double.IsNaN(db))
                return;

            var target = fade;
            Edit("fadeLevel", "cues",
                () => target.ToLevelDb, set => target.ToLevelDb = set,
                Math.Clamp(db, GainRange.SilenceFloorDb, 12), "set fade level");
        }
    }

    public string FadeDurationValue
    {
        get => Fade is { } fade ? CuePresentation.Seconds(fade.DurationMs) : "—";
        set
        {
            if (Fade is { } fade && TryParseSeconds(value, out var ms))
            {
                var target = fade;
                Edit("fadeDuration", "cues",
                    () => target.DurationMs, set => target.DurationMs = set, ms, "set fade duration");
            }
        }
    }

    public bool FadeEverythingValue
    {
        get => Fade is { FadeEverythingSounding: true };
        set
        {
            if (Fade is not { } fade || value == fade.FadeEverythingSounding)
                return;

            var target = fade;
            Edit("fadeEverything", "cues",
                () => target.FadeEverythingSounding, on => target.FadeEverythingSounding = on, value,
                value ? "fade everything sounding" : "fade only the targets");
        }
    }

    public bool FadeStopsTargetsValue
    {
        get => Fade is not { StopTargetsWhenComplete: false };
        set
        {
            if (Fade is not { } fade || value == fade.StopTargetsWhenComplete)
                return;

            var target = fade;
            Edit("fadeStops", "cues",
                () => target.StopTargetsWhenComplete, on => target.StopTargetsWhenComplete = on, value,
                value ? "stop targets when complete" : "leave targets running");
        }
    }

    /// <summary>Whether the fade covers anything. A fade with no target is a cue that does nothing.</summary>
    public string FadeTargetHint => Fade is not { } fade
        ? ""
        : fade.FadeEverythingSounding
            ? "everything sounding — the per-output list below is ignored"
            : fade.TargetChannelIds.Count + fade.TargetCueIds.Count == 0
                ? "no target — this cue will do nothing"
                : $"{fade.TargetChannelIds.Count + fade.TargetCueIds.Count} target(s)";

    // ── JUMP ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every cue in the show, as jump destinations. A jump may legitimately cross lists.</summary>
    public IReadOnlyList<string> JumpTargets =>
        Jump is null
            ? []
            : ["— none —", .. Project.AllCues()
                .Where(cue => cue.Id != Jump.Id)
                .Select(cue => $"Q{CuePresentation.Number(cue.Number)} · {cue.Label}")];

    public int JumpTargetIndex
    {
        get
        {
            if (Jump is not { TargetCueIds.Count: > 0 } jump)
                return 0;

            var candidates = Candidates(jump);
            var at = candidates.FindIndex(cue => cue.Id == jump.TargetCueIds[0]);

            // A target that has been deleted reads as "none" rather than pointing at whatever now
            // occupies that position — Project status reports the dangling reference separately.
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Jump is not { } jump || value < 0)
                return;

            var candidates = Candidates(jump);
            var chosen = value == 0 || value > candidates.Count
                ? new List<Guid>()
                : [candidates[value - 1].Id];

            if (chosen.SequenceEqual(jump.TargetCueIds))
                return;

            var target = jump;
            Edit("jumpTarget", "cues",
                () => target.TargetCueIds, ids => target.TargetCueIds = ids, chosen,
                chosen.Count == 0 ? "clear jump target" : "set jump target");
        }
    }

    private List<CueNode> Candidates(JumpCueNode jump) =>
        [.. Project.AllCues().Where(cue => cue.Id != jump.Id)];

    public IReadOnlyList<string> JumpConditions { get; } =
        ["always", "while the trigger is held", "n times, then continue"];

    public int JumpConditionIndex
    {
        get => Jump is { } jump ? (int)jump.Condition : -1;
        set
        {
            if (Jump is not { } jump || value < 0 || (JumpCondition)value == jump.Condition)
                return;

            var target = jump;
            Edit("jumpCondition", "cues",
                () => target.Condition, condition => target.Condition = condition, (JumpCondition)value,
                $"jump {JumpConditions[value]}");
            OnPropertyChanged(nameof(IsCountedJump));
        }
    }

    public bool IsCountedJump => Jump?.Condition == JumpCondition.CountThenContinue;

    public string JumpCountValue
    {
        get => Jump?.JumpCount.ToString(CultureInfo.CurrentCulture) ?? "1";
        set
        {
            if (Jump is not { } jump
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count))
                return;

            count = Math.Clamp(count, 1, 10_000);
            Edit("jumpCount", "cues",
                () => jump.JumpCount, number => jump.JumpCount = number, count,
                $"jump {count} times, then continue");
        }
    }

    public bool JumpPickAtRandomValue
    {
        get => Jump is { PickAtRandom: true };
        set
        {
            if (Jump is not { } jump || value == jump.PickAtRandom)
                return;

            var target = jump;
            Edit("jumpRandom", "cues",
                () => target.PickAtRandom, on => target.PickAtRandom = on, value,
                value ? "pick at random" : "always the first target");
        }
    }

    public bool JumpFiresOnArrivalValue
    {
        get => Jump is not { FireOnArrival: false };
        set
        {
            if (Jump is not { } jump || value == jump.FireOnArrival)
                return;

            var target = jump;
            Edit("jumpFires", "cues",
                () => target.FireOnArrival, on => target.FireOnArrival = on, value,
                value ? "fire on arrival" : "move standby only");
        }
    }

    /// <summary>Said in the editor rather than left for Project status to find at a get-in.</summary>
    public string JumpHint => Jump is not { } jump
        ? ""
        : jump.TargetCueIds.Count == 0
            ? "no target — a jump with nowhere to go is an error on Project status, not a silent no-op"
            : jump.TargetCueIds.Any(id => Project.FindCue(id) is null)
                ? "a target no longer exists in this show"
                : "";

    // ── ACTION ────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ActionEndpoints =>
        Action is null
            ? []
            : ["— none —", .. Project.ActionEndpoints.Select(
                endpoint => $"{endpoint.Name} · {Describe(endpoint.Kind)}")];

    public int ActionEndpointIndex
    {
        get
        {
            if (Action?.EndpointId is not { } id)
                return 0;

            var at = Project.ActionEndpoints.FindIndex(endpoint => endpoint.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Action is not { } action || value < 0)
                return;

            var chosen = value == 0 || value > Project.ActionEndpoints.Count
                ? (Guid?)null
                : Project.ActionEndpoints[value - 1].Id;

            if (chosen == action.EndpointId)
                return;

            var target = action;
            Edit("actionEndpoint", "cues",
                () => target.EndpointId, id => target.EndpointId = id, chosen, "set action endpoint");
        }
    }

    public string ActionAddressValue
    {
        get => Action?.Address ?? "";
        set
        {
            if (Action is not { } action)
                return;

            var target = action;
            Edit("actionAddress", "cues",
                () => target.Address, address => target.Address = address, value, "set action address");
        }
    }

    public string ActionArgumentsValue
    {
        get => Action?.Arguments ?? "";
        set
        {
            if (Action is not { } action)
                return;

            var target = action;
            Edit("actionArguments", "cues",
                () => target.Arguments, args => target.Arguments = args, value, "set action arguments");
        }
    }

    /// <summary>
    /// What this action will actually do — or what is wrong with it.
    /// </summary>
    /// <remarks>
    /// For a MIDI endpoint this is the PARSER's own verdict, and the same check runs in the status
    /// pass, so the hint and "will this show run" can never disagree. Saying it HERE means the operator
    /// finds out while authoring rather than when the desk fails to respond.
    /// </remarks>
    public string ActionHint
    {
        get
        {
            if (Action is not { } action)
                return "";

            if (action.EndpointId is not { } id
                || Project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == id) is not { } endpoint)
                return "no endpoint — this cue will do nothing";

            if (endpoint.Kind == EndpointKind.MidiOut)
            {
                // The parser's own verdict rather than a description of the syntax: an operator who has
                // typed something wrong wants to know WHAT is wrong, and the same check runs in the
                // status pass, so the two can never disagree about whether this cue will send.
                return MidiActions.TryParse(action.Address, action.Arguments, out var message) is { } wrong
                    ? wrong
                    : $"sends {Describe(message)} · channels are 1–16, values 0–127";
            }

            return action.Address.Length == 0
                ? "no address — this cue will do nothing"
                : "arguments are whitespace-separated and typed by shape: 3 is an int, 3.0 a float";
        }
    }

    private static string Describe(EndpointKind kind) =>
        kind == EndpointKind.MidiOut ? "MIDI out" : "OSC out";

    /// <summary>A parsed MIDI message in the words a desk's manual uses.</summary>
    private static string Describe(MidiAction message) => message.Kind switch
    {
        MidiActionKind.ControlChange =>
            $"CC {message.Number} = {message.Value} on ch {message.Channel}",
        MidiActionKind.ProgramChange =>
            $"program {message.Number} on ch {message.Channel}",
        MidiActionKind.NoteOff =>
            $"note {message.Number} off on ch {message.Channel}",
        _ => $"note {message.Number} on ch {message.Channel} at velocity {message.Value}",
    };

    // ── PATCH ─────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> PatchSnapshots =>
        Patch is null
            ? []
            : ["— none —", .. Project.PatchSnapshots.Select(
                snapshot => $"snapshot “{snapshot.Name}”")];

    public int PatchSnapshotIndex
    {
        get
        {
            if (Patch?.SnapshotId is not { } id)
                return 0;

            var at = Project.PatchSnapshots.FindIndex(snapshot => snapshot.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Patch is not { } patch || value < 0)
                return;

            var chosen = value == 0 || value > Project.PatchSnapshots.Count
                ? (Guid?)null
                : Project.PatchSnapshots[value - 1].Id;

            if (chosen == patch.SnapshotId)
                return;

            var target = patch;
            Edit("patchSnapshot", "cues",
                () => target.SnapshotId, id => target.SnapshotId = id, chosen, "set patch snapshot");
        }
    }

    public string PatchFadeValue
    {
        get => Patch is { } patch ? CuePresentation.Seconds(patch.FadeMs) : "—";
        set
        {
            if (Patch is { } patch && TryParseSeconds(value, out var ms))
            {
                var target = patch;
                Edit("patchFade", "cues",
                    () => target.FadeMs, set => target.FadeMs = set, ms, "set patch fade");
            }
        }
    }

    /// <summary>The cue's inline level changes, as the pane lists them.</summary>
    /// <remarks>
    /// Read from the document rather than authored: the pane used to show two fixed rows ("Fold L/R",
    /// "Sub") whatever the cue actually carried, so a patch cue with three changes showed two and one
    /// with none still showed two.
    /// </remarks>
    public IReadOnlyList<string> PatchLevelChanges =>
        Patch is null
            ? []
            : [.. Patch.Levels.Select(change =>
                $"{Project.FindChannel(change.LogicalChannelId)?.Name ?? "(deleted output)"} → "
                + (change.Muted ? "mute" : CuePresentation.Db(change.GainDb)))];

    public bool HasPatchLevelChanges => PatchLevelChanges.Count > 0;

    public string PatchHint => Patch is not { } patch
        ? ""
        : patch.SnapshotId is null && patch.Levels.Count == 0
            ? "nothing to recall — this cue will do nothing"
            : "";

    // ── VISUALIZER ────────────────────────────────────────────────────────────────────────────

    public string VisualizerPresetPackValue
    {
        get => Visualizer?.PresetPack ?? "";
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var target = visualizer;
            Edit("presetPack", "cues",
                () => target.PresetPack, pack => target.PresetPack = pack, value, "set preset pack");
        }
    }

    public string VisualizerHoldValue
    {
        get => Visualizer is { } visualizer ? CuePresentation.Seconds(visualizer.HoldMs) : "—";
        set
        {
            if (Visualizer is { } visualizer && TryParseSeconds(value, out var ms))
            {
                var target = visualizer;
                Edit("visualizerHold", "cues",
                    () => target.HoldMs, set => target.HoldMs = set, ms, "set preset hold");
            }
        }
    }

    public string VisualizerBlendValue
    {
        get => Visualizer is { } visualizer ? CuePresentation.Seconds(visualizer.BlendMs) : "—";
        set
        {
            if (Visualizer is { } visualizer && TryParseSeconds(value, out var ms))
            {
                var target = visualizer;
                Edit("visualizerBlend", "cues",
                    () => target.BlendMs, set => target.BlendMs = set, ms, "set preset blend");
            }
        }
    }

    public bool VisualizerLocksPresetValue
    {
        get => Visualizer is { LockPreset: true };
        set
        {
            if (Visualizer is not { } visualizer || value == visualizer.LockPreset)
                return;

            var target = visualizer;
            Edit("visualizerLock", "cues",
                () => target.LockPreset, on => target.LockPreset = on, value,
                value ? "lock the preset" : "auto-advance presets");
        }
    }

    public bool VisualizerFeedAllValue
    {
        get => Visualizer is not { FeedAll: false };
        set
        {
            if (Visualizer is not { } visualizer || value == visualizer.FeedAll)
                return;
            Edit("visualizerFeedAll", "cues", () => visualizer.FeedAll,
                on => visualizer.FeedAll = on, value,
                value ? "feed all sounding media to the visualizer" : "use a selective visualizer feed");
        }
    }

    public string VisualizerFeedCueNumbers
    {
        get => Visualizer is not { } visualizer
            ? ""
            : string.Join(", ", visualizer.FeedCueIds
                .Select(Project.FindCue)
                .OfType<MediaCueNode>()
                .Select(cue => CuePresentation.Number(cue.Number)));
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var tokens = value.Split([',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var wanted = Project.AllCues().OfType<MediaCueNode>()
                .Where(cue => tokens.Contains(CuePresentation.Number(cue.Number), StringComparer.OrdinalIgnoreCase))
                .Select(cue => cue.Id)
                .Distinct()
                .ToList();
            if (wanted.SequenceEqual(visualizer.FeedCueIds))
                return;
            Edit("visualizerFeedCues", "cues", () => visualizer.FeedCueIds,
                ids => visualizer.FeedCueIds = ids, wanted, "set visualizer audio feed cues");
        }
    }

    public string VisualizerFeedHint => Visualizer is not { } visualizer
        ? ""
        : visualizer.FeedAll
            ? "program bus · every sounding cue"
            : $"{visualizer.FeedCueIds.Count} explicit cue(s) plus every media cue marked “send to visualizer”";

    /// <summary>
    /// What a visualizer cue will actually do on THIS machine.
    /// </summary>
    /// <remarks>
    /// projectM is a native library a booth box may not have, and the settings above are perfectly
    /// editable without it — so the honest hint is the machine's answer, not a fixed sentence. A cue
    /// authored on a laptop with no library still travels and still runs at the venue.
    /// </remarks>
    public string VisualizerHint =>
        HaCue2.Engine.ProjectVisualizers.IsAvailable
            ? "renders onto every composition this cue is placed on · fires and stops like any other cue"
            : "projectM is not available on this machine — "
              + (HaCue2.Engine.ProjectVisualizers.UnavailableReason ?? "the library was not found")
              + " · the settings still travel with the show";

    // ── media tracks ──────────────────────────────────────────────────────────────────────────
    // The options are a MACHINE fact and the choice is a DOCUMENT one, so the picker takes both: the
    // probe's track list and the cue it writes to.

    /// <summary>What the selected cue's file turned out to contain. Set by the shell as probes land.</summary>
    public MediaFacts? Facts { get; set; }

    /// <summary>Where derived files live, so the clip editor can cache a scan. Set by the shell.</summary>
    public string CacheRoot { get; set; } = "";

    /// <summary>The machine's cap for waveform data; null keeps it unbounded.</summary>
    public long? WaveformCacheBytes { get; set; }

    /// <summary>The current project filename, read lazily so Save As immediately changes resolution.</summary>
    public Func<string?> ProjectPath { get; set; } = static () => null;

    /// <summary>Resolves a prepared source URI to the verified local asset used for playback.</summary>
    public Func<string, string?> PreparedMediaPath { get; set; } = static _ => null;

    private TimeSpan? ClipDuration => Facts?.Duration
        ?? (Cue is MediaCueNode { SourceDurationMs: > 0 } media
            ? TimeSpan.FromMilliseconds(media.SourceDurationMs)
            : null);

    /// <summary>
    /// Whether there is a FILE to open a clip editor on.
    /// </summary>
    /// <remarks>
    /// A live source has none. A prepared YouTube cue does: its portable URI is resolved through the
    /// cache to the exact local asset the playback provider opens.
    /// </remarks>
    public bool CanEditClip => Cue is MediaCueNode media && ClipPath(media) is { Length: > 0 };

    public string ClipEditorHint => Cue is MediaCueNode media
        && SourceUri.KindOf(media.MediaPath) == SourceKind.YouTube
        && !CanEditClip
            ? "The graphical editor becomes available when this cue's background download is ready."
            : "Open the graphical waveform and frame trimming editor.";

    /// <summary>Rechecks the machine-local prepared asset without resetting the inspector's tab.</summary>
    public void RefreshPreparedMedia()
    {
        OnPropertyChanged(nameof(CanEditClip));
        OnPropertyChanged(nameof(ClipEditorHint));
    }

    /// <summary>Whether an in/out window has meaning for this media cue.</summary>
    /// <remarks>
    /// YouTube cues play a prepared finite asset and can use numeric trim points even though the
    /// waveform editor cannot open their URI directly. Cameras and capture devices are live and have
    /// neither a stable beginning nor an end to trim against.
    /// </remarks>
    public bool CanTrimMedia => Cue is MediaCueNode media && !SourceUri.IsLive(media.MediaPath);

    /// <summary>
    /// The clip editor for the selected cue, or null when there is nothing to edit.
    /// </summary>
    /// <remarks>
    /// The path is RESOLVED here, against the project's own roots, because the editor opens a file and
    /// the document stores a reference — the same resolution the engine does when it plays the cue, so
    /// what is trimmed is what will be played.
    /// </remarks>
    public ClipEditorViewModel? ClipEditor() =>
        !CanEditClip || Cue is not MediaCueNode media
            ? null
            : new ClipEditorViewModel(
                _journal,
                media,
                ClipPath(media)!,
                ClipDuration,
                CacheRoot,
                WaveformCacheBytes);

    private string? ClipPath(MediaCueNode media) => SourceUri.KindOf(media.MediaPath) switch
    {
        SourceKind.File when media.MediaPath.Length > 0 =>
            MediaPaths.Resolve(Project, media.MediaPath, ProjectPath()),
        SourceKind.YouTube => PreparedMediaPath(media.MediaPath),
        _ => null,
    };

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

    /// <summary>
    /// One expander header per placement, so several can be organised without one anonymous editor.
    /// </summary>
    /// <remarks>
    /// A placement carries geometry, a fit, an opacity, a crop, a chroma key and a colour adjust — the
    /// better part of a screen each. With a picker above one editor, a cue on three canvases meant
    /// three screens of settings reachable only by remembering which entry was which. Each is now an
    /// expander, and opening one selects the placement that its nested editor projects.
    /// </remarks>
    public IReadOnlyList<PlacementHeader> PlacementHeaders =>
    [
        .. PlacementList.Select((placement, index) => new PlacementHeader(
            index,
            Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId)?.Name
            ?? "no composition",
            $"L{placement.LayerIndex}",
            Summarize(placement),
            index == SelectedPlacement)),
    ];

    /// <summary>Where the layer sits, as the one line a collapsed row can afford.</summary>
    private static string Summarize(LayerPlacement placement)
    {
        var full = placement is { X: 0, Y: 0, Width: 1, Height: 1 };
        var geometry = full
            ? "full frame"
            : $"{placement.X:0.##}, {placement.Y:0.##} · {placement.Width:0.##}×{placement.Height:0.##}";

        var extras = new List<string>();
        if (placement.Opacity < 1)
            extras.Add($"{placement.Opacity:0.##} opacity");
        if (placement.RotationDegrees != 0)
            extras.Add($"{placement.RotationDegrees:0.#}°");
        if (placement is { CropLeft: > 0 } or { CropTop: > 0 } or { CropRight: > 0 } or { CropBottom: > 0 })
            extras.Add("cropped");
        if (placement is { ChromaKeyEnabled: true, ChromaKey: not null })
            extras.Add("keyed");
        if (placement is { ColorAdjustEnabled: true, ColorAdjust: not null })
            extras.Add("graded");
        if (placement.HasVideoFx)
            extras.Add("mapped");

        return extras.Count == 0 ? geometry : $"{geometry} · {string.Join(" · ", extras)}";
    }

    /// <summary>Opens one placement's row, which is also how it becomes the one being edited.</summary>
    public void ExpandPlacement(int index)
    {
        if (index >= 0 && index < PlacementList.Count)
            SelectedPlacement = index;
    }





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
    //
    // The model, the compile path and the curve editor's own EffectLaneTarget all existed before this
    // landed — what was missing was any way to ADD a lane. Lanes reached the engine only if the fixture
    // generator or the timeline's duck helper happened to write one.

    /// <summary>The lanes on the selected cue, or empty for a kind that cannot carry one.</summary>
    private List<EffectLane>? Lanes => Cue switch
    {
        MediaCueNode media => media.EffectLanes,
        GroupCueNode group => group.EffectLanes,
        VisualizerCueNode visualizer => visualizer.EffectLanes,
        _ => null,
    };

    /// <summary>Whether this cue kind can carry automation at all.</summary>
    public bool CanCarryLanes => Lanes is not null;

    public IReadOnlyList<EffectLaneRow> EffectLanes =>
        Lanes is not { } lanes
            ? []
            : [.. lanes.Select((lane, index) => new EffectLaneRow(
                index,
                lane.Kind.ToString().ToLowerInvariant(),
                Describe(lane),
                lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp))];

    public bool HasEffectLanes => EffectLanes.Count > 0;

    /// <summary>
    /// What a lane will actually do, in a phrase.
    /// </summary>
    /// <remarks>
    /// Says when a lane cannot reach the engine rather than showing a point count that implies it can:
    /// a lane needs more than one point to be an envelope, and — because the compiler measures it
    /// against the cue's PLAYED length — a cue whose media has not been probed yet has no length to
    /// stretch it over.
    /// </remarks>
    private string Describe(EffectLane lane)
    {
        if (lane.Points.Count < 2)
            return "empty — needs at least two points";

        if (lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp)
        {
            return lane.EndpointId is null
                ? $"{lane.Points.Count} points · no endpoint, so nothing is sent"
                : $"{lane.Points.Count} points → {lane.Address}";
        }

        return $"{lane.Points.Count} points";
    }

    public IReadOnlyList<string> LaneKinds { get; } = ["volume", "opacity", "osc ramp", "midi ramp"];

    /// <summary>
    /// Adds a lane of the given kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seeded with two points at unity rather than empty, so the editor opens on something the operator
    /// can grab — the same reason a fade curve opens on a straight line. A flat lane at unity changes
    /// nothing until it is dragged, so adding one is safe mid-show.
    /// </para>
    /// <para>
    /// One lane per kind: a cue with two volume lanes has no defined answer for what its level is, and
    /// the compiler takes the first, so the second would be invisible.
    /// </para>
    /// </remarks>
    public void AddLane(int kindIndex)
    {
        if (Lanes is not { } lanes || Cue is not { } cue || kindIndex < 0 || kindIndex >= LaneKinds.Count)
            return;

        var kind = (EffectLaneKind)kindIndex;

        if (lanes.Any(lane => lane.Kind == kind))
            return;

        var added = new EffectLane
        {
            Kind = kind,
            Points = [new LanePoint(0, 1), new LanePoint(1, 1)],
        };

        _journal.Do(new AddItemCommand<EffectLane>(
            lanes, added, lanes.Count, "cues", $"add {LaneKinds[kindIndex]} lane"));
        _journal.CloseGroup();

        Reload();
    }

    /// <summary>Whether a kind can still be added — the picker greys out what is already there.</summary>
    public bool CanAddLane(int kindIndex) =>
        Lanes is { } lanes
        && kindIndex >= 0
        && kindIndex < LaneKinds.Count
        && lanes.All(lane => lane.Kind != (EffectLaneKind)kindIndex);

    /// <summary>Removes a lane. Undoable, like everything else this panel does.</summary>
    public void RemoveLane(int index)
    {
        if (Lanes is not { } lanes || index < 0 || index >= lanes.Count)
            return;

        _journal.Do(new RemoveItemCommand<EffectLane>(
            lanes, lanes[index], "cues", $"remove {lanes[index].Kind.ToString().ToLowerInvariant()} lane"));
        _journal.CloseGroup();

        Reload();
    }

    /// <summary>
    /// The editor for one lane — the SAME editor a fade curve uses (register item 18).
    /// </summary>
    /// <remarks>
    /// <c>EffectLaneTarget</c> has existed since the journal was written and was never constructed:
    /// the two shapes are one sorted list of normalized points, differing only in whether a point can
    /// hold. One editor was the plan's requirement and is what this returns.
    /// </remarks>
    public CurveEditorViewModel? LaneEditor(int index)
    {
        if (Lanes is not { } lanes || Cue is not { } cue || index < 0 || index >= lanes.Count)
            return null;

        var lane = lanes[index];

        return new CurveEditorViewModel(
            _journal,
            new EffectLaneTarget(cue.Id, lane),
            $"Q{CuePresentation.Number(cue.Number)} · {lane.Kind.ToString().ToLowerInvariant()} lane");
    }

    /// <summary>Configures the endpoint and address for an outbound lane.</summary>
    public PromptViewModel? ConfigureLane(int index)
    {
        if (Lanes is not { } lanes || Cue is not { } cue || index < 0 || index >= lanes.Count)
            return null;

        var lane = lanes[index];
        if (lane.Kind is not (EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp))
            return null;

        var kind = lane.Kind == EffectLaneKind.OscRamp ? EndpointKind.OscOut : EndpointKind.MidiOut;
        var endpoints = _journal.Project.ActionEndpoints.Where(endpoint => endpoint.Kind == kind).ToList();
        var options = endpoints.Count == 0 ? ["(no compatible endpoints)"] : endpoints.Select(e => e.Name).ToList();
        var selected = Math.Max(0, endpoints.FindIndex(endpoint => endpoint.Id == lane.EndpointId));

        return new PromptViewModel(
            $"Configure {lane.Kind.ToString().ToLowerInvariant()} lane",
            endpoints.Count == 0
                ? $"Add a {kind} endpoint in Targets first."
                : "The lane appends its current value to this address/message.",
            [
                new PromptField
                {
                    Label = "Endpoint",
                    Kind = PromptFieldKind.Choice,
                    Options = options,
                    SelectedIndex = selected,
                },
                new PromptField
                {
                    Label = "Address",
                    Value = lane.Address,
                    Hint = lane.Kind == EffectLaneKind.OscRamp
                        ? "/osc/address — the normalized value is its argument"
                        : "cc <channel> <controller> — the lane supplies value 0–127",
                },
            ],
            prompt =>
            {
                if (endpoints.Count == 0)
                    return;

                var endpointIndex = Math.Clamp(prompt["Endpoint"].SelectedIndex, 0, endpoints.Count - 1);
                using (_journal.Composite("configure outbound lane", "cues"))
                {
                    _journal.Do(new SetValueCommand<Guid?>(
                        cue.Id, $"lane:{lane.Id}:endpoint", "cues",
                        () => lane.EndpointId, value => lane.EndpointId = value,
                        endpoints[endpointIndex].Id, "choose lane endpoint"));
                    _journal.Do(new SetValueCommand<string>(
                        cue.Id, $"lane:{lane.Id}:address", "cues",
                        () => lane.Address, value => lane.Address = value,
                        prompt["Address"].Value.Trim(), "set lane address"));
                }
                Reload();
            },
            confirm: "APPLY");
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

/// <summary>
/// One toggleable target in a cue's target list.
/// </summary>
/// <remarks>
/// A tiny view-model rather than a bound model object, because the LIST is derived (every logical
/// output in the project) while the STATE is a membership test against the cue. Binding a checkbox
/// straight to the document would need a property that does not exist on either side.
/// </remarks>
public sealed class TargetToggle(string name, bool selected, Action<bool> apply)
{
    public string Name { get; } = name;

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (value == selected)
                return;

            selected = value;
            apply(value);
        }
    }
}


/// <summary>One automation lane, as the Effects pane lists it.</summary>
/// <param name="Index">Its position in the cue's lane list — what edit and remove act on.</param>
public sealed record EffectLaneRow(int Index, string Kind, string Detail, bool IsOutbound);
