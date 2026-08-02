using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Sample;

namespace HaCue2.ViewModels;

/// <summary>
/// Screens 09–10 — Compositions · Mapping · Outputs · Audition.
/// </summary>
/// <remarks>
/// Tab order is Compositions · Mapping · Outputs because mapping is the stage BETWEEN the two
/// (register item 22), and mapping is a property of an output binding, not of a composition: the same
/// Cyc renders warped to the projector and clean to the lobby TV.
/// <para>
/// A composition owns exactly size, frame rate and idle image. There is deliberately no visualizer
/// flag — that was HaPlay residue; a visualizer is purely a cue type whose canvas presence is an
/// ordinary placement (register item 21).
/// </para>
/// </remarks>
public partial class VideoViewModel : ObservableObject
{
    public const string CompositionsTab = "COMPOSITIONS · 2";
    public const string MappingTab = "MAPPING";
    public const string OutputsTab = "OUTPUTS · 3";
    public const string AuditionTab = "AUDITION";

    public IReadOnlyList<string> Tabs { get; } = [CompositionsTab, MappingTab, OutputsTab, AuditionTab];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompositionsPane))]
    [NotifyPropertyChangedFor(nameof(IsMappingPane))]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private string _selectedTab = CompositionsTab;

    public bool IsCompositionsPane => SelectedTab == CompositionsTab;
    public bool IsMappingPane => SelectedTab == MappingTab;
    public bool IsOutputsPane => SelectedTab == OutputsTab;
    public bool IsAuditionPane => SelectedTab == AuditionTab;

    public string TabHint => SelectedTab switch
    {
        MappingTab => "output: Projector A ▾ · source: Cyc 1920×1080",
        AuditionTab => "same pane appears in Audio · one audition rig",
        _ => "canvas thumbnails are live when the show runs",
    };

    // ── 09 · compositions ─────────────────────────────────────────────────────────────────────
    public string CycHeader { get; } = "Cyc";
    public string CycHint { get; } = "1920×1080 · 29.97 · idle: black";
    public IReadOnlyList<PlacementBox> CycLayers { get; } = SampleShow.CycLayers;

    public string PortalHeader { get; } = "Portal";
    public string PortalHint { get; } = "1280×720 · 30 · idle: logo.png";
    public IReadOnlyList<PlacementBox> PortalLayers { get; } = SampleShow.PortalLayers;

    public IReadOnlyList<VideoOutputRow> Outputs { get; } = SampleShow.VideoOutputs;

    [ObservableProperty]
    private VideoOutputRow? _selectedOutput = SampleShow.VideoOutputs[0];

    public IReadOnlyList<string> Compositions { get; } = ["Cyc", "Portal"];
    public IReadOnlyList<string> Screens { get; } = ["1 · 2560×1440", "2 · 1920×1080", "3 · 1920×1080"];
    public IReadOnlyList<string> IdleImages { get; } = ["black", "logo.png", "venue-logo.png"];

    // ── 10 · mapping ──────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> Sections { get; } = SampleShow.MappingSections;
    public IReadOnlyList<PlacementBox> MappingSource { get; } = SampleShow.MappingSource;
    public IReadOnlyList<PlacementBox> MappingOutput { get; } = SampleShow.MappingOutput;
    public IReadOnlyList<string> WarpModes { get; } = ["off", "3×3", "5×5"];
    public IReadOnlyList<string> MappingOutputs { get; } = ["Projector A", "Lobby TV", "NDI Prog"];

    public AuditionViewModel Audition { get; } = new();
}
