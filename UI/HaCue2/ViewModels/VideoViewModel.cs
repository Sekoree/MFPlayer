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

        Record = new RecordEditor(journal, project, runtime);

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

    public bool HasNoCompositions => Compositions.Count == 0;
    public bool HasNoOutputs => Outputs.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingSource))]
    [NotifyPropertyChangedFor(nameof(MappingTarget))]
    [NotifyPropertyChangedFor(nameof(Sections))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    [NotifyPropertyChangedFor(nameof(SelectedOutputName))]
    private VideoOutputRow? _selectedOutput;

    public string SelectedOutputName => SelectedOutput?.Name ?? "no output selected";

    partial void OnSelectedOutputChanged(VideoOutputRow? value)
    {
        ShowRecordPane();
        RaiseOutputFields();

        // The composition pane follows the output's own composition: an operator who selects the
        // projector and then edits a size expects to be editing what the projector shows.
        SelectedCompositionId = MappedOutput?.CompositionId;
    }

    /// <summary>
    /// The record pane for the selected output (register item 30).
    /// </summary>
    /// <remarks>
    /// The same editor the Audio view uses. A record output and a record line hold the same block and
    /// answer the same questions, so they are configured by the same pane rather than by two that would
    /// drift.
    /// </remarks>
    public RecordEditor Record { get; private set; } = null!;

    /// <summary>Points the record pane at the selection, or at nothing when it is a screen.</summary>
    private void ShowRecordPane()
    {
        if (MappedOutput is not { Kind: VideoOutputKind.Record or VideoOutputKind.Stream } output)
        {
            Record.Show(null);
            return;
        }

        Record.Show(new RecordSubject(
            output.Id,
            output.Name,
            () => output.Record,
            () => output.Record ??= new RecordTarget(),
            CarriesVideo: true,
            IsStream: output.Kind == VideoOutputKind.Stream,
            Channels: 0));
    }

    /// <summary>Re-announces what only the running show knows, on every tick.</summary>
    public void RefreshRecorders() => Record.RefreshRunning();

    public IReadOnlyList<string> CompositionNames =>
        [.. _project.Compositions.Select(composition => composition.Name)];

    // ── 09 · the selected output ──────────────────────────────────────────────────────────────

    /// <summary>The composition this output shows, or -1 when it shows none.</summary>
    public int OutputCompositionIndex
    {
        get => MappedOutput?.CompositionId is { } id
            ? _project.Compositions.FindIndex(composition => composition.Id == id)
            : -1;
        set
        {
            if (MappedOutput is not { } output
                || value < 0
                || value >= _project.Compositions.Count)
                return;

            var chosen = _project.Compositions[value].Id;

            Edit(output, "composition", () => output.CompositionId, id => output.CompositionId = id,
                chosen, $"“{output.Name}” shows {_project.Compositions[value].Name}");
        }
    }

    /// <summary>
    /// Which screen the output opens on, one-based, or 0 for "wherever it opens".
    /// </summary>
    /// <remarks>
    /// Stored in <see cref="VideoOutputDefinition.TargetHint"/> as a number because it is a HINT like an
    /// audio line's: a show carried to a venue with fewer screens finds nothing there, which is a
    /// reported absence rather than a silent move to whichever display answered.
    /// </remarks>
    public int OutputScreenIndex
    {
        get => MappedOutput is { } output && int.TryParse(output.TargetHint, out var display) && display > 0
            ? Math.Min(display, Screens.Count - 1)
            : 0;
        set
        {
            if (MappedOutput is not { } output || value < 0)
                return;

            var hint = value == 0 ? "" : value.ToString(CultureInfo.InvariantCulture);

            Edit(output, "screen", () => output.TargetHint, text => output.TargetHint = text, hint,
                value == 0 ? $"“{output.Name}” opens anywhere" : $"“{output.Name}” on screen {value}");
        }
    }

    /// <summary>Fullscreen (0) or windowed (1).</summary>
    public int OutputFullscreenIndex
    {
        get => MappedOutput?.Fullscreen == false ? 1 : 0;
        set
        {
            if (MappedOutput is not { } output)
                return;

            Edit(output, "fullscreen", () => output.Fullscreen, flag => output.Fullscreen = flag,
                value == 0, value == 0 ? $"“{output.Name}” fullscreen" : $"“{output.Name}” windowed");
        }
    }

    /// <summary>Used only when the composition has no idle image of its own (register item 23).</summary>
    public string OutputIdleFallback
    {
        get => MappedOutput?.IdleFallbackPath ?? "";
        set
        {
            if (MappedOutput is not { } output)
                return;

            Edit(output, "idleFallback", () => output.IdleFallbackPath,
                path => output.IdleFallbackPath = path, value, $"“{output.Name}” idle fallback");
        }
    }

    /// <summary>Mapping in force (0) or clean (1).</summary>
    /// <remarks>
    /// A toggle, never a delete. Switching to clean keeps the authored sections, so an operator who
    /// wants an unwarped feed tonight does not have to author the warp again tomorrow.
    /// </remarks>
    public int OutputMappingIndex
    {
        get => MappedOutput?.MappingEnabled == false ? 1 : 0;
        set
        {
            if (MappedOutput is not { } output)
                return;

            Edit(output, "mappingEnabled", () => output.MappingEnabled,
                flag => output.MappingEnabled = flag, value == 0,
                value == 0 ? $"“{output.Name}” mapped" : $"“{output.Name}” clean");
        }
    }

    /// <summary>Register item 25: an absent REQUIRED output is an error rather than a warning.</summary>
    public bool OutputRequired
    {
        get => MappedOutput?.Required == true;
        set
        {
            if (MappedOutput is not { } output)
                return;

            Edit(output, "required", () => output.Required, flag => output.Required = flag, value,
                value ? $"“{output.Name}” is required" : $"“{output.Name}” is optional");
        }
    }

    /// <summary>What the mapping toggle means for this output, since "clean" has two causes.</summary>
    public string MappingNote => MappedOutput switch
    {
        null => "",
        { Mapping.Count: 0 } => "no sections authored — this output shows a clean feed",
        { MappingEnabled: false } output =>
            $"{output.Mapping.Count} section(s) authored, bypassed — the feed is clean tonight",
        var output => $"{output.Mapping.Count} section(s) in force",
    };

    /// <summary>Writes one output field through the journal and re-announces the pane.</summary>
    private void Edit<T>(
        VideoOutputDefinition output,
        string field,
        Func<T> read,
        Action<T> write,
        T value,
        string label)
    {
        if (EqualityComparer<T>.Default.Equals(read(), value))
            return;

        _journal.Do(new SetValueCommand<T>(output.Id, field, "video", read, write, value, label));
        _journal.CloseGroup();

        RaiseOutputFields();
    }

    private void RaiseOutputFields()
    {
        OnPropertyChanged(nameof(OutputCompositionIndex));
        OnPropertyChanged(nameof(OutputScreenIndex));
        OnPropertyChanged(nameof(OutputFullscreenIndex));
        OnPropertyChanged(nameof(OutputIdleFallback));
        OnPropertyChanged(nameof(OutputMappingIndex));
        OnPropertyChanged(nameof(OutputRequired));
        OnPropertyChanged(nameof(MappingNote));
    }

    // ── 09 · the selected composition ─────────────────────────────────────────────────────────

    /// <summary>The composition the output pane's canvas belongs to.</summary>
    private CompositionDefinition? SelectedComposition =>
        SelectedCompositionId is { } id
            ? _project.Compositions.FirstOrDefault(composition => composition.Id == id)
            : _project.Compositions.FirstOrDefault();

    /// <summary>Which composition the second pane edits; null follows the first.</summary>
    [ObservableProperty]
    private Guid? _selectedCompositionId;

    partial void OnSelectedCompositionIdChanged(Guid? value) => RaiseCompositionFields();

    public string CompositionSize
    {
        get => SelectedComposition is { } composition
            ? $"{composition.Width}×{composition.Height}"
            : "";
        set
        {
            if (SelectedComposition is not { } composition)
                return;

            // Accepts the × it renders and the x anybody types. Refusing the operator's own keyboard
            // would be a strange thing for a field whose value is shown with a character they cannot
            // easily produce.
            var parts = value.Split(['×', 'x', 'X'], StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height)
                || width <= 0
                || height <= 0)
            {
                OnPropertyChanged(nameof(CompositionSize));
                return;
            }

            if (composition.Width == width && composition.Height == height)
                return;

            // ONE composite: a size is one edit, and an undo that took the width back without the
            // height would leave a canvas nobody authored — and every placement in the show is
            // expressed as a fraction of it.
            using (_journal.Composite($"“{composition.Name}” {width}×{height}", "video"))
            {
                _journal.Do(new SetValueCommand<int>(
                    composition.Id, "width", "video",
                    () => composition.Width, number => composition.Width = number, width,
                    $"“{composition.Name}” width"));
                _journal.Do(new SetValueCommand<int>(
                    composition.Id, "height", "video",
                    () => composition.Height, number => composition.Height = number, height,
                    $"“{composition.Name}” height"));
            }

            _journal.CloseGroup();

            RaiseCompositionFields();
        }
    }

    public string CompositionRate
    {
        get => SelectedComposition?.FramesPerSecond.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        set
        {
            if (SelectedComposition is not { } composition
                || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                || rate is <= 0 or > 240)
            {
                OnPropertyChanged(nameof(CompositionRate));
                return;
            }

            if (Math.Abs(composition.FramesPerSecond - rate) < 0.001)
                return;

            _journal.Do(new SetValueCommand<double>(
                composition.Id, "fps", "video",
                () => composition.FramesPerSecond, number => composition.FramesPerSecond = number, rate,
                $"“{composition.Name}” {rate:0.##} fps"));
            _journal.CloseGroup();

            RaiseCompositionFields();
        }
    }

    /// <summary>Register item 21/23: shown when the canvas is empty, ahead of an output's fallback.</summary>
    public string CompositionIdleImage
    {
        get => SelectedComposition?.IdleImagePath ?? "";
        set
        {
            if (SelectedComposition is not { } composition || composition.IdleImagePath == value)
                return;

            _journal.Do(new SetValueCommand<string>(
                composition.Id, "idleImage", "video",
                () => composition.IdleImagePath, path => composition.IdleImagePath = path, value,
                $"“{composition.Name}” idle image"));
            _journal.CloseGroup();

            RaiseCompositionFields();
        }
    }

    public string CompositionHeader => SelectedComposition?.Name ?? "no composition";

    private void RaiseCompositionFields()
    {
        OnPropertyChanged(nameof(CompositionSize));
        OnPropertyChanged(nameof(CompositionRate));
        OnPropertyChanged(nameof(CompositionIdleImage));
        OnPropertyChanged(nameof(CompositionHeader));
    }

    /// <summary>
    /// Screens this machine has, as the window manager reports them.
    /// </summary>
    /// <remarks>
    /// Filled by the view from <c>TopLevel.Screens</c> rather than invented here: it is a MACHINE fact,
    /// and the list used to be three hardcoded resolutions that matched no rig anybody would open this
    /// on. Index 0 is "anywhere", which is a real answer and the one a show that does not care wants.
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<string> _screens = ["anywhere"];

    /// <summary>Adopts the machine's real screen list, keeping "anywhere" first.</summary>
    public void SetScreens(IEnumerable<string> screens)
    {
        Screens = ["anywhere", .. screens];
        OnPropertyChanged(nameof(OutputScreenIndex));
    }

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

    /// <summary>
    /// The SAME rig the Audio view configures.
    /// </summary>
    /// <remarks>
    /// Handed in rather than constructed here: the audition rig is one thing (register item 15), and
    /// two view-models over it would drift the moment either was edited — the operator would set a
    /// surface in Video and find it unset in Audio.
    /// </remarks>
    public AuditionViewModel Audition { get; init; } = new();

    /// <summary>The journal, for the dialogs the view opens.</summary>
    public ProjectJournal Journal => _journal;

    // ── mapping sections ──────────────────────────────────────────────────────────────────────
    // These have nothing to ask, so they act directly rather than opening a prompt. A new section
    // covers the whole frame, which is the only starting shape that is obviously wrong in a way the
    // operator can see and drag into place.

    public void AddSection()
    {
        if (MappedOutput is not { } output)
            return;

        _journal.Do(new AddItemCommand<MappingSection>(
            output.Mapping,
            new MappingSection { Name = $"Section {output.Mapping.Count + 1}" },
            output.Mapping.Count,
            "mapping",
            "add mapping section"));
        _journal.CloseGroup();

        SelectedSection = output.Mapping.Count - 1;
        Refresh();
    }

    /// <summary>
    /// Copies the selected section, offset slightly.
    /// </summary>
    /// <remarks>
    /// Offset rather than exactly on top: two identical sections look like one, and the operator would
    /// drag what they think is the copy and find they moved the original.
    /// </remarks>
    public void DuplicateSection()
    {
        if (Section is not { } section || MappedOutput is not { } output)
            return;

        _journal.Do(new AddItemCommand<MappingSection>(
            output.Mapping,
            section with
            {
                Id = Guid.NewGuid(),
                Name = $"{section.Name} copy",
                TargetX = Math.Min(section.TargetX + 0.02, 1 - section.TargetWidth),
                TargetY = Math.Min(section.TargetY + 0.02, 1 - section.TargetHeight),
            },
            SelectedSection + 1,
            "mapping",
            $"duplicate “{section.Name}”"));
        _journal.CloseGroup();

        SelectedSection = Math.Min(SelectedSection + 1, output.Mapping.Count - 1);
        Refresh();
    }

    public void DeleteSection()
    {
        if (Section is not { } section || MappedOutput is not { } output)
            return;

        _journal.Do(new RemoveItemCommand<MappingSection>(
            output.Mapping, section, "mapping", $"delete “{section.Name}”"));
        _journal.CloseGroup();

        SelectedSection = Math.Clamp(SelectedSection, 0, Math.Max(0, output.Mapping.Count - 1));
        Refresh();
    }

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

        // By LAYER as well as by cue: a cue on two canvases has two rectangles, and the id alone
        // would move whichever came first.
        if (CuePlacements.Of(cue).FirstOrDefault(item => item.LayerIndex == gesture.Layer)
            is not { } placement)
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
