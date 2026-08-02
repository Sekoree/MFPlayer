using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
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

    public ShellViewModel() : this(SampleProject.Create(), MachineFacts.Nothing)
    {
    }

    public ShellViewModel(HaCueProject project) : this(project, MachineFacts.Nothing)
    {
    }

    public ShellViewModel(HaCueProject project, MachineFacts machine)
    {
        Journal = new ProjectJournal(project);
        Machine = machine;

        // Real answers where this machine can give one, invented ones where it cannot — yet. Media
        // durations and broken files come from the PROBE now; sounding cues, meters and the bay still
        // come from the sample, because those need a running session and there is not one.
        Runtime = SampleRuntime.For(project);
        AdoptProbeResults();

        Environment = machine.Environment ?? new RuntimeEnvironment(Runtime, project);

        Cues = new CuesViewModel(Journal, Runtime)
        {
            // The inspector's track lists come from the probe. A cue whose file has not been looked at
            // yet still shows the choice it already holds — opening a show on a machine without the
            // media must not look like the choice was lost.
            MediaFacts = media => machine.Media.Facts(MediaPaths.Resolve(project, media.MediaPath, null)),
        };
        Audio = new AudioViewModel(Journal, Runtime);
        Video = new VideoViewModel(project, Runtime, Journal);
        Targets = new TargetsViewModel(project, Runtime, Journal);
        OutputInfo = new OutputInfoViewModel(Runtime);

        // A project as loaded is clean, whatever its contents. The dirty flag answers "does this
        // differ from the file", not "is anything wrong with it".
        Journal.MarkSaved();
        Journal.Changed += OnJournalChanged;

        _status = ProjectStatus.Run(project, environment: Environment);

        // Fire and forget: the views draw now and the answers arrive later. Until a file has been
        // looked at its length reads "—", which is the truth rather than a guess.
        machine.Media.Changed += OnProbesLanded;
        machine.Media.Refresh(project);
    }

    /// <summary>The machine seam: what this box has, and what its files turned out to be.</summary>
    public MachineFacts Machine { get; }

    private void OnProbesLanded()
    {
        // A probe finishes on a worker; everything below touches view-models bound to the UI.
        Dispatcher.UIThread.Post(() =>
        {
            AdoptProbeResults();
            Status = ProjectStatus.Run(Project, environment: Environment);
            Refresh();
        });
    }

    /// <summary>
    /// Copies what the probe now knows into the runtime the views already hold.
    /// </summary>
    /// <remarks>
    /// Mutated in place rather than replaced: every view-model captured this instance when it was
    /// built, so a new object would leave all of them reading the old answers.
    /// </remarks>
    private void AdoptProbeResults()
    {
        Runtime.MediaDurations = new Dictionary<Guid, TimeSpan>(Machine.Media.DurationsIn(Project, null));
        Runtime.Broken = [.. Machine.Media.BrokenIn(Project, null)];

        // Only a DEFINITE absence counts. A line whose devices nobody could enumerate stays present
        // here and is reported as unchecked by the status pass — a red row nobody verified is the
        // failure this seam exists to avoid.
        if (Machine.Environment is { } environment)
        {
            Runtime.AbsentLines =
            [
                .. Project.AudioLines
                    .Where(line => environment.AudioLine(line) == DeviceAvailability.Absent)
                    .Select(line => line.Id),
            ];
        }
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
        // One journal, every view: an undo made in the patch has to reach the cue tree too.
        Audio.Refresh();
        Video.Refresh();
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
