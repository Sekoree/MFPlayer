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
public partial class InspectorViewModel : ObservableObject, IInspectorEditorContext
{
    private static readonly IReadOnlyList<string> NoTabs = [];
    private const string Mixed = "-";

    private readonly ProjectJournal _journal;

    private readonly Dictionary<CueKind, string> _rememberedTab = [];
    private IReadOnlyList<Guid> _selection = [];

    public InspectorViewModel(ProjectJournal journal)
    {
        _journal = journal;
        Edits = new CueEditPlumbing(journal, this);
        Audio = new AudioPaneEditor(journal, this);
        Video = new PlacementAndAutomationEditor(journal, this);
        FadePane = new FadePaneEditor(Edits, this);
        JumpPane = new JumpPaneEditor(Edits, this);
        GroupPane = new GroupPaneEditor(Edits, this);
        TextPane = new TextPaneEditor(Edits, this);
        ActionPane = new ActionPaneEditor(Edits, this);
        PatchPane = new PatchPaneEditor(Edits, this);
        VisualizerPane = new VisualizerPaneEditor(Edits, this);
    }

    /// <summary>The Audio pane's editor (review F-11's exemplar extraction): the send matrix,
    /// presets, route readout and live pushes, owned by one class over the shared journal.</summary>
    public AudioPaneEditor Audio { get; }

    /// <summary>The Video pane + automation lanes, as one editor (they are one feature - the lane
    /// surface derives from the selected placement and the effect rack).</summary>
    public PlacementAndAutomationEditor Video { get; }

    /// <summary>The shared multi-selection edit plumbing every pane writes through (F-11) - the
    /// transaction boundary, extracted once so per-pane editors take it by constructor.</summary>
    internal CueEditPlumbing Edits { get; }

    /// <summary>The FADE pane's editor - the first per-kind editor over the shared plumbing.</summary>
    public FadePaneEditor FadePane { get; }

    /// <summary>The JUMP pane's editor.</summary>
    public JumpPaneEditor JumpPane { get; }

    /// <summary>The remaining per-kind pane editors (F-11).</summary>
    public GroupPaneEditor GroupPane { get; }
    public TextPaneEditor TextPane { get; }
    public ActionPaneEditor ActionPane { get; }
    public PatchPaneEditor PatchPane { get; }
    public VisualizerPaneEditor VisualizerPane { get; }

    /// <summary>When false each selection starts on its cue-kind default instead of recalling a prior tab.</summary>
    public bool RememberTabs { get; set; } = true;

    private HaCueProject Project => _journal.Project;

    HaCueProject IInspectorEditorContext.Project => Project;

    MediaFacts? IInspectorEditorContext.LeadFacts => Facts;
    Func<MediaCueNode, MediaFacts?>? IInspectorEditorContext.MediaFactsFor => MediaFacts;
    TimeSpan? IInspectorEditorContext.LeadClipDuration => ClipDuration;
    string? IInspectorEditorContext.ClipPathFor(MediaCueNode media) => ClipPath(media);
    string IInspectorEditorContext.CacheRoot => CacheRoot;
    long? IInspectorEditorContext.WaveformCacheBytes => WaveformCacheBytes;

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    /// <summary>The running show, used only for hot geometry edits while a cue is sounding.</summary>
    public ShowHost? Host { get; set; }

    /// <summary>The lead cue - the one whose values the single-selection fields show.</summary>
    public CueNode? Cue => _selection.Count > 0 ? Project.FindCue(_selection[0]) : null;

    public IReadOnlyList<CueNode> Selected =>
        [.. _selection.Select(Project.FindCue).OfType<CueNode>()];

    /// <summary>Whether document-authoring controls should be interactive under the shell Lock.</summary>
    public bool CanAuthor => !_journal.IsReadOnly;

    private IReadOnlyList<string> _tabs = NoTabs;

    /// <summary>
    /// The tab set for the current selection.
    /// </summary>
    /// <remarks>
    /// Assigned only when the set actually CHANGED, which is why this is not an
    /// <c>[ObservableProperty]</c>: <see cref="Reload"/> builds a fresh list every time it runs, and a
    /// generated setter compares by reference, so an ordinary edit replaced the tab strip's
    /// <c>ItemsSource</c> with an identical list on every keystroke. Avalonia resets a
    /// <c>SelectingItemsControl</c> when its source is replaced, so the strip pushed a null
    /// <see cref="SelectedTab"/> back through its two-way binding before the view-model set the tab
    /// again - and the pane that was open went invisible for that instant, taking keyboard focus with
    /// it. One character in a field, then out of the field: renaming anything was almost impossible.
    /// </remarks>
    public IReadOnlyList<string> Tabs
    {
        get => _tabs;
        private set
        {
            if (_tabs.SequenceEqual(value))
                return;

            _tabs = value;
            OnPropertyChanged();
        }
    }

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
        // here - when only single selections wrote the memory, adding a send on the Audio pane of
        // eleven stems recalled a stale "GENERAL" on the restore and threw the operator out of the
        // pane once per edit. The open tab is in the intersection of the selected kinds' tab sets
        // (see Reload), so it is a valid memory for each of them.
        if (RememberTabs && SelectedTab is { } previous && Cue is not null)
            foreach (var kind in Selected.Select(KindOf).Distinct())
                _rememberedTab[kind] = previous;

        // The live automated level belongs to the cue that was selected. Clear it on the way in, so the
        // outgoing cue's value cannot be read as the incoming one's in the gap before the next poll.
        _liveAutomatedVolumeDb = null;

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
    /// snaps back to General on every keystroke - which is what it did, because every edit ran the tab
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
        Audio.RaiseChanged();
        Video.RaiseChanged();
        OnPropertyChanged(nameof(CanAddAudioGainLane));
        OnPropertyChanged(nameof(FadeInCurve));
        OnPropertyChanged(nameof(FadeOutCurve));
        OnPropertyChanged(nameof(CrossfadeCurve));
        OnPropertyChanged(nameof(CrossfadeOutShape));
        OnPropertyChanged(nameof(CrossfadeInShape));
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
        GroupPane.RaiseChanged();
        OnPropertyChanged(nameof(EndBehaviourIndex));
        OnPropertyChanged(nameof(IsLooping));
        OnPropertyChanged(nameof(LoopCrossfadeValue));
        OnPropertyChanged(nameof(ColorTagIndex));
        OnPropertyChanged(nameof(PreRollValue));
        TextPane.RaiseChanged();
        OnPropertyChanged(nameof(SubtitlePicker));

        // The per-kind panes. Every one of these reads straight off the selected cue, so they all have
        // to be re-announced whenever the selection or the document changes.
        FadePane.RaiseChanged();
        JumpPane.RaiseChanged();
        ActionPane.RaiseChanged();
        PatchPane.RaiseChanged();
        VisualizerPane.RaiseChanged();
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
        // an opacity track - it is a picture from the moment it is drawn.
        CueKind.Text => ["GENERAL", "TEXT", "VIDEO", "AUTOMATION", "NOTE"],
        _ => ["GENERAL", "NOTE"],
    };

    /// <summary>
    /// The tabs a particular cue actually has - the kind's set, less anything its media cannot do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A media cue's kind says it MIGHT carry video; the file says whether it does. A WAV stem was
    /// offered a Video tab with an empty composition picker, a placement list that could never have an
    /// entry, and a crop editor over nothing - eleven of them in a stem group. Worse, "place on
    /// composition" was reachable there, and a placement on a cue with no video stream is a layer that
    /// renders nothing and cannot be told apart from a broken one.
    /// </para>
    /// <para>
    /// Only removed when the probe has actually ANSWERED. A file nobody has looked at yet keeps its
    /// Video tab: hiding a tab because the answer has not arrived is the same failure as painting a cue
    /// red before anybody looked at it. Live sources (<c>ndi:</c> and friends) are never probed at all
    /// and keep it for the same reason - what a camera is carrying is a fact about the moment it
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
    /// The lead's copy first - the shell pushes it on selection and it is the freshest answer - then
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
    // Across a multi-selection a differing value reads "-" and stays that way until the operator types
    // into it: showing the lead cue's value instead would invite an edit that silently overwrote the
    // others with something nobody read.

    public string MultiSelectionNote =>
        $"{SelectionCount} cues · mixed values read - and only write when touched";

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
        : ["- normal follow -", .. EndTargetCandidates(media)
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
    /// reopens the dialog that built it - there is no path here a file picker could replace.
    /// </remarks>
    public string SourceNote => Cue is not MediaCueNode media || !SourceUri.IsSource(media.MediaPath)
        ? ""
        : SourceUri.Describe(media.MediaPath)
          + (SourceUri.IsLive(media.MediaPath)
              ? " · live - no length, no seeking, and it opens when the cue fires"
              : "");

    public string LevelValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Db(media.LevelDb) : "-");
        set
        {
            if (!CueEditPlumbing.TryParseDb(value, out var db))
                return;

            EditMedia("level", media => media.LevelDb, (media, parsed) => media.LevelDb = parsed, db);

            // Cue volume is its own runtime component beside per-send trim. Push both snapshots so a
            // sounding cue changes without a reload while the two gain stages still compose once.
            Audio.PushLiveSends();
        }
    }

    /// <summary>The volume track that owns this cue's level, when one does.</summary>
    private AutomationTrack? LevelAutomationTrack => Cue is MediaCueNode media
        ? media.AutomationTracks.FirstOrDefault(track =>
            track.Enabled
            && track.Target.ObjectId is null
            && track.Target.PropertyId == AutomationPropertyIds.CueVolume
            && track.Keyframes.Count > 0)
        : null;

    public bool LevelIsAutomated => LevelAutomationTrack is not null || LiveAutomatedVolumeDb is not null;

    private double? _liveAutomatedVolumeDb;

    /// <summary>What automation is driving the selected cue's volume to RIGHT NOW, or null when nothing
    /// is. Pushed by the engine poll (see <c>CuesViewModel.Tick</c>) rather than queried per read.</summary>
    public double? LiveAutomatedVolumeDb
    {
        get => _liveAutomatedVolumeDb;
        set
        {
            // Only signal on a change the operator could see - this arrives 4× a second.
            if (_liveAutomatedVolumeDb is { } previous && value is { } next
                ? Math.Abs(previous - next) < 0.05
                : _liveAutomatedVolumeDb is null && value is null)
                return;
            _liveAutomatedVolumeDb = value;
            OnPropertyChanged(nameof(LiveAutomatedVolumeDb));
            OnPropertyChanged(nameof(LevelIsAutomated));
            OnPropertyChanged(nameof(LevelAutomationNote));
        }
    }

    /// <summary>Says out loud that the box above is the BASE, not what the cue will actually play. Cue
    /// volume is replace-authored, so a track shadows this value while it runs - and a static control that
    /// looks authoritative while automation overrides it is exactly what the automation design forbids.
    /// <para>While the cue is sounding this reads the design's "base −6.0 dB · automated now −12.0 dB";
    /// off air it falls back to naming the track and the range it covers.</para></summary>
    public string LevelAutomationNote
    {
        get
        {
            if (LiveAutomatedVolumeDb is { } now)
                return $"base {LevelValue} · automated now {CuePresentation.Db(now)}";
            if (LevelAutomationTrack is not { } track)
                return "";

            var values = track.Keyframes.Select(key => key.Value).ToList();
            return $"automated by a volume track ({CuePresentation.Db(values.Min())} … "
                   + $"{CuePresentation.Db(values.Max())}) - the value above is the base";
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
        Video.CanAddLane(AudioEffectCatalog.PropertyId(
            S.Media.Routing.GainAudioEffect.EffectId,
            S.Media.Routing.GainAudioEffect.GainParameterId));

    public string FadeValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Seconds(media.FadeInMs) : "-");
        set
        {
            if (CueEditPlumbing.TryParseSeconds(value, out var ms))
                EditMedia("fadeIn", media => media.FadeInMs, (media, set) => media.FadeInMs = set, ms);
        }
    }

    public string FadeOutValue
    {
        get => Shared(cue => cue is MediaCueNode media ? CuePresentation.Seconds(media.FadeOutMs) : "-");
        set
        {
            if (CueEditPlumbing.TryParseSeconds(value, out var ms))
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
    /// Phrased as the POSITIVE on screen - "pre-roll this cue" - because the document's flag is a
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
    /// The fastest thing to read in a list of six hundred rows - an operator finds "the blue block"
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
        get => Shared(cue => cue is MediaCueNode media ? ClipTimes.Format(media.TrimInMs) : "-");
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
    /// the very start - a cue that plays nothing. The word is the honest rendering.
    /// </remarks>
    public string TrimOutValue
    {
        get => Shared(cue => cue is MediaCueNode media
            ? media.TrimOutMs <= 0 ? "end" : ClipTimes.Format(media.TrimOutMs)
            : "-");
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
    /// How this cue is reached - manual, follow, or continue.
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
            if (CueEditPlumbing.TryParseSeconds(value, out var ms))
                Edit("preWait", cue => cue.PreWaitMs, (cue, set) => cue.PreWaitMs = set, ms);
        }
    }

    public string PostWaitValue
    {
        get => Shared(cue => CuePresentation.Seconds(cue.PostWaitMs));
        set
        {
            if (CueEditPlumbing.TryParseSeconds(value, out var ms))
                Edit("postWait", cue => cue.PostWaitMs, (cue, set) => cue.PostWaitMs = set, ms);
        }
    }


    /// <summary>
    /// Ends the current coalescing group - called when a field loses focus.
    /// </summary>
    /// <remarks>
    /// Without an explicit boundary two separate edits of the same field merge into one undo step, and
    /// an edit made after a save merges into the pre-save command. The UI owns the boundary because
    /// only it knows when the gesture ended.
    /// </remarks>
    public void EndEdit() => _journal.CloseGroup();

    // ── the per-kind panes ────────────────────────────────────────────────────────────────────
    // Every control-flow kind EXECUTES (the transport resolves them app-side), so every one of them
    // has to be authorable. Until this landed the only working fade, jump, patch and action cues in
    // existence were the ones the fixture generator wrote - the panes were literals, so the transport
    // could fire a cue the editor could not create.

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
    /// the document stores a reference - the same resolution the engine does when it plays the cue, so
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

    /// <summary>The only video is a still frame - worth saying before somebody places it.</summary>
    public bool IsCoverArtOnly => Facts is { IsCoverArtOnly: true };

    public string CoverArtNote =>
        "this file's only video is cover art - a placement shows that still frame for the cue's length";






    /// <summary>The subtitle tracks in the file, as the picker lists them.</summary>
    public IReadOnlyList<string> SubtitleTracks =>
        Facts is null ? [] : [.. Facts.SubtitleTracks.Select(track => track.Label)];

    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;

    /// <summary>What the cue currently shows - empty is the default, subtitles are never on by accident.</summary>
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

    /// <summary>
    /// The crossfade as it will be PLAYED - both halves of it.
    /// </summary>
    /// <remarks>
    /// A crossfade is the one curve in the app that is two ramps, and the picker beside this shows one
    /// line: the operator could see the shape of the departure and had to imagine the arrival, which is
    /// the half that decides whether the join dips or bumps. Drawn from the session's own fade math, so
    /// this is the join rather than an illustration of one - and it answers for a named LAW too, whose
    /// equal-power shape no editable point list can express.
    /// </remarks>
    public IReadOnlyList<CurvePoint> CrossfadeOutShape => CrossfadeRamps.Out;

    public IReadOnlyList<CurvePoint> CrossfadeInShape => CrossfadeRamps.In;

    private (IReadOnlyList<CurvePoint> Out, IReadOnlyList<CurvePoint> In) CrossfadeRamps =>
        Cue is GroupCueNode group
            ? CurveLibrary.CrossfadeRamps(group.CrossfadeCurve, Project)
            : ([], []);

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
    /// Register item 16 - one editor for fades, crossfades and patch-cue ramps alike. Building the
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
            CurveDuration(which, cue),
            crossfade: which == "crossfade");
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

    // ── edit plumbing ─────────────────────────────────────────────────────────────────────────

    /// <summary>A value all selected cues agree on, or <see cref="Mixed"/> when they do not.</summary>
    private string Shared(Func<CueNode, string> read)
    {
        var values = Selected.Select(read).Distinct().ToList();
        return values.Count == 1 ? values[0] : values.Count == 0 ? "" : Mixed;
    }

    /// <summary>Single-cue edit with an explicit undo domain (the audio gain-insert fields).</summary>
    private void Edit<T>(
        string property, string domain, Func<T> read, Action<T> write, T value, string description)
    {
        if (Cue is not { } cue)
            return;

        _journal.Do(new SetValueCommand<T>(cue.Id, property, domain, read, write, value, description));
        _journal.CloseGroup();
        Reload();
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

}

/// <summary>
/// One curve picker: the four built-in laws, plus "custom" for a shape somebody drew.
/// </summary>
/// <remarks>
/// "custom" is not a fifth law - it is what the list reads when an INLINE POINT LIST exists, because
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
/// <param name="Index">Its position in the cue's lane list - what edit and remove act on.</param>
public sealed record EffectLaneRow(int Index, string Kind, string Detail, bool IsOutbound);

public sealed record LayerEffectRackRow(
    Guid Id,
    int Number,
    string Name,
    bool Enabled,
    bool CanMoveUp,
    bool CanMoveDown);
