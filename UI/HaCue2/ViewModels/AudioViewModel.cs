using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>Screens 06–08b — Logical outputs · Patch · Devices · Audition.</summary>
public partial class AudioViewModel : ObservableObject
{
    public const string OutputsTab = "LOGICAL OUTPUTS · 12";
    public const string PatchTab = "PATCH";
    public const string DevicesTab = "DEVICES · 5";
    public const string AuditionTab = "AUDITION";

    public IReadOnlyList<string> Tabs { get; } = [OutputsTab, PatchTab, DevicesTab, AuditionTab];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsPatchPane))]
    [NotifyPropertyChangedFor(nameof(IsDevicesPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private string _selectedTab = OutputsTab;

    public bool IsOutputsPane => SelectedTab == OutputsTab;
    public bool IsPatchPane => SelectedTab == PatchTab;
    public bool IsDevicesPane => SelectedTab == DevicesTab;
    public bool IsAuditionPane => SelectedTab == AuditionTab;

    public string TabHint => SelectedTab switch
    {
        PatchTab => "rows: device channels · columns: logical outputs",
        AuditionTab => "same pane appears in Video · one audition rig",
        _ => $"mix {SampleShow.MixRate} · clock master {SampleShow.ClockMaster} · edits apply live",
    };

    // ── 06 · logical outputs ──────────────────────────────────────────────────────────────────
    public IReadOnlyList<LogicalOutputRow> Outputs { get; } = SampleShow.LogicalOutputs;

    [ObservableProperty]
    private LogicalOutputRow? _selectedOutput = SampleShow.LogicalOutputs[^1];

    public IReadOnlyList<string> LobbySenders { get; } = SampleShow.LobbySenders;

    // ── 07 · patch ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<MatrixColumn> PatchColumns { get; } = SampleShow.PatchColumns;
    public IReadOnlyList<MatrixRow> PatchRows { get; } = SampleShow.PatchRows;
    public IReadOnlyList<string> Snapshots { get; } = SampleShow.PatchSnapshots;

    // ── 08 · devices ──────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<AudioLineRow> Lines { get; } = SampleShow.AudioLines;

    [ObservableProperty]
    private AudioLineRow? _selectedLine = SampleShow.AudioLines[2];

    public string PatternTokens { get; } = SampleShow.RecordPatternTokens;
    public IReadOnlyList<string> MixRates { get; } = ["44 100 Hz", "48 000 Hz", "96 000 Hz"];
    public IReadOnlyList<string> ClockMasters { get; } = ["18i20", "NDI Prog", "Stream"];

    // ── 08b · audition ────────────────────────────────────────────────────────────────────────
    // One rig, two halves (register item 15). The identical pane appears in the Video view; both edit
    // this same object, because "the audition rig" is one thing an operator configures once.
    public AuditionViewModel Audition { get; } = new();
}

/// <summary>The audition rig — an audio device plus a video surface, shared by the Audio and Video views.</summary>
public partial class AuditionViewModel : ObservableObject
{
    public IReadOnlyList<string> Devices { get; } =
        ["built-in headphones", "18i20 · Out 9/10", "Behringer UCA222"];

    [ObservableProperty]
    private string _device = "built-in headphones";

    [ObservableProperty]
    private string _level = "−12.0 dB";

    /// <summary>Ducks the monitor while the program is sounding — the booth's own ears, not the mix.</summary>
    [ObservableProperty]
    private bool _duckWhenProgramSounds = true;

    public IReadOnlyList<string> Surfaces { get; } = ["window", "screen 2", "none"];

    [ObservableProperty]
    private string _surface = "window";

    public string Size { get; } = "960×540 · follows composition aspect";
}
