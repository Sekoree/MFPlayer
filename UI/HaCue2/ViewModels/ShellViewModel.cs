using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>
/// The one window: CUES · AUDIO · VIDEO · TARGETS, the title-bar latches, the status bar, and the
/// Output info drawer (register items 1–4).
/// </summary>
/// <remarks>
/// Nothing here talks to an engine. The state it owns is genuinely view state — which view is showing,
/// whether the drawer is up, whether the Lock latch is engaged — which is the same state the real shell
/// will own, so the type survives the wiring rather than being thrown away with the sample data.
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    public const string CuesView = "CUES";
    public const string AudioView = "AUDIO";
    public const string VideoView = "VIDEO";
    public const string TargetsView = "TARGETS";

    public IReadOnlyList<string> Views { get; } = [CuesView, AudioView, VideoView, TargetsView];

    public CuesViewModel Cues { get; } = new();
    public AudioViewModel Audio { get; } = new();
    public VideoViewModel Video { get; } = new();
    public TargetsViewModel Targets { get; } = new();
    public OutputInfoViewModel OutputInfo { get; } = new();

    public string ProjectFile => SampleShow.ProjectFile + " *";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPane))]
    private string _selectedView = CuesView;

    /// <summary>
    /// The Lock latch (register item 2): opt-in, never the launch state, read-only document, and GO
    /// untouched. The shell shows it engaged; nothing in this dummy actually becomes read-only.
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

    public object CurrentPane => SelectedView switch
    {
        AudioView => Audio,
        VideoView => Video,
        TargetsView => Targets,
        _ => Cues,
    };

    /// <summary>The mode chip: "Editing" until the operator latches Lock themselves.</summary>
    public string ModeLabel => IsLocked ? "LOCKED" : "EDITING";

    // ── status bar ────────────────────────────────────────────────────────────────────────────
    public string OutputSummary => "● outputs ok";
    public string IssueSummary => "▲ 2 issues · Project status";
    public string UnsavedSummary => "3 unsaved edits";
    public string SavedSummary => "saved 14:02";
}

/// <summary>The Output info drawer's content (screen 02b) — program meters, line chips, bay counters.</summary>
public sealed class OutputInfoViewModel
{
    public IReadOnlyList<ProgramMeter> Meters { get; } = SampleShow.ProgramMeters;
    public IReadOnlyList<OutputLineChip> Lines { get; } = SampleShow.LineChips;
    public string BaySummary { get; } = SampleShow.BaySummary;
    public string BayClock { get; } = SampleShow.BayClock;
}
