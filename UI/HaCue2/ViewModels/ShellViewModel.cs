using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// The one window: CUES · AUDIO · VIDEO · TARGETS, the title-bar latches, the status bar, and the
/// Output info drawer (register items 1–4).
/// </summary>
/// <remarks>
/// It owns the two things every view needs: the <see cref="ProjectJournal"/> (the document and its
/// undo history) and the <see cref="ShowRuntime"/> (what a running session would be saying). Views
/// read the document through projections and write it through the journal, so "what is on screen" and
/// "what would be saved" cannot drift apart.
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    public const string CuesView = "CUES";
    public const string AudioView = "AUDIO";
    public const string VideoView = "VIDEO";
    public const string TargetsView = "TARGETS";

    public ShellViewModel() : this(SampleProject.Create())
    {
    }

    public ShellViewModel(HaCueProject project)
    {
        Journal = new ProjectJournal(project);
        Runtime = SampleRuntime.For(project);
        Environment = new RuntimeEnvironment(Runtime, project);

        Cues = new CuesViewModel(Journal, Runtime);
        Audio = new AudioViewModel(project, Runtime);
        Video = new VideoViewModel(project, Runtime);
        Targets = new TargetsViewModel(project, Runtime);
        OutputInfo = new OutputInfoViewModel(Runtime);

        // A project as loaded is clean, whatever its contents. The dirty flag answers "does this
        // differ from the file", not "is anything wrong with it".
        Journal.MarkSaved();
        Journal.Changed += OnJournalChanged;

        _status = ProjectStatus.Run(project, environment: Environment);
    }

    public ProjectJournal Journal { get; }
    public ShowRuntime Runtime { get; }
    public IProjectEnvironment Environment { get; }
    public HaCueProject Project => Journal.Project;

    public CuesViewModel Cues { get; }
    public AudioViewModel Audio { get; }
    public VideoViewModel Video { get; }
    public TargetsViewModel Targets { get; }
    public OutputInfoViewModel OutputInfo { get; }

    public IReadOnlyList<string> Views { get; } = [CuesView, AudioView, VideoView, TargetsView];

    /// <summary>The title bar's project name, with the mockup's unsaved marker.</summary>
    public string ProjectFile =>
        Project.Title + HaCueProjectFile.Extension + (Journal.IsDirty ? " *" : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPane))]
    private string _selectedView = CuesView;

    /// <summary>
    /// The Lock latch (register item 2): opt-in, never the launch state, read-only document, and GO
    /// untouched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    private bool _isLocked;

    /// <summary>
    /// One master toggle over MIDI/OSC/hotkey triggers, wall-clock schedules and MTC chase
    /// (register item 3). Off when a project opens; it never gates GO.
    /// </summary>
    [ObservableProperty]
    private bool _isExternalInputEnabled;

    /// <summary>Register item 4 — hidden by default, F9 or the status-bar toggle summons it.</summary>
    [ObservableProperty]
    private bool _isOutputInfoOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    [NotifyPropertyChangedFor(nameof(IssueSummary))]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    private ProjectStatusReport _status;

    public object CurrentPane => SelectedView switch
    {
        AudioView => Audio,
        VideoView => Video,
        TargetsView => Targets,
        _ => Cues,
    };

    public string ModeLabel => IsLocked ? "LOCKED" : "EDITING";

    // ── status bar ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one-token health summary that replaced the permanent output strip (register item 4).
    /// </summary>
    /// <remarks>
    /// It reports on the OUTPUT checks only. A dangling jump target is a real problem but not an
    /// output problem, and a token that went red for everything would stop meaning anything.
    /// </remarks>
    public string OutputSummary => OutputChecks().Any(check => check.Outcome == CheckOutcome.Failed)
        ? "● outputs failing"
        : OutputChecks().Any(check => check.Outcome == CheckOutcome.Warning)
            ? "● outputs degraded"
            : "● outputs ok";

    public bool HasIssues => Status.Errors + Status.Warnings > 0;

    public string IssueSummary
    {
        get
        {
            var count = Status.Errors + Status.Warnings;
            return count == 0
                ? "no issues"
                : $"▲ {count} issue{(count == 1 ? "" : "s")} · Project status";
        }
    }

    public string UnsavedSummary => Journal.IsDirty
        ? $"{Journal.Log.Count} unsaved edit{(Journal.Log.Count == 1 ? "" : "s")}"
        : "no unsaved edits";

    public string SavedSummary => Journal.IsDirty ? "unsaved" : "saved";

    /// <summary>The undo toast: what ⌘Z would take back, and which surface it would change.</summary>
    public string? UndoSummary => Journal.NextUndo is { } command
        ? $"undo: {command.Domain} — {command.Description}"
        : null;

    public bool CanUndo => Journal.CanUndo;
    public bool CanRedo => Journal.CanRedo;

    public void Undo()
    {
        if (Journal.Undo())
            Refresh();
    }

    public void Redo()
    {
        if (Journal.Redo())
            Refresh();
    }

    private void OnJournalChanged()
    {
        // Re-running the status pass on every keystroke would be wasteful on a big show; it is cheap
        // here and the honest behaviour, and the real app debounces it the same way it debounces save.
        Status = ProjectStatus.Run(Project, environment: Environment);
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(ProjectFile));
        OnPropertyChanged(nameof(UnsavedSummary));
        OnPropertyChanged(nameof(SavedSummary));
        OnPropertyChanged(nameof(UndoSummary));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Cues.Refresh();
    }

    private IEnumerable<StatusCheck> OutputChecks() =>
        Status.Checks.Where(check =>
            check.Name is "Audio devices" or "Video outputs" or "Logical outputs");
}

/// <summary>The Output info drawer's content (screen 02b) — entirely runtime facts.</summary>
public sealed class OutputInfoViewModel(ShowRuntime runtime)
{
    public IReadOnlyList<ProgramMeter> Meters { get; } = runtime.Meters;
    public IReadOnlyList<OutputLineChip> Lines { get; } = runtime.LineChips;
    public string BaySummary { get; } = runtime.BaySummary;
    public string BayClock { get; } = runtime.BayClock;
}
