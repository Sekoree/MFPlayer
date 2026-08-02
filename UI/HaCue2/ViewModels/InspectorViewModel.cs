using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using HaCue2.Presentation;
using HaCue2.Sample;

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
    private readonly Dictionary<CueKind, string> _rememberedTab = [];
    private IReadOnlyList<Guid> _selection = [];

    public InspectorViewModel(ProjectJournal journal) => _journal = journal;

    private HaCueProject Project => _journal.Project;

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
        OnPropertyChanged(nameof(NoteValue));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(SendColumns));
        OnPropertyChanged(nameof(SendRows));
        OnPropertyChanged(nameof(RouteChain));
        OnPropertyChanged(nameof(Placements));
        OnPropertyChanged(nameof(EffectLanes));
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
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
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
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                EditMedia("fadeIn", media => media.FadeInMs,
                    (media, ms) => media.FadeInMs = ms, (int)(seconds * 1000));
        }
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
    public IReadOnlyList<CurveOption> Curves { get; } = SampleShow.FadeCurves;

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
    public IReadOnlyList<string> RouteChain => Cue is MediaCueNode media
        ? AudioPresentation.RouteChain(Project, media, sourceChannel: 1)
        : [];

    public bool HasRoute => RouteChain.Count > 0;

    // ── the Video pane ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<PlacementBox> Placements
    {
        get
        {
            var placement = Cue switch
            {
                MediaCueNode media => media.Placement,
                VisualizerCueNode visualizer => visualizer.Placement,
                _ => null,
            };

            if (placement is null)
                return [];

            var composition = Project.Compositions
                .FirstOrDefault(item => item.Id == placement.CompositionId);

            return composition is null ? [] : VideoPresentation.Layers(Project, composition, Cue?.Id);
        }
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
