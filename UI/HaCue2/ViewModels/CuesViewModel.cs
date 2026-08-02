using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
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

        Inspector = new InspectorViewModel(journal);
        Inspector.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(RightPanelHeader));
            OnPropertyChanged(nameof(RightPanelHint));
        };

        Scopes = BuildScopes();
        // The show root, not a group: the app opens showing everything, and scoping is something the
        // operator does on purpose (register item 7).
        _selectedScope = Scopes.FirstOrDefault(scope => scope.IsList && scope.Name == "Act 1")
                         ?? Scopes.FirstOrDefault();

        Cues = [];
        ActiveCues = [.. runtime.ActiveCues];
        Rebuild();
        SelectedCue = Cues.FirstOrDefault();
    }

    private HaCueProject Project => _journal.Project;

    // ── the tree ──────────────────────────────────────────────────────────────────────────────
    public ObservableCollection<CueRow> Cues { get; }

    [ObservableProperty]
    private CueRow? _selectedCue;

    partial void OnSelectedCueChanged(CueRow? value)
    {
        // Selecting a cue must NOT flip the right panel to Cue properties (register item 7).
        Inspector.Show(value is null ? [] : [value.Id]);
    }

    /// <summary>Called when the document changes under us — an undo, or an edit from another view.</summary>
    public void Refresh()
    {
        var selected = SelectedCue?.Id;
        Rebuild();
        SelectedCue = Cues.FirstOrDefault(row => row.Id == selected);
        Inspector.Reload();
        OnPropertyChanged(nameof(TreeHint));
        OnPropertyChanged(nameof(Breadcrumb));
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
            return IsScoped
                ? $"{Cues.Count} cues in scope · {total} in show"
                : $"{Cues.Count} cues · {total} in show";
        }
    }

    // ── scope (right panel, Lists & groups tab) ───────────────────────────────────────────────
    public IReadOnlyList<ScopeEntry> Scopes { get; }

    public IReadOnlyList<ScopeEntry> CueLists => [.. Scopes.Where(scope => scope.IsList)];

    public IReadOnlyList<ScopeEntry> Groups => [.. Scopes.Where(scope => !scope.IsList)];

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

    public TimelineViewModel Timeline => new(Project, _runtime);
}

/// <summary>A scope root: a cue list, or a group inside one.</summary>
/// <param name="Depth">Nesting, so a song inside "Songs" is indented under it.</param>
public sealed record ScopeEntry(Guid Id, string Name, int Count, bool IsList, int Depth)
{
    public string Tally => Count.ToString();
    public Thickness Indent => new(12 + (Depth * 12), 0, 0, 0);
}

/// <summary>Screen 05 — the timeline sheet over whichever group is open.</summary>
public sealed class TimelineViewModel
{
    public TimelineViewModel(HaCueProject project, ShowRuntime runtime)
    {
        // The first timeline group in the show: what the sheet opens onto until a cue is chosen.
        var group = project.AllCues().OfType<GroupCueNode>()
            .FirstOrDefault(candidate => candidate.FireMode == GroupFireMode.Timeline);

        if (group is null)
        {
            Title = "Timeline";
            Hint = "no timeline group in this show";
            return;
        }

        Title = $"Timeline · Q{CuePresentation.Number(group.Number)} {group.Label}";
        Hint = $"{group.Children.Count} cues · snap 0.5 s · zoom fit";
        Ruler = TimelinePresentation.Ruler(group, runtime);
        Lanes = TimelinePresentation.Lanes(group, project, runtime);
    }

    public string Title { get; }
    public string Hint { get; }
    public IReadOnlyList<string> Ruler { get; } = [];
    public IReadOnlyList<TimelineLane> Lanes { get; } = [];

    /// <summary>Where the transport is inside the group — a runtime fact, so zero until one exists.</summary>
    public double Playhead { get; }
}
