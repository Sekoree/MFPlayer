using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// Screens 09–10 — Compositions · Mapping · Outputs · Audition, projected from the document.
/// </summary>
/// <remarks>
/// Tab order is Compositions · Mapping · Outputs because mapping is the stage BETWEEN the two
/// (register item 22), and mapping belongs to an output binding rather than to a composition: the same
/// canvas renders warped to a projector and clean to a TV. A composition owns exactly size, frame rate
/// and idle image — there is deliberately no visualizer flag anywhere in this view.
/// </remarks>
public partial class VideoViewModel : ObservableObject
{
    private readonly HaCueProject _project;
    private readonly ShowRuntime _runtime;

    public VideoViewModel(HaCueProject project, ShowRuntime runtime)
    {
        _project = project;
        _runtime = runtime;

        CompositionsTab = $"COMPOSITIONS · {project.Compositions.Count}";
        OutputsTab = $"OUTPUTS · {project.VideoOutputs.Count}";
        Tabs = [CompositionsTab, MappingTab, OutputsTab, AuditionTab];
        _selectedTab = CompositionsTab;

        Outputs = VideoPresentation.Outputs(project, runtime);
        // The mapped output is the interesting one to land on: a clean feed has nothing to show here.
        _selectedOutput = Outputs.FirstOrDefault(row => row.Map != "clean") ?? Outputs.FirstOrDefault();
    }

    public const string MappingTab = "MAPPING";
    public const string AuditionTab = "AUDITION";

    public string CompositionsTab { get; }
    public string OutputsTab { get; }
    public IReadOnlyList<string> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompositionsPane))]
    [NotifyPropertyChangedFor(nameof(IsMappingPane))]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private string _selectedTab;

    public bool IsCompositionsPane => SelectedTab == CompositionsTab;
    public bool IsMappingPane => SelectedTab == MappingTab;
    public bool IsOutputsPane => SelectedTab == OutputsTab;
    public bool IsAuditionPane => SelectedTab == AuditionTab;

    public string TabHint => SelectedTab switch
    {
        MappingTab => $"output: {SelectedOutput?.Name ?? "none"} ▾ · source: {MappedCompositionLabel}",
        AuditionTab => "same pane appears in Audio · one audition rig",
        _ => "canvas thumbnails are live when the show runs",
    };

    // ── 09 · compositions ─────────────────────────────────────────────────────────────────────
    public IReadOnlyList<CompositionPaneViewModel> Compositions =>
    [
        .. _project.Compositions.Select(composition => new CompositionPaneViewModel(
            composition.Name,
            $"{composition.Width}×{composition.Height} · {composition.FramesPerSecond:0.##} · idle: "
            + (composition.IdleImagePath.Length == 0 ? "black" : Path.GetFileName(composition.IdleImagePath)),
            (double)composition.Width / composition.Height,
            VideoPresentation.Layers(_project, composition))),
    ];

    public IReadOnlyList<VideoOutputRow> Outputs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingSource))]
    [NotifyPropertyChangedFor(nameof(MappingTarget))]
    [NotifyPropertyChangedFor(nameof(Sections))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputName))]
    private VideoOutputRow? _selectedOutput;

    public string SelectedOutputName => SelectedOutput?.Name ?? "no output selected";

    public IReadOnlyList<string> CompositionNames =>
        [.. _project.Compositions.Select(composition => composition.Name)];

    public IReadOnlyList<string> IdleImages { get; } = ["black", "logo.png", "venue-logo.png"];

    /// <summary>
    /// Screens this machine has — a MACHINE fact, so it will come from the runtime seam once screen
    /// enumeration is real. Listed here meanwhile so the picker has something to offer.
    /// </summary>
    public IReadOnlyList<string> Screens { get; } = ["1 · 2560×1440", "2 · 1920×1080", "3 · 1920×1080"];

    // ── 10 · mapping ──────────────────────────────────────────────────────────────────────────
    private VideoOutputDefinition? MappedOutput =>
        SelectedOutput is null
            ? null
            : _project.VideoOutputs.FirstOrDefault(output => output.Id == SelectedOutput.Id);

    private string MappedCompositionLabel
    {
        get
        {
            var composition = MappedOutput?.CompositionId is { } id
                ? _project.Compositions.FirstOrDefault(item => item.Id == id)
                : null;

            return composition is null ? "none" : $"{composition.Name} {composition.Width}×{composition.Height}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingSource))]
    [NotifyPropertyChangedFor(nameof(MappingTarget))]
    private int _selectedSection;

    public IReadOnlyList<string> Sections =>
        MappedOutput is null ? [] : VideoPresentation.SectionLabels(MappedOutput);

    public IReadOnlyList<PlacementBox> MappingSource =>
        MappedOutput is null ? [] : VideoPresentation.MappingSource(MappedOutput, SelectedSection);

    public IReadOnlyList<PlacementBox> MappingTarget =>
        MappedOutput is null ? [] : VideoPresentation.MappingTarget(MappedOutput, SelectedSection);

    public IReadOnlyList<string> WarpModes { get; } = ["off", "3×3", "5×5"];

    public AuditionViewModel Audition { get; } = new();
}

/// <summary>One composition's canvas: its header, its aspect ratio, and the cues placed on it.</summary>
/// <param name="Aspect">
/// Width ÷ height, so the canvas is drawn at the FRAME's shape. A placement checked on a canvas of the
/// wrong aspect does not tell you what will hit the wall.
/// </param>
public sealed record CompositionPaneViewModel(
    string Name, string Hint, double Aspect, IReadOnlyList<PlacementBox> Layers);
