using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>Screens 06–08b — Logical outputs · Patch · Devices · Audition, all from the document.</summary>
public partial class AudioViewModel : ObservableObject
{
    private readonly HaCueProject _project;
    private readonly ShowRuntime _runtime;

    public AudioViewModel(HaCueProject project, ShowRuntime runtime)
    {
        _project = project;
        _runtime = runtime;

        // The counts in the tab labels are the real ones: add a logical output and the tab says so.
        OutputsTab = $"LOGICAL OUTPUTS · {project.AudioPatch.LogicalChannels.Count}";
        DevicesTab = $"DEVICES · {project.AudioLines.Count}";
        Tabs = [OutputsTab, PatchTab, DevicesTab, AuditionTab];
        _selectedTab = OutputsTab;

        Outputs = AudioPresentation.LogicalOutputs(project, runtime);
        _selectedOutput = Outputs.FirstOrDefault(row => row.PatchedTo.IsBad) ?? Outputs.FirstOrDefault();
        Lines = AudioPresentation.Lines(project, runtime);
        _selectedLine = Lines.FirstOrDefault(row => row.Kind.StartsWith("File", StringComparison.Ordinal))
                        ?? Lines.FirstOrDefault();
    }

    public const string PatchTab = "PATCH";
    public const string AuditionTab = "AUDITION";

    public string OutputsTab { get; }
    public string DevicesTab { get; }
    public IReadOnlyList<string> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsPatchPane))]
    [NotifyPropertyChangedFor(nameof(IsDevicesPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private string _selectedTab;

    public bool IsOutputsPane => SelectedTab == OutputsTab;
    public bool IsPatchPane => SelectedTab == PatchTab;
    public bool IsDevicesPane => SelectedTab == DevicesTab;
    public bool IsAuditionPane => SelectedTab == AuditionTab;

    public string TabHint => SelectedTab switch
    {
        PatchTab => "rows: device channels · columns: logical outputs",
        AuditionTab => "same pane appears in Video · one audition rig",
        _ => $"mix {_project.AudioPatch.MixSampleRate:N0} Hz · clock master {ClockMasterName} · edits apply live",
    };

    private string ClockMasterName =>
        _project.AudioPatch.ClockMasterLineId is { } id
            ? _project.FindLine(id)?.Name ?? "none"
            : "none";

    // ── 06 · logical outputs ──────────────────────────────────────────────────────────────────
    public IReadOnlyList<LogicalOutputRow> Outputs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Senders))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputName))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputHint))]
    [NotifyPropertyChangedFor(nameof(IsSelectedOutputUnpatched))]
    private LogicalOutputRow? _selectedOutput;

    public string SelectedOutputName => SelectedOutput?.Name ?? "no output selected";

    public string SelectedOutputHint => SelectedOutput is null
        ? ""
        : SelectedOutput.HasGroup ? $"logical output · in group “{SelectedOutput.Group}”" : "logical output";

    public bool IsSelectedOutputUnpatched => SelectedOutput?.PatchedTo.IsBad ?? false;

    public IReadOnlyList<string> Senders => SelectedOutput is null
        ? []
        : AudioPresentation.Senders(_project, SelectedOutput.Id);

    // ── 07 · patch ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<MatrixColumn> PatchColumns => AudioPresentation.PatchColumns(_project);
    public IReadOnlyList<MatrixRow> PatchRows => AudioPresentation.PatchRows(_project, _runtime);

    public IReadOnlyList<string> Snapshots =>
    [
        .. _project.PatchSnapshots.Select(snapshot =>
            $"▸ {snapshot.Name} · {snapshot.Cells.Count} cell{(snapshot.Cells.Count == 1 ? "" : "s")}"),
    ];

    // ── 08 · devices ──────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<AudioLineRow> Lines { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLineName))]
    private AudioLineRow? _selectedLine;

    public string SelectedLineName => SelectedLine?.Name ?? "no line selected";

    public string PatternTokens { get; } = SampleShow.RecordPatternTokens;

    public IReadOnlyList<string> MixRates { get; } = ["44 100 Hz", "48 000 Hz", "96 000 Hz"];

    public IReadOnlyList<string> ClockMasters =>
        [.. _project.AudioLines.Select(line => line.Name)];

    // ── 08b · audition ────────────────────────────────────────────────────────────────────────
    public AuditionViewModel Audition { get; } = new();
}

/// <summary>The audition rig — an audio device plus a video surface, shared by the Audio and Video views.</summary>
public partial class AuditionViewModel : ObservableObject
{
    public IReadOnlyList<string> Devices { get; } =
        ["built-in headphones", "18i20 · Out 9/10", "Behringer UCA222"];

    [ObservableProperty] private string _device = "built-in headphones";
    [ObservableProperty] private string _level = "−12.0 dB";

    /// <summary>Ducks the monitor while the program is sounding — the booth's own ears, not the mix.</summary>
    [ObservableProperty] private bool _duckWhenProgramSounds = true;

    public IReadOnlyList<string> Surfaces { get; } = ["window", "screen 2", "none"];

    [ObservableProperty] private string _surface = "window";

    public string Size { get; } = "960×540 · follows composition aspect";
}
