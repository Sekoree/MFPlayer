using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>Screens 02, 03 and 05 — the cue tree, the Active panel, the right column, the timeline.</summary>
public partial class CuesViewModel : ObservableObject
{
    public const string PropertiesTab = "CUE PROPERTIES";
    public const string ListsTab = "LISTS & GROUPS";

    public CuesViewModel()
    {
        Scopes = [.. SampleShow.CueListScopes, .. SampleShow.GroupScopes];
        // The show root, not a group: the app opens showing everything, and scoping is a thing the
        // operator does on purpose (register item 7).
        _selectedScope = SampleShow.CueListScopes[1];
        Cues = [.. SampleShow.Act1Cues];
        _selectedCue = SampleShow.Act1Cues[3];
        Inspector = new InspectorViewModel(_selectedCue);
        // The right panel's header follows whichever tab is showing, so it has to hear the inspector.
        Inspector.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(RightPanelHeader));
            OnPropertyChanged(nameof(RightPanelHint));
        };
    }

    // ── the tree ──────────────────────────────────────────────────────────────────────────────
    public ObservableCollection<CueRow> Cues { get; }

    [ObservableProperty]
    private CueRow? _selectedCue;

    partial void OnSelectedCueChanged(CueRow? value)
    {
        // Selecting a cue must NOT flip the right panel to Cue properties (register item 7): browsing
        // the tree while reading Lists & groups is a real thing an operator does. The inspector still
        // follows the selection so the tab is correct when they switch back themselves.
        Inspector.Show(value);
    }

    public bool IsScoped => SelectedScope is not null && SampleShow.GroupScopes.Contains(SelectedScope);

    public string Breadcrumb => IsScoped
        ? $"Act 1  ›  Songs  ›  {SelectedScope!.Name.Trim()}"
        : "Act 1  ›  all cues";

    public string TreeHint => IsScoped
        ? $"{SelectedScope!.Tally} cues in scope · 84 in show"
        : "84 cues · undo: set fade 2.0 → 3.0 on Q13.1";

    // ── scope (right panel, Lists & groups tab) ───────────────────────────────────────────────
    public IReadOnlyList<SettingsPane> Scopes { get; }
    public IReadOnlyList<SettingsPane> CueLists { get; } = SampleShow.CueListScopes;
    public IReadOnlyList<SettingsPane> Groups { get; } = SampleShow.GroupScopes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScoped))]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    [NotifyPropertyChangedFor(nameof(TreeHint))]
    [NotifyPropertyChangedFor(nameof(ActivePanelHint))]
    private SettingsPane? _selectedScope;

    partial void OnSelectedScopeChanged(SettingsPane? value)
    {
        Cues.Clear();
        foreach (var cue in IsScoped ? SampleShow.ScopedCues : SampleShow.Act1Cues)
            Cues.Add(cue);

        ActiveCues.Clear();
        foreach (var active in IsScoped ? SampleShow.ScopedActiveCues : SampleShow.ActiveCues)
            ActiveCues.Add(active);

        SelectedCue = null;
    }

    // ── the Active panel ──────────────────────────────────────────────────────────────────────
    // Scope is a view filter, never a transport boundary: this list always shows everything sounding,
    // in or out of scope, and says which list an out-of-scope cue belongs to.
    public ObservableCollection<ActiveCueRow> ActiveCues { get; } = [.. SampleShow.ActiveCues];

    public string ActivePanelHint => IsScoped
        ? "includes cues outside the scope"
        : "5 sounding · 1 fading · scope never hides these";

    // ── the right column ──────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> RightTabs { get; } = [PropertiesTab, ListsTab];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPropertiesTab))]
    [NotifyPropertyChangedFor(nameof(RightPanelHeader))]
    [NotifyPropertyChangedFor(nameof(RightPanelHint))]
    private string _selectedRightTab = PropertiesTab;

    public bool IsPropertiesTab => SelectedRightTab == PropertiesTab;

    public string RightPanelHeader => IsPropertiesTab ? Inspector.Title : "Lists & groups";

    public string RightPanelHint => IsPropertiesTab
        ? Inspector.KindLabel
        : "pick a root to scope the tree";

    public InspectorViewModel Inspector { get; }

    // ── the transport row ─────────────────────────────────────────────────────────────────────
    public string ListSelector { get; } = "Act 1 · merged 3 lists ▾";
    public string ChaseReadout { get; } = "MTC 01:12:44:07";

    /// <summary>Register item 18 — a bottom sheet by default, undockable to its own window.</summary>
    [ObservableProperty]
    private bool _isTimelineOpen;

    public TimelineViewModel Timeline { get; } = new();
}

/// <summary>Screen 05 — the timeline sheet.</summary>
public sealed class TimelineViewModel
{
    public string Title { get; } = "Timeline · Q13 Act 1 · Opening sequence";
    public string Hint { get; } = "snap 0.5 s · zoom fit";
    public IReadOnlyList<string> Ruler { get; } = SampleShow.TimelineRuler;
    public IReadOnlyList<TimelineLane> Lanes { get; } = SampleShow.TimelineLanes;
    public double Playhead { get; } = SampleShow.TimelinePlayhead;
}
