using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Compile;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Controls;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;
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
    private readonly object _livePlacementGate = new();
    private readonly Dictionary<LivePlacementKey, LivePlacementEdit> _pendingLivePlacements = [];
    private bool _livePlacementPublisherRunning;
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
            CueKind.Automation => "automation cue",
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
        // Remembered for EVERY kind in the outgoing selection, not just a single one. A journal
        // change mid-multi-edit clears and restores the tree selection, and each pass comes through
        // here — when only single selections wrote the memory, adding a send on the Audio pane of
        // eleven stems recalled a stale "GENERAL" on the restore and threw the operator out of the
        // pane once per edit. The open tab is in the intersection of the selected kinds' tab sets
        // (see Reload), so it is a valid memory for each of them.
        if (RememberTabs && SelectedTab is { } previous && Cue is not null)
            foreach (var kind in Selected.Select(KindOf).Distinct())
                _rememberedTab[kind] = previous;

        _selection = cueIds;

        // A SELECTION change re-chooses the pane; an edit does not. See Reload.
        Reload(keepTab: false);
    }

    /// <summary>Re-reads the document after an edit or an undo, keeping the selection and the tab.</summary>
    public void Reload() => Reload(keepTab: true);

    /// <param name="keepTab">
    /// True for an EDIT, false for a selection change.
    /// <para>
    /// This is the difference between an inspector that stays where the operator put it and one that
    /// snaps back to General on every keystroke — which is what it did, because every edit ran the tab
    /// choice below from scratch and the remembered tab was only ever written when the SELECTION
    /// changed. Editing a level on the Audio pane therefore recomputed "media cue ⇒ GENERAL" and threw
    /// the operator out of the pane they were working in, once per character.
    /// </para>
    /// </param>
    private void Reload(bool keepTab)
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
            // Still a tab this selection has: stay on it. An edit is not a reason to move.
            : keepTab && SelectedTab is { } open && Tabs.Contains(open)
                ? open
                : RememberTabs
                  && _rememberedTab.TryGetValue(KindOf(lead), out var remembered)
                  && Tabs.Contains(remembered)
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
        OnPropertyChanged(nameof(HasAudioGain));
        OnPropertyChanged(nameof(AudioGainOn));
        OnPropertyChanged(nameof(AudioGainDb));
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
        OnPropertyChanged(nameof(SendPresetTarget));
        OnPropertyChanged(nameof(HasSendPresetTarget));
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
        OnPropertyChanged(nameof(EffectRackRows));
        OnPropertyChanged(nameof(HasChromaKey));
        OnPropertyChanged(nameof(ChromaKeyOn));
        OnPropertyChanged(nameof(ChromaColour));
        OnPropertyChanged(nameof(ChromaSimilarity));
        OnPropertyChanged(nameof(ChromaSmoothness));
        OnPropertyChanged(nameof(ChromaSpill));
        OnPropertyChanged(nameof(HasColorAdjust));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
        OnPropertyChanged(nameof(EffectLanes));
        OnPropertyChanged(nameof(HasEffectLanes));
        OnPropertyChanged(nameof(CanCarryLanes));
        OnPropertyChanged(nameof(CanAddVolumeLane));
        OnPropertyChanged(nameof(CanAddOpacityLane));
        OnPropertyChanged(nameof(CanAddPlacementXLane));
        OnPropertyChanged(nameof(CanAddPlacementYLane));
        OnPropertyChanged(nameof(CanAddPlacementWidthLane));
        OnPropertyChanged(nameof(CanAddPlacementHeightLane));
        OnPropertyChanged(nameof(CanAddPlacementRotationLane));
        OnPropertyChanged(nameof(CanAddChromaSimilarityLane));
        OnPropertyChanged(nameof(CanAddChromaSmoothnessLane));
        OnPropertyChanged(nameof(CanAddChromaSpillLane));
        OnPropertyChanged(nameof(CanAddColorBrightnessLane));
        OnPropertyChanged(nameof(CanAddColorContrastLane));
        OnPropertyChanged(nameof(CanAddAudioGainLane));
        OnPropertyChanged(nameof(HasAutomationChromaKey));
        OnPropertyChanged(nameof(HasAutomationColorAdjust));
        OnPropertyChanged(nameof(HasAutomationAudioGain));
        OnPropertyChanged(nameof(CanAddOutboundLane));
        OnPropertyChanged(nameof(VolumeLaneLabel));
        OnPropertyChanged(nameof(OpacityLaneLabel));
        OnPropertyChanged(nameof(IsAutomationCue));
        OnPropertyChanged(nameof(AutomationTargetCues));
        OnPropertyChanged(nameof(AutomationTargetPlacements));
        OnPropertyChanged(nameof(HasMultipleAutomationTargetPlacements));
        OnPropertyChanged(nameof(AutomationDuration));
        OnPropertyChanged(nameof(AutomationRestoreBase));
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
        CueKind.Media or CueKind.Video => ["GENERAL", "AUDIO", "VIDEO", "AUTOMATION", "NOTE", "PREVIEW"],
        CueKind.Group => ["GENERAL", "GROUP", "NOTE"],
        CueKind.Action => ["GENERAL", "ACTION", "NOTE"],
        CueKind.Automation => ["GENERAL", "AUTOMATION", "NOTE"],
        CueKind.Fade => ["GENERAL", "FADE", "NOTE"],
        CueKind.Jump => ["GENERAL", "JUMP", "NOTE"],
        CueKind.Visualizer => ["GENERAL", "VISUALIZER", "VIDEO", "AUTOMATION", "NOTE"],
        CueKind.Patch => ["GENERAL", "PATCH", "NOTE"],
        // A card is words plus where they sit, so it takes VIDEO for the placement and AUTOMATION for
        // an opacity track — it is a picture from the moment it is drawn.
        CueKind.Text => ["GENERAL", "TEXT", "VIDEO", "AUTOMATION", "NOTE"],
        _ => ["GENERAL", "NOTE"],
    };

    /// <summary>
    /// The tabs a particular cue actually has — the kind's set, less anything its media cannot do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A media cue's kind says it MIGHT carry video; the file says whether it does. A WAV stem was
    /// offered a Video tab with an empty composition picker, a placement list that could never have an
    /// entry, and a crop editor over nothing — eleven of them in a stem group. Worse, "place on
    /// composition" was reachable there, and a placement on a cue with no video stream is a layer that
    /// renders nothing and cannot be told apart from a broken one.
    /// </para>
    /// <para>
    /// Only removed when the probe has actually ANSWERED. A file nobody has looked at yet keeps its
    /// Video tab: hiding a tab because the answer has not arrived is the same failure as painting a cue
    /// red before anybody looked at it. Live sources (<c>ndi:</c> and friends) are never probed at all
    /// and keep it for the same reason — what a camera is carrying is a fact about the moment it
    /// fires, not about the document.
    /// </para>
    /// <para>
    /// AUTOMATION stays either way: a volume track is something an audio-only cue can perfectly well
    /// carry, and the property picker itself offers only targets that actually exist.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> TabsFor(CueNode cue)
    {
        var tabs = TabsFor(KindOf(cue));

        if (cue is not MediaCueNode media
            || SourceUri.IsLive(media.MediaPath)
            || FactsFor(media) is not { IsKnown: true, HasPlaceableVideo: false })
            return tabs;

        return [.. tabs.Where(tab => tab != "VIDEO")];
    }

    /// <summary>
    /// What the probe knows about one cue's file.
    /// </summary>
    /// <remarks>
    /// The lead's copy first — the shell pushes it on selection and it is the freshest answer — then
    /// the lookup. The fallback matters: <see cref="Facts"/> is only written when the selection comes
    /// through the cue tree, so anything that shows a cue by id (a jump, a search result, a test) would
    /// otherwise decide the lead's tabs on a null.
    /// </remarks>
    private MediaFacts? FactsFor(MediaCueNode media) =>
        (ReferenceEquals(media, Cue) ? Facts : null) ?? MediaFacts?.Invoke(media);

    public bool IsGeneralPane => SelectedTab == "GENERAL";
    public bool IsAudioPane => SelectedTab == "AUDIO";
    public bool IsVideoPane => SelectedTab == "VIDEO";
    public bool IsEffectsPane => SelectedTab == "AUTOMATION";
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
            if (!TryParseDb(value, out var db))
                return;

            EditMedia("level", media => media.LevelDb, (media, parsed) => media.LevelDb = parsed, db);

            // Cue volume is its own runtime component beside per-send trim. Push both snapshots so a
            // sounding cue changes without a reload while the two gain stages still compose once.
            PushLiveSends();
        }
    }

    private AudioEffectInstance? AudioGainEffect => Cue is MediaCueNode media
        ? media.AudioEffects.FirstOrDefault(effect => effect.EffectTypeId == S.Media.Routing.GainAudioEffect.EffectId)
        : null;

    public bool HasAudioGain => AudioGainEffect is not null;

    public bool AudioGainOn
    {
        get => AudioGainEffect is { Enabled: true };
        set
        {
            if (Cue is not MediaCueNode media)
                return;
            using (_journal.Composite(value ? "audio insert on" : "audio insert off", "audio"))
            {
                if (value && AudioGainEffect is null)
                    _journal.Do(new AddItemCommand<AudioEffectInstance>(
                        media.AudioEffects,
                        AudioEffectCatalog.Create(S.Media.Routing.GainAudioEffect.EffectId),
                        media.AudioEffects.Count,
                        "audio",
                        "add gain insert"));
                if (AudioGainEffect is { } effect && effect.Enabled != value)
                    _journal.Do(new SetValueCommand<bool>(
                        media.Id, $"audioEffect:{effect.Id}:enabled", "audio",
                        () => effect.Enabled, enabled => effect.Enabled = enabled, value,
                        value ? "enable gain insert" : "bypass gain insert"));
            }
            _journal.CloseGroup();
            Reload();
        }
    }

    public double AudioGainDb
    {
        get => AudioGainEffect?.Read(S.Media.Routing.GainAudioEffect.GainParameterId, 0) ?? 0;
        set
        {
            if (AudioGainEffect is not { } effect || !double.IsFinite(value) || Cue is null)
                return;
            var parameter = effect.Parameters.First(candidate =>
                candidate.ParameterId == S.Media.Routing.GainAudioEffect.GainParameterId);
            var clamped = Math.Clamp(value, -96, 12);
            if (Math.Abs(parameter.Value - clamped) < .000001)
                return;
            Edit($"audioEffect:{effect.Id}:gainDb", "audio",
                () => parameter.Value, number => parameter.Value = number, clamped, "adjust gain insert");
        }
    }

    public bool CanAddAudioGainLane =>
        CanAddLane(AudioEffectCatalog.PropertyId(
            S.Media.Routing.GainAudioEffect.EffectId,
            S.Media.Routing.GainAudioEffect.GainParameterId));

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
        // EVERY selected media cue, not just the lead. Routing a stem group to a different logical
        // output is the archetypal multi-selection edit, and this pane silently applied it to the first
        // row only — leaving the other ten cues on the old send with nothing on screen saying so.
        //
        // The lead still decides WHAT the gesture means (which cell, whether it is being added or
        // removed, which direction a mute toggles), because that is what the operator clicked on. The
        // others follow it, so the whole selection ends up in the state the lead's cell now shows
        // rather than each cue toggling its own way and the matrix reading "mixed" afterwards.
        if (Cue is not MediaCueNode lead
            || gesture.Row >= SendRows.Count
            || gesture.Column >= SendColumns.Count)
            return;

        var source = SendRows[gesture.Row].LineChannel;
        var channelId = SendColumns[gesture.Column].ChannelId;
        var targets = Selected.OfType<MediaCueNode>().ToList();

        CueAudioSend? SendOn(MediaCueNode cue) => cue.Sends.FirstOrDefault(
            send => send.SourceChannel == source && send.LogicalChannelId == channelId);

        var existing = SendOn(lead);

        switch (gesture.Kind)
        {
            case MatrixGestureKind.Toggle:
            {
                var routing = existing is null;
                Each(routing ? "route send at unity" : "remove send", (cue, current) =>
                    new SetCueSendCommand(
                        cue, source, channelId,
                        routing ? 0 : null,
                        routing ? false : null,
                        routing ? "route send at unity" : "remove send"));
                _journal.CloseGroup();
                break;
            }

            case MatrixGestureKind.Adjust when existing is not null:
                // A drag emits one command per pointer sample. Quiet, so the shell reacts once on
                // release instead of re-probing and re-refreshing the whole project per pixel.
                _drag ??= _journal.Composite("adjust send gain", "cues", quiet: true);
                Each("set send gain", (cue, current) => current is null
                    ? null
                    : new SetCueSendCommand(
                        cue, source, channelId, current.GainDb + gesture.DeltaDb, current.Muted,
                        "set send gain"));
                PushLiveSends();
                OnPropertyChanged(nameof(SendRows));
                OnPropertyChanged(nameof(RouteChain));
                return;

            case MatrixGestureKind.Mute when existing is not null:
            {
                var muting = !existing.Muted;
                Each(muting ? "mute send" : "unmute send", (cue, current) => current is null
                    ? null
                    : new SetCueSendCommand(
                        cue, source, channelId, current.GainDb, muting,
                        muting ? "mute send" : "unmute send"));
                _journal.CloseGroup();
                break;
            }
        }

        PushLiveSends();
        Reload();
        return;

        void Each(string description, Func<MediaCueNode, CueAudioSend?, SetCueSendCommand?> build)
        {
            if (targets.Count > 1)
            {
                using (_journal.Composite($"{description} on {targets.Count} cues", "cues"))
                    foreach (var cue in targets)
                    {
                        if (build(cue, SendOn(cue)) is { } command)
                            _journal.Do(command);
                    }

                return;
            }

            if (build(lead, existing) is { } single)
                _journal.Do(single);
        }
    }

    // ── send presets ──────────────────────────────────────────────────────────────────────────
    //
    // Screen 05 has always shown a PRESETS strip reading "stereo → Main · mono from L · swap · clear".
    // It was a caption: a Border with a TextBlock in it and nothing behind either, so the four things
    // it names could not be clicked and never happened. HaPlay's cue player had the feature; this is it.
    //
    // They exist because the matrix is the slow way to do the four routings almost every cue wants.
    // Eleven stems into Main is twenty-two clicks placed exactly right, and the commonest authoring
    // mistake in a cue player is a stem that is quietly mono into one side because one of them missed.

    /// <summary>The pair of logical channels a stereo preset targets, in sort order.</summary>
    /// <remarks>
    /// The first Output GROUP with at least two members, because that is what a stereo pair IS in this
    /// document (register item 9) — falling back to the first two logical channels for a project that
    /// never grouped anything. Named rather than assumed so the button can say where it is sending.
    /// </remarks>
    private IReadOnlyList<LogicalAudioChannel> StereoTarget
    {
        get
        {
            var patch = Project.AudioPatch;
            var ordered = patch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

            var pair = patch.Groups
                .Select(group => group.MemberIds
                    .Select(id => ordered.FirstOrDefault(channel => channel.Id == id))
                    .OfType<LogicalAudioChannel>()
                    .OrderBy(channel => channel.SortOrder)
                    .ToList())
                .FirstOrDefault(members => members.Count >= 2);

            return pair ?? [.. ordered.Take(2)];
        }
    }

    /// <summary>Where the stereo presets would send — on the buttons, so nobody has to guess.</summary>
    public string SendPresetTarget => StereoTarget switch
    {
        { Count: >= 2 } pair => $"{pair[0].Name} · {pair[1].Name}",
        { Count: 1 } single => single[0].Name,
        _ => "no logical outputs",
    };

    /// <summary>False on a project with nothing to send TO — the buttons say so rather than no-op.</summary>
    public bool HasSendPresetTarget => StereoTarget.Count >= 2;

    /// <summary>
    /// Applies one send preset to every selected media cue.
    /// </summary>
    /// <remarks>
    /// Written as "replace this cue's sends with exactly these", not as a series of toggles: a preset
    /// whose result depended on what was already routed would give two cues in one selection different
    /// answers, which is the whole failure the presets exist to avoid.
    /// </remarks>
    public void ApplySendPreset(string preset)
    {
        var targets = Selected.OfType<MediaCueNode>().ToList();
        if (targets.Count == 0)
            return;

        var pair = StereoTarget;
        if (preset != "clear" && pair.Count < 2)
            return;

        // (source channel, logical channel) at unity. Empty means "route nothing".
        IReadOnlyList<(int Source, Guid Channel)> wanted = preset switch
        {
            "stereo" => [(0, pair[0].Id), (1, pair[1].Id)],
            // One channel to both sides, so a mono stem sits in the middle instead of hard left.
            "monoL" => [(0, pair[0].Id), (0, pair[1].Id)],
            "swap" => [(1, pair[0].Id), (0, pair[1].Id)],
            "clear" => [],
            _ => [],
        };

        if (preset is not ("stereo" or "monoL" or "swap" or "clear"))
            return;

        var description = preset switch
        {
            "stereo" => $"stereo → {SendPresetTarget}",
            "monoL" => $"mono from L → {SendPresetTarget}",
            "swap" => "swap L/R sends",
            _ => "clear sends",
        };

        using (_journal.Composite(
                   targets.Count > 1 ? $"{description} on {targets.Count} cues" : description, "cues"))
        {
            foreach (var cue in targets)
            {
                // Remove what the preset does not want FIRST, so a swap cannot momentarily double up
                // on a channel and so "clear" is simply the empty case of the same operation.
                foreach (var send in cue.Sends.ToList())
                {
                    if (wanted.Any(want =>
                            want.Source == send.SourceChannel && want.Channel == send.LogicalChannelId))
                        continue;

                    _journal.Do(new SetCueSendCommand(
                        cue, send.SourceChannel, send.LogicalChannelId, null, null, description));
                }

                foreach (var (source, channel) in wanted)
                    _journal.Do(new SetCueSendCommand(cue, source, channel, 0, false, description));
            }
        }

        _journal.CloseGroup();
        PushLiveSends();
        Reload();
    }

    /// <summary>Closes the send-gain drag's undo step, on pointer release.</summary>
    public void EndSendGesture()
    {
        _drag?.Dispose();
        _drag = null;
        _journal.CloseGroup();
        Reload();
    }

    /// <summary>
    /// Re-applies the selection's sends to whatever is currently sounding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A send or level edit changes the cue's clip binding, so the engine will not adopt it while that
    /// cue is playing — a reload would restart the cue, which on a group of stems is a pop and eleven
    /// re-opened files for one fader move. The running voice gets the new matrix directly instead, and
    /// the document catches up when the show is idle.
    /// </para>
    /// <para>
    /// Best-effort on every count. An idle cue simply has no active voice and the session says so; the
    /// authored value stands either way.
    /// </para>
    /// </remarks>
    private void PushLiveSends()
    {
        if (Host is not { } host)
            return;

        foreach (var media in Selected.OfType<MediaCueNode>())
            _liveSends.Offer(
                new LiveSendKey(host, media.Id),
                new LiveCueMix(ShowCompiler.LogicalSends(media), media.LevelDb));
    }

    /// <summary>
    /// Serializes live send pushes per cue, keeping only the newest.
    /// </summary>
    /// <remarks>
    /// A gain drag emits one of these per pointer sample and each crosses the session dispatcher, which
    /// is the same thread playback runs its commands on. Queueing every sample makes the fader lag the
    /// mouse and starves the show; the publisher lets one finish and replaces the rest.
    /// </remarks>
    private readonly LatestOnlyPublisher<LiveSendKey, LiveCueMix> _liveSends =
        new(
            static async (key, mix) =>
            {
                await key.Host.ApplyActiveSendsAsync(key.CueId, mix.Sends).ConfigureAwait(false);
                await key.Host.ApplyActiveVolumeAsync(key.CueId, mix.LevelDb).ConfigureAwait(false);
            },
            TimeSpan.FromMilliseconds(33),
            static failure => System.Diagnostics.Trace.TraceWarning(
                $"Live send update failed: {failure.GetType().Name}: {failure.Message}"));

    private readonly record struct LiveSendKey(ShowHost Host, Guid CueId);
    private readonly record struct LiveCueMix(IReadOnlyList<ShowClipLogicalSend> Sends, double LevelDb);

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

            return composition is null
                ? []
                : VideoPresentation.Layers(Project, composition, Cue?.Id, MediaFacts);
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
        OnPropertyChanged(nameof(HasColorAdjust));
        OnPropertyChanged(nameof(ColorAdjustOn));
        OnPropertyChanged(nameof(LayerBrightness));
        OnPropertyChanged(nameof(LayerContrast));
        OnPropertyChanged(nameof(CanAddOpacityLane));
        OnPropertyChanged(nameof(CanAddPlacementXLane));
        OnPropertyChanged(nameof(CanAddPlacementYLane));
        OnPropertyChanged(nameof(CanAddPlacementWidthLane));
        OnPropertyChanged(nameof(CanAddPlacementHeightLane));
        OnPropertyChanged(nameof(CanAddPlacementRotationLane));
        OnPropertyChanged(nameof(CanAddChromaSimilarityLane));
        OnPropertyChanged(nameof(CanAddChromaSmoothnessLane));
        OnPropertyChanged(nameof(CanAddChromaSpillLane));
        OnPropertyChanged(nameof(CanAddColorBrightnessLane));
        OnPropertyChanged(nameof(CanAddColorContrastLane));
        OnPropertyChanged(nameof(HasAutomationChromaKey));
        OnPropertyChanged(nameof(HasAutomationColorAdjust));
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

    // ── ordered layer-effect rack ─────────────────────────────────────────────────────────────
    // The built-in focused editors below are projections of the same generic rack shown above them.

    private const string ChromaEffectType = S.Media.Compositor.Effects.ChromaKeyVideoEffect.EffectId;
    private const string ColourEffectType =
        S.Media.Compositor.Effects.BrightnessContrastVideoEffect.EffectId;

    private LayerEffectInstance? ChromaEffect => LayerEffectRack.Find(Placement, ChromaEffectType);
    private LayerEffectInstance? ColourEffect => LayerEffectRack.Find(Placement, ColourEffectType);

    public IReadOnlyList<LayerEffectRackRow> EffectRackRows => Placement is not { } placement
        ? []
        :
        [
            .. placement.Effects.Select((effect, index) => new LayerEffectRackRow(
                effect.Id,
                index + 1,
                LayerEffectCatalog.Get(effect.EffectTypeId)?.DisplayName ?? effect.EffectTypeId,
                effect.Enabled,
                index > 0,
                index + 1 < placement.Effects.Count)),
        ];

    public bool HasChromaKey => ChromaEffect is not null;

    public bool ChromaKeyOn
    {
        get => ChromaEffect is { Enabled: true };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (_journal.Composite(value ? "chroma key on" : "chroma key off", "video"))
            {
                if (value && ChromaEffect is null)
                    _journal.Do(new AddItemCommand<LayerEffectInstance>(
                        placement.Effects,
                        LayerEffectCatalog.Create(ChromaEffectType),
                        placement.Effects.Count,
                        "video",
                        "add chroma key"));

                if (ChromaEffect is { } effect && effect.Enabled != value)
                    _journal.Do(new SetValueCommand<bool>(
                        Cue.Id, $"effect:{effect.Id}:enabled", "video",
                        () => effect.Enabled, flag => effect.Enabled = flag, value,
                        value ? "chroma key on" : "chroma key off"));
            }

            _journal.CloseGroup();
            Reload();
        }
    }

    public double ChromaSimilarity
    {
        get => ChromaEffect?.Read("similarity", 0.4) ?? 0.4;
        set => WriteEffectParameter(ChromaEffect, "similarity", 0, 1, value, "adjust key");
    }

    public double ChromaSmoothness
    {
        get => ChromaEffect?.Read("smoothness", 0.1) ?? 0.1;
        set => WriteEffectParameter(ChromaEffect, "smoothness", 0, 1, value, "adjust key");
    }

    public double ChromaSpill
    {
        get => ChromaEffect?.Read("spill", 0.1) ?? 0.1;
        set => WriteEffectParameter(ChromaEffect, "spill", 0, 1, value, "adjust key");
    }

    /// <summary>The key colour as "#RRGGBB" — what a designer is given on a call sheet.</summary>
    public string ChromaColour
    {
        get => ChromaEffect is { } key
            ? $"#{Channel(key.Read("keyR", 0))}{Channel(key.Read("keyG", 1))}{Channel(key.Read("keyB", 0))}"
            : "#00FF00";
        set
        {
            if (ChromaEffect is not { } key || Cue is null || !TryColour(value, out var rgb))
                return;

            using (_journal.Composite("chroma key colour", "video"))
            {
                WriteEffectParameterCommand(key, "keyR", rgb.Red, "key colour");
                WriteEffectParameterCommand(key, "keyG", rgb.Green, "key colour");
                WriteEffectParameterCommand(key, "keyB", rgb.Blue, "key colour");
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

    public bool HasColorAdjust => ColourEffect is not null;

    public bool ColorAdjustOn
    {
        get => ColourEffect is { Enabled: true };
        set
        {
            if (Placement is not { } placement || Cue is null)
                return;

            using (_journal.Composite(value ? "colour adjust on" : "colour adjust off", "video"))
            {
                if (value && ColourEffect is null)
                    _journal.Do(new AddItemCommand<LayerEffectInstance>(
                        placement.Effects,
                        LayerEffectCatalog.Create(ColourEffectType),
                        placement.Effects.Count,
                        "video",
                        "add colour adjust"));

                if (ColourEffect is { } effect && effect.Enabled != value)
                    _journal.Do(new SetValueCommand<bool>(
                        Cue.Id, $"effect:{effect.Id}:enabled", "video",
                        () => effect.Enabled, flag => effect.Enabled = flag,
                        value, value ? "colour adjust on" : "colour adjust off"));
            }

            _journal.CloseGroup();
            Reload();
        }
    }

    public double LayerBrightness
    {
        get => ColourEffect?.Read("brightness", 0) ?? 0;
        set => WriteEffectParameter(ColourEffect, "brightness", -1, 1, value, "adjust layer colour");
    }

    public double LayerContrast
    {
        get => ColourEffect?.Read("contrast", 1) ?? 1;
        set => WriteEffectParameter(ColourEffect, "contrast", 0, 4, value, "adjust layer colour");
    }

    private void WriteEffectParameter(
        LayerEffectInstance? effect,
        string parameterId,
        double minimum,
        double maximum,
        double value,
        string description)
    {
        if (effect is null || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);
        var parameter = effect.Parameters.FirstOrDefault(candidate => candidate.ParameterId == parameterId);
        if (parameter is null || Math.Abs(parameter.Value - clamped) < 0.000001)
            return;
        Edit($"effect:{effect.Id}:{parameterId}", "video",
            () => parameter.Value, number => parameter.Value = number, clamped, description);
    }

    private void WriteEffectParameterCommand(
        LayerEffectInstance effect, string parameterId, double value, string description)
    {
        var parameter = effect.Parameters.First(candidate => candidate.ParameterId == parameterId);
        _journal.Do(new SetValueCommand<double>(
            Cue!.Id, $"effect:{effect.Id}:{parameterId}", "video",
            () => parameter.Value, number => parameter.Value = number, value, description));
    }

    public void AddLayerEffect(string typeId)
    {
        if (Placement is not { } placement || Cue is null || LayerEffectCatalog.Get(typeId) is not { } definition)
            return;
        _journal.Do(new AddItemCommand<LayerEffectInstance>(
            placement.Effects,
            LayerEffectCatalog.Create(typeId),
            placement.Effects.Count,
            "video",
            $"add {definition.DisplayName}"));
        _journal.CloseGroup();
        Reload();
    }

    public void ToggleLayerEffect(Guid effectId)
    {
        if (Placement?.Effects.FirstOrDefault(effect => effect.Id == effectId) is not { } effect || Cue is null)
            return;
        _journal.Do(new SetValueCommand<bool>(
            Cue.Id, $"effect:{effect.Id}:enabled", "video",
            () => effect.Enabled, value => effect.Enabled = value, !effect.Enabled,
            effect.Enabled ? "bypass effect" : "enable effect"));
        _journal.CloseGroup();
        Reload();
    }

    public void MoveLayerEffect(Guid effectId, int direction)
    {
        if (Placement is not { } placement || direction is not (-1 or 1))
            return;
        var index = placement.Effects.FindIndex(effect => effect.Id == effectId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= placement.Effects.Count)
            return;
        var effect = placement.Effects[index];
        using (_journal.Composite("reorder layer effects", "video"))
        {
            _journal.Do(new RemoveItemCommand<LayerEffectInstance>(
                placement.Effects, effect, "video", "move layer effect"));
            _journal.Do(new AddItemCommand<LayerEffectInstance>(
                placement.Effects, effect, target, "video", "move layer effect"));
        }
        _journal.CloseGroup();
        Reload();
    }

    public void RemoveLayerEffect(Guid effectId)
    {
        if (Placement is not { } placement
            || placement.Effects.FirstOrDefault(effect => effect.Id == effectId) is not { } effect)
            return;
        using (_journal.Composite("remove layer effect", "video"))
        {
            foreach (var owner in Project.AllCues())
            {
                if (CueAutomation.ListOf(owner) is not { } tracks)
                    continue;
                foreach (var track in tracks.Where(track => track.Target.ObjectId == effectId).ToArray())
                    _journal.Do(new RemoveItemCommand<AutomationTrack>(
                        tracks, track, "cues", "remove effect automation"));
            }
            _journal.Do(new RemoveItemCommand<LayerEffectInstance>(
                placement.Effects, effect, "video", "remove layer effect"));
        }
        _journal.CloseGroup();
        Reload();
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
    /// Writes one property of a KIND-SPECIFIC pane across the whole selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The general fields (label, level, trigger, waits) have always applied to every selected cue; the
    /// per-kind panes did not. Their setters closed over the LEAD cue's payload — <c>() =&gt;
    /// group.Shuffle</c> — so selecting five fade cues and typing a duration changed exactly one of
    /// them, silently, while the field showed the new value for the whole selection.
    /// </para>
    /// <para>
    /// Resolved per cue by TYPE, which is also the safety rule: a selection of mixed kinds only sees a
    /// pane at all when every member has that tab (the tab set is the intersection), and anything that
    /// is not a <typeparamref name="TCue"/> is skipped rather than coerced.
    /// </para>
    /// <para>
    /// Cues already holding the value are left out entirely, so a multi-selection edit produces one
    /// undo step containing only the cues it actually changed — and none at all when it changed
    /// nothing, which is what keeps a combo box re-announcing its own value from filling the stack.
    /// </para>
    /// </remarks>
    /// <param name="lead">
    /// The cue whose pane the operator is looking at. Present only so <typeparamref name="TCue"/> can
    /// be inferred at the call site — the edit itself is resolved against the selection, and the lead
    /// gets no special treatment beyond being one of them.
    /// </param>
    private void EditEach<TCue, T>(
        TCue lead,
        string property,
        string domain,
        Func<TCue, T> read,
        Action<TCue, T> write,
        T value,
        string description)
        where TCue : CueNode
    {
        _ = lead;

        var targets = Selected
            .OfType<TCue>()
            .Where(cue => !EqualityComparer<T>.Default.Equals(read(cue), value))
            .ToList();

        if (targets.Count == 0)
            return;

        if (targets.Count > 1)
        {
            using (_journal.Composite($"{description} on {targets.Count} cues", domain))
                foreach (var cue in targets)
                    _journal.Do(Write(cue));
        }
        else
        {
            _journal.Do(Write(targets[0]));
        }

        _journal.CloseGroup();
        Reload();
        return;

        SetValueCommand<T> Write(TCue cue) => new(
            cue.Id, property, domain, () => read(cue), parsed => write(cue, parsed), value, description);
    }

    /// <summary>
    /// The same, for a property whose new value has to be computed per cue.
    /// </summary>
    /// <remarks>
    /// Needed by every LIST-valued property here — a fade's targets, a jump's destination, a
    /// visualizer's feed. Handing one <c>List&lt;Guid&gt;</c> to eleven cues would alias them all onto
    /// a single instance, so editing one afterwards would silently edit the rest; and a relative change
    /// ("add this channel") means something different on each cue and must be recomputed from that
    /// cue's own state rather than from the lead's.
    /// </remarks>
    private void EditEach<TCue, T>(
        TCue lead,
        string property,
        string domain,
        Func<TCue, T> read,
        Action<TCue, T> write,
        Func<TCue, T> value,
        string description)
        where TCue : CueNode
    {
        _ = lead;

        var targets = Selected
            .OfType<TCue>()
            .Select(cue => (Cue: cue, Value: value(cue)))
            .Where(pair => !EqualityComparer<T>.Default.Equals(read(pair.Cue), pair.Value))
            .ToList();

        if (targets.Count == 0)
            return;

        if (targets.Count > 1)
        {
            using (_journal.Composite($"{description} on {targets.Count} cues", domain))
                foreach (var (cue, next) in targets)
                    _journal.Do(Write(cue, next));
        }
        else
        {
            _journal.Do(Write(targets[0].Cue, targets[0].Value));
        }

        _journal.CloseGroup();
        Reload();
        return;

        SetValueCommand<T> Write(TCue cue, T next) => new(
            cue.Id, property, domain, () => read(cue), parsed => write(cue, parsed), next, description);
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
        _journal.Do(RectEdits.Placement(cue, placement, gesture.AuthoredRect()));
        ApplyLivePlacement(cue, placement);

        // These are the only values a move/resize changes. Keeping the hot pointer path focused is
        // important on the compact inspector canvas: rebuilding every combo, crop field, effect lane
        // and guide collection for each native motion event can make the backend coalesce a whole drag
        // into its first pixel. The complete inspector refresh still happens once when the quiet undo
        // scope closes on release.
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(PlacementX));
        OnPropertyChanged(nameof(PlacementY));
        OnPropertyChanged(nameof(PlacementWidth));
        OnPropertyChanged(nameof(PlacementHeight));
    }

    /// <summary>Closes the drag's undo step. Called when the pointer is released or a nudge lands.</summary>
    public void EndPlacementGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    private async Task PublishLivePlacementsAsync()
    {
        while (true)
        {
            LivePlacementEdit edit;
            lock (_livePlacementGate)
            {
                if (_pendingLivePlacements.Count == 0)
                {
                    _livePlacementPublisherRunning = false;
                    return;
                }

                edit = _pendingLivePlacements.Values.First();
                _pendingLivePlacements.Remove(edit.Key);
            }

            try
            {
                await edit.Key.Host.UpdateActivePlacementAsync(
                        edit.Key.CueId,
                        edit.Key.CompositionId,
                        edit.Key.LayerIndex,
                        edit.Placement)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A pointer release can race application/engine shutdown; the persisted edit stands.
            }
            catch (OperationCanceledException)
            {
                // Likewise during shutdown/reload. A later engine sync reads the document value.
            }
            catch (Exception exception)
            {
                // A hot update is best-effort; losing it must not strand the publisher or the authored
                // gesture. The normal release-time project sync remains the recovery path.
                System.Diagnostics.Trace.TraceWarning(
                    $"Live placement update failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private void ApplyLivePlacement(CueNode? cue, LayerPlacement placement)
    {
        if (cue is null || Host is not { } host)
            return;

        var key = new LivePlacementKey(
            host, cue.Id, placement.CompositionId, placement.LayerIndex);
        var edit = new LivePlacementEdit(key, ShowCompiler.VideoPlacement(placement));
        var startPublisher = false;

        lock (_livePlacementGate)
        {
            // Latest wins for this layer while one compositor update is in flight. Queuing every
            // native pointer pixel can leave the projected picture seconds behind the mouse and can
            // starve ordinary playback work on the same serialized session dispatcher.
            _pendingLivePlacements[key] = edit;
            if (!_livePlacementPublisherRunning)
            {
                _livePlacementPublisherRunning = true;
                startPublisher = true;
            }
        }

        if (startPublisher)
            _ = PublishLivePlacementsAsync();
    }

    private readonly record struct LivePlacementKey(
        ShowHost Host, Guid CueId, Guid CompositionId, int LayerIndex);

    private readonly record struct LivePlacementEdit(
        LivePlacementKey Key, ShowVideoPlacement Placement);

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

            EditEach(group, "fireMode", "cues",
                cue => cue.FireMode, (cue, mode) => cue.FireMode = mode, (GroupFireMode)value,
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
                EditEach(group, "shuffle", "cues",
                cue => cue.Shuffle, (cue, on) => cue.Shuffle = on, value,
                    value ? "shuffle" : "play in order");
        }
    }

    public bool ReshuffleValue
    {
        get => Group is { ReshuffleEachPass: true };
        set
        {
            if (Group is { } group && value != group.ReshuffleEachPass)
                EditEach(group, "reshuffle", "cues",
                cue => cue.ReshuffleEachPass, (cue, on) => cue.ReshuffleEachPass = on, value,
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
                EditEach(group, "avoidRepeat", "cues",
                cue => cue.AvoidImmediateRepeat, (cue, on) => cue.AvoidImmediateRepeat = on, value,
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
                EditEach(group, "loopCount", "cues",
                cue => cue.LoopCount, (cue, count) => cue.LoopCount = count, Math.Clamp(value, 0, 999),
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
            EditEach(group, "playCount", "cues",
                cue => cue.PlayCount, (cue, set) => cue.PlayCount = set, count, count is null ? "play every item per pass" : $"play {count} item(s) per pass");
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

            EditEach(group, "crossfade", "cues",
                cue => cue.CrossfadeMs, (cue, ms) => cue.CrossfadeMs = ms, (int)Math.Clamp(seconds * 1000, 0, 60_000), "set crossfade");
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

            EditEach(group, "atEnd", "cues",
                cue => cue.AtEnd, (cue, at) => cue.AtEnd = at, (AtListEnd)value,
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

    // Every control on the TEXT pane goes through here, so making this one method selection-aware is
    // what makes "select three cards, set the font" work — it used to change the first one only.
    private void EditCard<T>(
        string property, Func<TextCueNode, T> read, Action<TextCueNode, T> write, T value)
    {
        if (Card is not { } card)
            return;

        EditEach(card, property, "cues", read, write, value, "edit text cue");
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

        // Computed PER CUE. Ticking "Main L" on a five-fade selection adds that one channel to each of
        // them; copying the lead's finished list across would replace every other cue's own targets
        // with the lead's, which is a different edit and not the one that was asked for.
        EditEach(fade, "fadeTargets", "cues",
            cue => cue.TargetChannelIds,
            (cue, ids) => cue.TargetChannelIds = ids,
            cue =>
            {
                var next = new List<Guid>(cue.TargetChannelIds);

                if (on)
                {
                    if (!next.Contains(channelId))
                        next.Add(channelId);
                }
                else
                {
                    next.Remove(channelId);
                }

                return next;
            },
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
            EditEach(target, "fadeLevel", "cues",
                cue => cue.ToLevelDb, (cue, set) => cue.ToLevelDb = set, Math.Clamp(db, GainRange.SilenceFloorDb, 12), "set fade level");
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
                EditEach(target, "fadeDuration", "cues",
                cue => cue.DurationMs, (cue, set) => cue.DurationMs = set, ms, "set fade duration");
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
            EditEach(target, "fadeEverything", "cues",
                cue => cue.FadeEverythingSounding, (cue, on) => cue.FadeEverythingSounding = on, value,
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
            EditEach(target, "fadeStops", "cues",
                cue => cue.StopTargetsWhenComplete, (cue, on) => cue.StopTargetsWhenComplete = on, value,
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

            // A fresh list per cue: one shared instance would alias every selected jump onto the same
            // object, so editing one afterwards would silently edit the others.
            EditEach(jump, "jumpTarget", "cues",
                cue => cue.TargetCueIds, (cue, ids) => cue.TargetCueIds = ids,
                _ => new List<Guid>(chosen),
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
            EditEach(target, "jumpCondition", "cues",
                cue => cue.Condition, (cue, condition) => cue.Condition = condition, (JumpCondition)value,
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
            EditEach(jump, "jumpCount", "cues",
                cue => cue.JumpCount, (cue, number) => cue.JumpCount = number, count,
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
            EditEach(target, "jumpRandom", "cues",
                cue => cue.PickAtRandom, (cue, on) => cue.PickAtRandom = on, value,
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
            EditEach(target, "jumpFires", "cues",
                cue => cue.FireOnArrival, (cue, on) => cue.FireOnArrival = on, value,
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
            EditEach(target, "actionEndpoint", "cues",
                cue => cue.EndpointId, (cue, id) => cue.EndpointId = id, chosen, "set action endpoint");
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
            EditEach(target, "actionAddress", "cues",
                cue => cue.Address, (cue, address) => cue.Address = address, value, "set action address");
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
            EditEach(target, "actionArguments", "cues",
                cue => cue.Arguments, (cue, args) => cue.Arguments = args, value, "set action arguments");
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
            EditEach(target, "patchSnapshot", "cues",
                cue => cue.SnapshotId, (cue, id) => cue.SnapshotId = id, chosen, "set patch snapshot");
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
                EditEach(target, "patchFade", "cues",
                cue => cue.FadeMs, (cue, set) => cue.FadeMs = set, ms, "set patch fade");
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
            EditEach(target, "presetPack", "cues",
                cue => cue.PresetPack, (cue, pack) => cue.PresetPack = pack, value, "set preset pack");
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
                EditEach(target, "visualizerHold", "cues",
                cue => cue.HoldMs, (cue, set) => cue.HoldMs = set, ms, "set preset hold");
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
                EditEach(target, "visualizerBlend", "cues",
                cue => cue.BlendMs, (cue, set) => cue.BlendMs = set, ms, "set preset blend");
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
            EditEach(target, "visualizerLock", "cues",
                cue => cue.LockPreset, (cue, on) => cue.LockPreset = on, value,
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
            EditEach(visualizer, "visualizerFeedAll", "cues",
                cue => cue.FeedAll, (cue, on) => cue.FeedAll = on, value,
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
            EditEach(visualizer, "visualizerFeedCues", "cues",
                cue => cue.FeedCueIds, (cue, ids) => cue.FeedCueIds = ids,
                _ => new List<Guid>(wanted), "set visualizer audio feed cues");
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

    /// <summary>Machine media facts for every cue drawn beside the selected one.</summary>
    public Func<MediaCueNode, MediaFacts?>? MediaFacts { get; set; }

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

    /// <summary>The same resolved asset the clip editor opens, shared with the timeline waveform so
    /// the two editors cannot scan different files for one cue.</summary>
    public string? ResolveClipPath(MediaCueNode media) => ClipPath(media);

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
        if (LayerEffectRack.Find(placement, ChromaEffectType) is { Enabled: true })
            extras.Add("keyed");
        if (LayerEffectRack.Find(placement, ColourEffectType) is { Enabled: true })
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

    /// <summary>Direct binding endpoints for Avalonia. A two-way binding through a getter-only,
    /// newly-created nested picker can read but cannot reliably write the leaf; that was the preset
    /// selector which visibly snapped back. These properties put the setter on the inspector object
    /// the view actually owns.</summary>
    public IReadOnlyList<CurveOption> CurveChoices => CurveLibrary.Choices(Project);
    public int FadeInCurveIndex { get => FadeInCurve.SelectedIndex; set => FadeInCurve.SelectedIndex = value; }
    public int FadeOutCurveIndex { get => FadeOutCurve.SelectedIndex; set => FadeOutCurve.SelectedIndex = value; }
    public int CrossfadeCurveIndex { get => CrossfadeCurve.SelectedIndex; set => CrossfadeCurve.SelectedIndex = value; }
    public int FadeCurveIndex { get => FadeCurve.SelectedIndex; set => FadeCurve.SelectedIndex = value; }
    public int PatchCurveIndex { get => PatchCurve.SelectedIndex; set => PatchCurve.SelectedIndex = value; }

    private CurvePickerViewModel Picker(string which) =>
        new(_journal, Cue, SpecOf(which).Spec, which, Reload);

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
            new CurveSpecTarget(cue.Id, which, spec, Project, CurveLibrary.EmptyShape(which)),
            $"Q{CuePresentation.Number(cue.Number)} · {label}",
            CurveDuration(which, cue));
    }

    private static TimeSpan? CurveDuration(string which, CueNode cue)
    {
        TimeSpan? duration = (which, cue) switch
        {
            ("fadeIn", MediaCueNode media) => TimeSpan.FromMilliseconds(media.FadeInMs),
            ("fadeOut", MediaCueNode media) => TimeSpan.FromMilliseconds(media.FadeOutMs),
            ("crossfade", GroupCueNode group) => TimeSpan.FromMilliseconds(group.CrossfadeMs),
            ("fade", FadeCueNode fade) => TimeSpan.FromMilliseconds(fade.DurationMs),
            ("patch", PatchCueNode patch) => TimeSpan.FromMilliseconds(patch.FadeMs),
            _ => null,
        };
        return duration is { TotalMilliseconds: > 0 } ? duration : null;
    }

    // ── Automation properties ────────────────────────────────────────────────────────────────

    private List<AutomationTrack>? Lanes => Cue is null ? null : CueAutomation.ListOf(Cue);

    public bool CanCarryLanes => Lanes is not null;

    public IReadOnlyList<EffectLaneRow> EffectLanes =>
        Lanes is not { } tracks || Cue is not { } cue
            ? []
            : [.. tracks.Select((track, index) => new EffectLaneRow(
                index,
                AutomationName(cue, track),
                Describe(track),
                AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External))];

    public bool HasEffectLanes => EffectLanes.Count > 0;

    private string Describe(AutomationTrack track)
    {
        var detail = track.Keyframes.Count == 0
            ? "empty — double-click the editor to add a key"
            : $"{track.Keyframes.Count} keyframe{(track.Keyframes.Count == 1 ? "" : "s")} · absolute time";
        return AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External
            ? track.Target.EndpointId is null ? detail + " · no endpoint" : detail + $" → {track.Target.Address}"
            : detail;
    }

    private string AutomationName(CueNode cue, AutomationTrack track)
    {
        var name = AutomationPropertyCatalog.Get(track.Target.PropertyId)?.DisplayName
                   ?? track.Target.PropertyId;
        var targetCue = track.Target.CueId is { } cueId ? Project.FindCue(cueId) : cue;
        if (track.Target.CueId is { } targetId)
            name = Project.FindCue(targetId) is { } target
                ? $"Q{CuePresentation.Number(target.Number)} · {name}"
                : $"missing cue · {name}";
        if (track.Target.ObjectId is { } objectId
            && targetCue is not null
            && CuePlacements.Of(targetCue).FirstOrDefault(placement => placement.Id == objectId) is { } placement)
            name += $" · layer {placement.LayerIndex}";
        return name;
    }

    public bool CanAddVolumeLane => CanAddLane(AutomationPropertyIds.CueVolume);
    public bool CanAddOpacityLane => CanAddLane(AutomationPropertyIds.PlacementOpacity);
    public bool CanAddPlacementXLane => CanAddLane(AutomationPropertyIds.PlacementX);
    public bool CanAddPlacementYLane => CanAddLane(AutomationPropertyIds.PlacementY);
    public bool CanAddPlacementWidthLane => CanAddLane(AutomationPropertyIds.PlacementWidth);
    public bool CanAddPlacementHeightLane => CanAddLane(AutomationPropertyIds.PlacementHeight);
    public bool CanAddPlacementRotationLane => CanAddLane(AutomationPropertyIds.PlacementRotation);
    public bool CanAddChromaSimilarityLane => CanAddLane(AutomationPropertyIds.ChromaSimilarity);
    public bool CanAddChromaSmoothnessLane => CanAddLane(AutomationPropertyIds.ChromaSmoothness);
    public bool CanAddChromaSpillLane => CanAddLane(AutomationPropertyIds.ChromaSpillReduction);
    public bool CanAddColorBrightnessLane => CanAddLane(AutomationPropertyIds.ColorBrightness);
    public bool CanAddColorContrastLane => CanAddLane(AutomationPropertyIds.ColorContrast);
    public bool HasAutomationChromaKey =>
        LayerEffectRack.Find(AutomationPlacement, ChromaEffectType) is not null;
    public bool HasAutomationColorAdjust =>
        LayerEffectRack.Find(AutomationPlacement, ColourEffectType) is not null;
    public bool HasAutomationAudioGain => AutomationTargetCue is MediaCueNode media
        ? media.AudioEffects.Any(effect => effect.EffectTypeId == S.Media.Routing.GainAudioEffect.EffectId)
        : Cue is MediaCueNode own
          && own.AudioEffects.Any(effect => effect.EffectTypeId == S.Media.Routing.GainAudioEffect.EffectId);
    public bool CanAddOutboundLane => CanCarryLanes;
    public string VolumeLaneLabel => AutomationTargetCue is GroupCueNode ? "Group audio trim" : "Volume";
    public string OpacityLaneLabel => AutomationTargetCue is GroupCueNode ? "Group video opacity" : "Opacity";

    public bool IsAutomationCue => Cue is AutomationCueNode;

    public string AutomationDuration
    {
        get => Cue is AutomationCueNode automation
            ? (automation.DurationMs / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        set
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                Edit(
                    "automationDuration",
                    cue => cue is AutomationCueNode automation ? automation.DurationMs : 0,
                    (cue, milliseconds) =>
                    {
                        if (cue is AutomationCueNode automation)
                            automation.DurationMs = milliseconds;
                    },
                    Math.Max(1, (int)Math.Round(seconds * 1000)));
        }
    }

    public bool AutomationRestoreBase
    {
        get => Cue is AutomationCueNode { Completion: AutomationCompletion.RestoreBase };
        set => Edit(
            "automationCompletion",
            cue => cue is AutomationCueNode automation ? automation.Completion : AutomationCompletion.HoldFinal,
            (cue, completion) =>
            {
                if (cue is AutomationCueNode automation)
                    automation.Completion = completion;
            },
            value ? AutomationCompletion.RestoreBase : AutomationCompletion.HoldFinal);
    }

    public IReadOnlyList<string> AutomationTargetCues =>
        [.. AutomationTargets.Select(target => $"Q{CuePresentation.Number(target.Number)} · {target.Label}")];

    public IReadOnlyList<string> AutomationTargetPlacements => AutomationTargetCue is not { } target
        ? []
        :
        [
            .. CuePlacements.Of(target).Select(placement =>
                $"{Project.Compositions.FirstOrDefault(item => item.Id == placement.CompositionId)?.Name ?? "missing composition"}"
                + $" · L{placement.LayerIndex}"),
        ];

    public bool HasMultipleAutomationTargetPlacements => AutomationTargetPlacements.Count > 1;

    private IReadOnlyList<CueNode> AutomationTargets =>
        [.. Project.AllCues().Where(candidate => candidate.Id != Cue?.Id
            && candidate is not CommentCueNode and not ActionCueNode and not AutomationCueNode)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddVolumeLane))]
    [NotifyPropertyChangedFor(nameof(CanAddOpacityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementXLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementYLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementWidthLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementHeightLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementRotationLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSimilarityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSmoothnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSpillLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorBrightnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorContrastLane))]
    [NotifyPropertyChangedFor(nameof(HasAutomationChromaKey))]
    [NotifyPropertyChangedFor(nameof(HasAutomationColorAdjust))]
    [NotifyPropertyChangedFor(nameof(VolumeLaneLabel))]
    [NotifyPropertyChangedFor(nameof(OpacityLaneLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationTargetPlacements))]
    [NotifyPropertyChangedFor(nameof(HasMultipleAutomationTargetPlacements))]
    private int _automationTargetCueIndex;

    partial void OnAutomationTargetCueIndexChanged(int value) => AutomationTargetPlacementIndex = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddOpacityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementXLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementYLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementWidthLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementHeightLane))]
    [NotifyPropertyChangedFor(nameof(CanAddPlacementRotationLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSimilarityLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSmoothnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddChromaSpillLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorBrightnessLane))]
    [NotifyPropertyChangedFor(nameof(CanAddColorContrastLane))]
    [NotifyPropertyChangedFor(nameof(HasAutomationChromaKey))]
    [NotifyPropertyChangedFor(nameof(HasAutomationColorAdjust))]
    private int _automationTargetPlacementIndex;

    private CueNode? AutomationTargetCue => AutomationTargetCueIndex >= 0
        && AutomationTargetCueIndex < AutomationTargets.Count
            ? AutomationTargets[AutomationTargetCueIndex]
            : null;

    private LayerPlacement? AutomationPlacement => Cue switch
    {
        AutomationCueNode when AutomationTargetCue is { } target =>
            CuePlacements.Of(target).ElementAtOrDefault(AutomationTargetPlacementIndex),
        not null => Placement,
        _ => null,
    };

    private AutomationTargetRef? TargetFor(string propertyId)
    {
        if (Cue is not { } cue)
            return null;
        var targetCue = cue is AutomationCueNode ? AutomationTargetCue : cue;
        var targetCueId = cue is AutomationCueNode ? targetCue?.Id : null;
        var placement = AutomationPlacement;
        if (targetCue is MediaCueNode audioCue
            && AudioEffectCatalog.TryResolveProperty(propertyId, out var audioEffectType, out _)
            && audioCue.AudioEffects.FirstOrDefault(effect =>
                effect.EffectTypeId == audioEffectType.TypeId) is { } audioEffect)
            return NewPlacementTarget(targetCueId, audioEffect.Id, propertyId);
        if (placement is not null
            && LayerEffectCatalog.TryResolveProperty(propertyId, out var effectType, out _)
            && LayerEffectRack.Effective(placement).FirstOrDefault(effect =>
                string.Equals(effect.EffectTypeId, effectType.TypeId, StringComparison.Ordinal)) is { } effect)
            return NewPlacementTarget(targetCueId, effect.Id, propertyId);
        return propertyId switch
        {
            AutomationPropertyIds.CueVolume when targetCue is MediaCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.CueVolume },
            AutomationPropertyIds.CueVolume when cue is AutomationCueNode && targetCue is GroupCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.GroupAudioTrim },
            AutomationPropertyIds.PlacementOpacity when cue is AutomationCueNode && targetCue is GroupCueNode => new AutomationTargetRef
                { CueId = targetCueId, PropertyId = AutomationPropertyIds.GroupVideoOpacity },
            AutomationPropertyIds.PlacementOpacity when placement is not null =>
                new AutomationTargetRef
                {
                    CueId = targetCueId,
                    PropertyId = AutomationPropertyIds.PlacementOpacity,
                    ObjectId = placement.Id,
                },
            AutomationPropertyIds.OscValue => new AutomationTargetRef { PropertyId = AutomationPropertyIds.OscValue },
            AutomationPropertyIds.MidiControlValue => new AutomationTargetRef { PropertyId = AutomationPropertyIds.MidiControlValue },
            AutomationPropertyIds.PlacementX when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementX),
            AutomationPropertyIds.PlacementY when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementY),
            AutomationPropertyIds.PlacementWidth when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementWidth),
            AutomationPropertyIds.PlacementHeight when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementHeight),
            AutomationPropertyIds.PlacementRotation when placement is not null => NewPlacementTarget(
                targetCueId, placement.Id, AutomationPropertyIds.PlacementRotation),
            _ => null,
        };
    }

    private static AutomationTargetRef NewPlacementTarget(Guid? cueId, Guid placementId, string propertyId) =>
        new() { CueId = cueId, ObjectId = placementId, PropertyId = propertyId };

    public void AddLane(string propertyId)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || TargetFor(propertyId) is not { } target
            || tracks.Any(track => CueAutomation.SameTarget(track.Target, target)))
            return;

        var descriptor = AutomationPropertyCatalog.Get(target.PropertyId)!;
        var authoredCue = cue is AutomationCueNode ? AutomationTargetCue : cue;
        var authored = (authoredCue is null ? [] : AutomationPropertyCatalog.ForCue(authoredCue))
            .FirstOrDefault(option => option.Target.PropertyId == target.PropertyId
                && option.Target.ObjectId == target.ObjectId)?.AuthoredValue
            ?? descriptor.Value.Default;
        var durationMs = Math.Max(1_000, (long)(LaneDuration(cue)?.TotalMilliseconds ?? 30_000));
        var added = new AutomationTrack
        {
            Target = target,
            Keyframes =
            [
                new AutomationKeyframe { TimeMs = 0, Value = authored },
                new AutomationKeyframe { TimeMs = durationMs, Value = authored },
            ],
        };

        _journal.Do(new AddItemCommand<AutomationTrack>(
            tracks, added, tracks.Count, "cues", $"add {descriptor.DisplayName} automation"));
        _journal.CloseGroup();
        Reload();
    }

    public bool CanAddLane(string propertyId) =>
        Lanes is { } tracks
        && TargetFor(propertyId) is { } target
        && tracks.All(track => !CueAutomation.SameTarget(track.Target, target));

    public void RemoveLane(int index)
    {
        if (Lanes is not { } tracks || index < 0 || index >= tracks.Count)
            return;
        var track = tracks[index];
        _journal.Do(new RemoveItemCommand<AutomationTrack>(
            tracks, track, "cues", $"remove {track.Target.PropertyId} automation"));
        _journal.CloseGroup();
        Reload();
    }

    public AutomationEditorViewModel? LaneEditor(int index)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || index < 0 || index >= tracks.Count)
            return null;
        var track = tracks[index];
        // A controller cue owns its own time coordinate; the targeted clip may already be minutes
        // into playback, so stretching that clip's waveform across the controller duration would be
        // actively misleading. Waveform context is exact only for a media cue's own track.
        var waveformCue = cue as MediaCueNode;
        var sourceDuration = waveformCue is null
            ? null
            : waveformCue.Id == Cue?.Id
                ? ClipDuration
                : MediaFacts?.Invoke(waveformCue)?.Duration
                  ?? (waveformCue.SourceDurationMs > 0
                      ? TimeSpan.FromMilliseconds(waveformCue.SourceDurationMs)
                      : null);
        return new AutomationEditorViewModel(
            _journal,
            cue,
            track,
            LaneDuration(cue),
            Host,
            waveformCue,
            waveformCue is null ? null : ClipPath(waveformCue),
            sourceDuration,
            CacheRoot,
            WaveformCacheBytes);
    }

    private TimeSpan? LaneDuration(CueNode cue) => cue switch
    {
        MediaCueNode media => media.TrimmedLength(ClipDuration),
        TextCueNode { DurationMs: > 0 } text => TimeSpan.FromMilliseconds(text.DurationMs),
        VisualizerCueNode { HoldMs: > 0 } visualizer => TimeSpan.FromMilliseconds(visualizer.HoldMs),
        AutomationCueNode { DurationMs: > 0 } automation => TimeSpan.FromMilliseconds(automation.DurationMs),
        _ => null,
    };

    public PromptViewModel? ConfigureLane(int index)
    {
        if (Lanes is not { } tracks || Cue is not { } cue || index < 0 || index >= tracks.Count)
            return null;

        var track = tracks[index];
        if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain != AutomationDomain.External)
            return null;

        var kind = track.Target.PropertyId == AutomationPropertyIds.OscValue
            ? EndpointKind.OscOut : EndpointKind.MidiOut;
        var endpoints = _journal.Project.ActionEndpoints.Where(endpoint => endpoint.Kind == kind).ToList();
        var options = endpoints.Count == 0 ? ["(no compatible endpoints)"] : endpoints.Select(e => e.Name).ToList();
        var selected = Math.Max(0, endpoints.FindIndex(endpoint => endpoint.Id == track.Target.EndpointId));

        return new PromptViewModel(
            $"Configure {AutomationName(cue, track)}",
            endpoints.Count == 0
                ? $"Add a {kind} endpoint in Targets first."
                : "Automation sends its current value at the bounded rate below.",
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
                    Value = track.Target.Address,
                    Hint = kind == EndpointKind.OscOut
                        ? "/osc/address — the automated value is its argument"
                        : "cc <channel> <controller> — automation supplies value 0–127",
                },
                new PromptField
                {
                    Label = "Send rate (Hz)",
                    Value = track.Target.SendRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Hint = "1–120; 25 is a good default",
                },
                new PromptField
                {
                    Label = "On STOP / PANIC",
                    Kind = PromptFieldKind.Choice,
                    Options = ["Freeze at current value", "Land on final key"],
                    SelectedIndex = track.Interruption == AutomationInterruption.LandFinal ? 1 : 0,
                    Hint = "Freeze is safer for lights, machinery and external faders.",
                },
            ],
            prompt =>
            {
                if (endpoints.Count == 0)
                    return;

                var endpointIndex = Math.Clamp(prompt["Endpoint"].SelectedIndex, 0, endpoints.Count - 1);
                _ = int.TryParse(prompt["Send rate (Hz)"].Value, out var parsedRate);
                var rate = Math.Clamp(parsedRate, 1, 120);
                using (_journal.Composite("configure outbound automation", "cues"))
                {
                    _journal.Do(new SetValueCommand<Guid?>(
                        cue.Id, $"automation:{track.Id}:endpoint", "cues",
                        () => track.Target.EndpointId, value => track.Target.EndpointId = value,
                        endpoints[endpointIndex].Id, "choose automation endpoint"));
                    _journal.Do(new SetValueCommand<string>(
                        cue.Id, $"automation:{track.Id}:address", "cues",
                        () => track.Target.Address, value => track.Target.Address = value,
                        prompt["Address"].Value.Trim(), "set automation address"));
                    _journal.Do(new SetValueCommand<int>(
                        cue.Id, $"automation:{track.Id}:rate", "cues",
                        () => track.Target.SendRateHz, value => track.Target.SendRateHz = value,
                        rate, "set automation send rate"));
                    var interruption = prompt["On STOP / PANIC"].SelectedIndex == 1
                        ? AutomationInterruption.LandFinal
                        : AutomationInterruption.Freeze;
                    _journal.Do(new SetValueCommand<AutomationInterruption>(
                        cue.Id, $"automation:{track.Id}:interruption", "cues",
                        () => track.Interruption, value => track.Interruption = value,
                        interruption, "set automation interruption policy"));
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
    ProjectJournal journal, CueNode? cue, CurveSpec? spec, string which, Action reload)
{
    public IReadOnlyList<CurveOption> Curves { get; } = CurveLibrary.Choices(journal.Project);

    private int CustomIndex => Curves.Count - 1;

    public bool HasCurve => spec is not null && cue is not null;

    public int SelectedIndex
    {
        get
        {
            if (spec is null)
                return -1;

            if (spec.PresetId is { } presetId)
                return IndexOf(option => option.PresetId == presetId);

            if (spec.Points is { Count: > 1 })
                return CustomIndex;

            return IndexOf(option => option.Law == spec.Law);
        }

        set
        {
            if (spec is null || cue is null || value < 0 || value >= Curves.Count || value == SelectedIndex)
                return;

            var target = new CurveSpecTarget(
                cue.Id, "curvePicker", spec, journal.Project, CurveLibrary.EmptyShape(which));
            var option = Curves[value];
            IProjectCommand? command = option switch
            {
                { Law: { } law } => CurveEdits.PickLaw(target, law),
                { PresetId: { } presetId } => CurveEdits.PickPreset(target, presetId, option.Name),
                { IsCustom: true } => new SetCurveCommand(target, target.Read(), "draw a custom curve"),
                _ => null,
            };

            if (command is null)
                return;

            journal.Do(command);
            journal.CloseGroup();

            reload();
        }
    }

    private int IndexOf(Func<CurveOption, bool> predicate)
    {
        for (var index = 0; index < Curves.Count; index++)
            if (predicate(Curves[index]))
                return index;
        return -1;
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

public sealed record LayerEffectRackRow(
    Guid Id,
    int Number,
    string Name,
    bool Enabled,
    bool CanMoveUp,
    bool CanMoveDown);
