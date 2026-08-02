using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>
/// The cue editor in the right column (screen 02) and its per-kind variants (screen 04).
/// </summary>
/// <remarks>
/// The tab SET is a function of the cue kind, and the selected tab is remembered PER KIND — the
/// app-scope "Remember inspector tab per cue kind" preference, on by default. That memory is why the
/// dictionary exists: an operator who works on sends all evening should not land on General every time
/// they click a different media cue, but should still land on Patch when they click a patch cue.
/// </remarks>
public partial class InspectorViewModel : ObservableObject
{
    private static readonly IReadOnlyList<string> NoTabs = [];
    private readonly Dictionary<CueKind, string> _rememberedTab = [];

    public InspectorViewModel(CueRow? cue) => Show(cue);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(HasSingleSelection))]
    [NotifyPropertyChangedFor(nameof(NumberValue))]
    [NotifyPropertyChangedFor(nameof(LabelValue))]
    [NotifyPropertyChangedFor(nameof(LevelValue))]
    [NotifyPropertyChangedFor(nameof(FadeValue))]
    private CueRow? _cue;

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

    /// <summary>
    /// How many cues the tree currently has selected. Above one the panel switches to the
    /// multi-selection state from screen 04's tab table: General plus the tabs common to the whole
    /// selection, mixed values reading <c>—</c> and writing only when touched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSelection))]
    [NotifyPropertyChangedFor(nameof(HasSingleSelection))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    [NotifyPropertyChangedFor(nameof(NumberValue))]
    [NotifyPropertyChangedFor(nameof(LabelValue))]
    [NotifyPropertyChangedFor(nameof(LevelValue))]
    [NotifyPropertyChangedFor(nameof(FadeValue))]
    [NotifyPropertyChangedFor(nameof(MultiSelectionNote))]
    private int _selectionCount;

    public bool HasSelection => Cue is not null;
    public bool IsMultiSelection => SelectionCount > 1;
    public bool HasSingleSelection => Cue is not null && SelectionCount <= 1;

    public string Title => IsMultiSelection
        ? $"{SelectionCount} cues selected"
        : Cue is null ? "Cue properties" : $"Q{Cue.Number} · {Cue.Label}";

    public string KindLabel => IsMultiSelection ? "multi-selection" : Cue?.Kind switch
    {
        CueKind.Media => "media cue",
        CueKind.Video => "media cue · video",
        CueKind.Group => "group",
        CueKind.Action => "action cue",
        CueKind.Fade => "fade cue",
        CueKind.Jump => "jump cue",
        CueKind.Visualizer => "visualizer cue",
        CueKind.Patch => "patch cue",
        CueKind.Comment => "comment",
        _ => "select a cue to edit",
    };

    /// <summary>
    /// Reopening with nothing selected is the specified behaviour, not a fallback: the panel never
    /// shows a stale cue after the selection is cleared (register item 7).
    /// </summary>
    public void Show(CueRow? cue) => Show(cue is null ? [] : [cue]);

    /// <summary>
    /// Shows one cue, nothing, or a multi-selection.
    /// </summary>
    /// <remarks>
    /// For more than one cue the tab set is the INTERSECTION of the selected kinds' tab sets — the
    /// mockup's "General + tabs common to the whole selection". Offering a tab that only some of the
    /// selection has would make an edit apply to an arbitrary subset, which is worse than not
    /// offering it.
    /// </remarks>
    public void Show(IReadOnlyList<CueRow> cues)
    {
        if (SelectedTab is { } previous && Cue is { } old && SelectionCount <= 1)
            _rememberedTab[old.Kind] = previous;

        SelectionCount = cues.Count;
        var lead = cues.Count > 0 ? cues[0] : null;
        Cue = lead;

        Tabs = lead is null
            ? NoTabs
            : cues.Select(c => TabsFor(c.Kind))
                  .Aggregate((IEnumerable<string>)TabsFor(lead.Kind), (common, next) => common.Intersect(next))
                  .ToList();

        SelectedTab = lead is null
            ? null
            : _rememberedTab.TryGetValue(lead.Kind, out var remembered) && Tabs.Contains(remembered)
                ? remembered
                // Second tab, not the first: General is the same on every kind, so landing on it
                // tells the operator nothing about what they just selected.
                : Tabs.Skip(1).FirstOrDefault() ?? Tabs.FirstOrDefault();
    }

    /// <summary>
    /// The tab table from screen 04. "Note" (singular) is on every kind and a comment cue collapses to
    /// it (register item 19); Subtitles is omitted here because no sample source carries them, and a
    /// tab that is always empty teaches the operator to ignore the strip.
    /// </summary>
    private static IReadOnlyList<string> TabsFor(CueKind kind) => kind switch
    {
        CueKind.Media => ["GENERAL", "AUDIO", "VIDEO", "EFFECTS", "NOTE", "PREVIEW"],
        CueKind.Video => ["GENERAL", "AUDIO", "VIDEO", "EFFECTS", "NOTE", "PREVIEW"],
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
    // One pane per kind rather than one shared "the other kinds" pane: a fade cue's editor is targets
    // and a curve, a jump's is a destination and a pick rule, and a visualizer's is a preset — sharing
    // an endpoint/address form between them would only look finished.
    public bool IsActionPane => SelectedTab == "ACTION";
    public bool IsFadePane => SelectedTab == "FADE";
    public bool IsJumpPane => SelectedTab == "JUMP";
    public bool IsVisualizerPane => SelectedTab == "VISUALIZER";

    // ── field values ──────────────────────────────────────────────────────────────────────────
    // Across a multi-selection a differing value reads "—" and stays that way until the operator
    // types into it (screen 04's tab table). Showing the lead cue's value instead would invite an
    // edit that silently overwrote the others with something the operator never read.
    public string NumberValue => IsMultiSelection ? Mixed : Cue?.Number ?? "";
    public string LabelValue => IsMultiSelection ? Mixed : Cue?.Label ?? "";
    public string LevelValue => IsMultiSelection ? Mixed : Cue?.Level ?? "";
    public string FadeValue => IsMultiSelection ? Mixed : Cue?.Fade ?? "";

    public string MultiSelectionNote =>
        $"{SelectionCount} cues · mixed values read — and only write when touched";

    private const string Mixed = "—";

    // ── the Audio pane (screen 02's drawn state) ──────────────────────────────────────────────
    public IReadOnlyList<MatrixColumn> SendColumns { get; } = SampleShow.SendColumns;
    public IReadOnlyList<MatrixRow> SendRows { get; } = SampleShow.SendRows;
    public IReadOnlyList<CurveOption> Curves { get; } = SampleShow.FadeCurves;

    /// <summary>
    /// The effective route for the picked cell — source → logical → physical, read from the middle.
    /// This strip exists so "why is this silent / why is it doubled" is answerable without mentally
    /// multiplying the send matrix by the patch matrix.
    /// </summary>
    public IReadOnlyList<string> RouteChain { get; } = ["Src R", "Main R", "18i20 · 2", "NDI · 2"];

    // ── the Video pane ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<PlacementBox> Placements { get; } =
    [
        new() { Label = "rain-loop · L2", Left = 0.08, Top = 0.10, Width = 0.60, Height = 0.75, IsSelected = true },
        new() { Label = "logo hold · L1", Left = 0.63, Top = 0.44, Width = 0.33, Height = 0.50, IsSecondary = true },
    ];

    // ── the Effects pane (register item 18: lanes, hidden until added) ────────────────────────
    public IReadOnlyList<string> EffectLanes { get; } = ["volume · 6 points", "opacity · not added", "OSC ramp · not added"];
}
