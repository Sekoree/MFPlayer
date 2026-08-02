using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
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
    private readonly ProjectJournal _journal;

    /// <summary>The composite open for the duration of one drag, so the whole gesture is one ⌘Z.</summary>
    private IDisposable? _drag;

    public VideoViewModel(HaCueProject project, ShowRuntime runtime, ProjectJournal journal)
    {
        _project = project;
        _runtime = runtime;
        _journal = journal;

        CompositionsTab = $"COMPOSITIONS · {project.Compositions.Count}";
        OutputsTab = $"OUTPUTS · {project.VideoOutputs.Count}";
        Tabs = [CompositionsTab, MappingTab, OutputsTab, AuditionTab];
        _selectedTab = CompositionsTab;

        // Built ONCE and mutated in place. Re-creating the list on every read would replace the
        // ItemsControl's containers mid-drag, and a control that is replaced loses the pointer capture
        // the drag depends on — the box would follow the pointer for exactly one frame.
        Compositions =
        [
            .. project.Compositions.Select(composition => new CompositionPaneViewModel(
                composition.Id,
                composition.Name,
                $"{composition.Width}×{composition.Height} · {composition.FramesPerSecond:0.##} · idle: "
                + (composition.IdleImagePath.Length == 0 ? "black" : Path.GetFileName(composition.IdleImagePath)),
                (double)composition.Width / composition.Height)
            {
                Layers = VideoPresentation.Layers(project, composition),
            }),
        ];

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
    public IReadOnlyList<CompositionPaneViewModel> Compositions { get; }

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
    [NotifyPropertyChangedFor(nameof(SectionHeader))]
    [NotifyPropertyChangedFor(nameof(HasSection))]
    private int _selectedSection;

    partial void OnSelectedSectionChanged(int value) => RaiseSectionFields();

    public IReadOnlyList<string> Sections =>
        MappedOutput is null ? [] : VideoPresentation.SectionLabels(MappedOutput);

    public IReadOnlyList<PlacementBox> MappingSource =>
        MappedOutput is null ? [] : VideoPresentation.MappingSource(MappedOutput, SelectedSection);

    public IReadOnlyList<PlacementBox> MappingTarget =>
        MappedOutput is null ? [] : VideoPresentation.MappingTarget(MappedOutput, SelectedSection);

    public IReadOnlyList<string> WarpModes { get; } = ["off", "3×3", "5×5"];

    public AuditionViewModel Audition { get; } = new();

    // ── the section editor: register item 22's numeric route ───────────────────────────────────
    // The same eight numbers a drag moves, typed. The pane already told the operator these existed;
    // a canvas alone cannot hit "exactly half", which is the number a two-projector blend is made of.

    private MappingSection? Section =>
        MappedOutput is { } output && SelectedSection >= 0 && SelectedSection < output.Mapping.Count
            ? output.Mapping[SelectedSection]
            : null;

    public string SectionHeader =>
        Section is { } section ? $"Section {SelectedSection + 1} · {section.Name}" : "No section selected";

    public bool HasSection => Section is not null;

    public string SourceXValue
    {
        get => Percent(Section?.SourceX);
        set => WriteRect(target: false, RectPart.X, value);
    }

    public string SourceYValue
    {
        get => Percent(Section?.SourceY);
        set => WriteRect(target: false, RectPart.Y, value);
    }

    public string SourceWidthValue
    {
        get => Percent(Section?.SourceWidth);
        set => WriteRect(target: false, RectPart.Width, value);
    }

    public string SourceHeightValue
    {
        get => Percent(Section?.SourceHeight);
        set => WriteRect(target: false, RectPart.Height, value);
    }

    public string TargetXValue
    {
        get => Percent(Section?.TargetX);
        set => WriteRect(target: true, RectPart.X, value);
    }

    public string TargetYValue
    {
        get => Percent(Section?.TargetY);
        set => WriteRect(target: true, RectPart.Y, value);
    }

    public string TargetWidthValue
    {
        get => Percent(Section?.TargetWidth);
        set => WriteRect(target: true, RectPart.Width, value);
    }

    public string TargetHeightValue
    {
        get => Percent(Section?.TargetHeight);
        set => WriteRect(target: true, RectPart.Height, value);
    }

    public string RotationValue
    {
        get => Section is { } section ? $"{section.RotationDegrees:0.0}°" : "—";
        set => WriteNumber(value, "rotation", 1,
            section => section.RotationDegrees, (section, number) => section.RotationDegrees = number);
    }

    public string OpacityValue
    {
        get => Percent(Section?.Opacity);
        set => WriteNumber(value, "opacity", 0.01,
            section => section.Opacity, (section, number) => section.Opacity = Math.Clamp(number, 0, 1));
    }

    public string BrightnessValue
    {
        get => Section is { } section ? section.Brightness.ToString("0.00", CultureInfo.CurrentCulture) : "—";
        set => WriteNumber(value, "brightness", 1,
            section => section.Brightness, (section, number) => section.Brightness = Math.Clamp(number, 0, 2));
    }

    /// <summary>Index into <see cref="WarpModes"/>: off · 3×3 · 5×5.</summary>
    public int WarpIndex
    {
        get => Section?.WarpGrid switch { 3 => 1, 5 => 2, _ => 0 };
        set
        {
            if (Section is not { } section)
                return;

            var grid = value switch { 1 => 3, 2 => 5, _ => 0 };
            if (grid == section.WarpGrid)
                return;

            _journal.Do(new SetValueCommand<int>(
                section.Id, "warp", "mapping",
                () => section.WarpGrid, number => section.WarpGrid = number, grid,
                grid == 0 ? "warp off" : $"warp {grid}×{grid}"));
            _journal.CloseGroup();
            Refresh();
        }
    }

    private enum RectPart
    {
        X,
        Y,
        Width,
        Height,
    }

    private void WriteRect(bool target, RectPart part, string text)
    {
        if (Section is not { } section || !TryFraction(text, out var fraction))
            return;

        var rect = target
            ? new NormalizedRect(
                section.TargetX, section.TargetY, section.TargetWidth, section.TargetHeight)
            : new NormalizedRect(
                section.SourceX, section.SourceY, section.SourceWidth, section.SourceHeight);

        rect = part switch
        {
            RectPart.X => rect with { X = fraction },
            RectPart.Y => rect with { Y = fraction },
            RectPart.Width => rect with { Width = fraction },
            _ => rect with { Height = fraction },
        };

        _journal.Do(target
            ? RectEdits.MappingTarget(section, rect)
            : RectEdits.MappingSource(section, rect));

        // A typed value is one decision, not a gesture in progress: close the step immediately so the
        // next field's edit is separately undoable.
        _journal.CloseGroup();
        Refresh();
    }

    private void WriteNumber(
        string text, string property, double scale,
        Func<MappingSection, double> read, Action<MappingSection, double> write)
    {
        if (Section is not { } section
            || !double.TryParse(Digits(text), NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
            return;

        var value = typed * scale;
        _journal.Do(new SetValueCommand<double>(
            section.Id, property, "mapping",
            () => read(section), number => write(section, number), value,
            $"set section {property}"));
        _journal.CloseGroup();
        Refresh();
    }

    private static bool TryFraction(string text, out double fraction)
    {
        fraction = 0;

        if (!double.TryParse(Digits(text), NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
            return false;

        fraction = typed / 100;
        return true;
    }

    /// <summary>Everything but the number, dropped — so "42 %" and "42" both mean 42.</summary>
    private static string Digits(string text) =>
        new([.. text.Where(character => char.IsDigit(character) || character is '.' or ',' or '-' or '+')]);

    private static string Percent(double? fraction) =>
        fraction is null ? "—" : $"{fraction.Value * 100:0.#} %";

    // ── canvas gestures (register item 22: drag · numeric entry · arrow nudge) ─────────────────

    /// <summary>A drag or nudge on a composition canvas: moves the placement of the cue it draws.</summary>
    public void ApplyLayerGesture(PlacementGesture gesture)
    {
        if (_project.FindCue(gesture.SubjectId) is not { } cue)
            return;

        var placement = cue switch
        {
            MediaCueNode media => media.Placement,
            VisualizerCueNode visualizer => visualizer.Placement,
            _ => null,
        };

        if (placement is null)
            return;

        _drag ??= _journal.Composite("move layer", "video");
        _journal.Do(RectEdits.Placement(cue, placement, gesture.Rect));
        Refresh();
    }

    public void ApplyMappingSourceGesture(PlacementGesture gesture) =>
        ApplyMappingGesture(gesture, RectEdits.MappingSource, "move source region");

    public void ApplyMappingTargetGesture(PlacementGesture gesture) =>
        ApplyMappingGesture(gesture, RectEdits.MappingTarget, "move output region");

    private void ApplyMappingGesture(
        PlacementGesture gesture,
        Func<MappingSection, NormalizedRect, SetRectCommand> command,
        string description)
    {
        if (MappedOutput?.Mapping.FirstOrDefault(item => item.Id == gesture.SubjectId) is not { } section)
            return;

        _drag ??= _journal.Composite(description, "mapping");
        _journal.Do(command(section, gesture.Rect));
        SelectedSection = gesture.Index;
        Refresh();
    }

    /// <summary>
    /// Ends the gesture: closes the undo step so the next drag starts a new one.
    /// </summary>
    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    /// <summary>Selects the box a click landed on, so the numeric fields address the same section.</summary>
    public void SelectSection(int index) => SelectedSection = index;

    /// <summary>Re-reads every canvas from the document — after an edit here, or an undo anywhere.</summary>
    public void Refresh()
    {
        foreach (var pane in Compositions)
        {
            var composition = _project.Compositions.FirstOrDefault(item => item.Id == pane.Id);
            if (composition is not null)
                pane.Layers = VideoPresentation.Layers(_project, composition);
        }

        OnPropertyChanged(nameof(MappingSource));
        OnPropertyChanged(nameof(MappingTarget));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(SectionHeader));
        RaiseSectionFields();
    }

    private void RaiseSectionFields()
    {
        OnPropertyChanged(nameof(SourceXValue));
        OnPropertyChanged(nameof(SourceYValue));
        OnPropertyChanged(nameof(SourceWidthValue));
        OnPropertyChanged(nameof(SourceHeightValue));
        OnPropertyChanged(nameof(TargetXValue));
        OnPropertyChanged(nameof(TargetYValue));
        OnPropertyChanged(nameof(TargetWidthValue));
        OnPropertyChanged(nameof(TargetHeightValue));
        OnPropertyChanged(nameof(RotationValue));
        OnPropertyChanged(nameof(OpacityValue));
        OnPropertyChanged(nameof(BrightnessValue));
        OnPropertyChanged(nameof(WarpIndex));
    }
}

/// <summary>One composition's canvas: its header, its aspect ratio, and the cues placed on it.</summary>
/// <param name="Aspect">
/// Width ÷ height, so the canvas is drawn at the FRAME's shape. A placement checked on a canvas of the
/// wrong aspect does not tell you what will hit the wall.
/// </param>
public sealed partial class CompositionPaneViewModel(
    Guid id, string name, string hint, double aspect) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Hint { get; } = hint;
    public double Aspect { get; } = aspect;

    /// <summary>Settable so a drag updates the boxes without replacing the canvas drawing them.</summary>
    [ObservableProperty]
    private IReadOnlyList<PlacementBox> _layers = [];
}
