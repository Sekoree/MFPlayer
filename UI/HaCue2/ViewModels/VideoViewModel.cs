using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>
/// Screens 09–10 — Compositions · Mapping · Outputs · Audition, projected from the document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tab order is the order of the work.</b> Outputs first, because an output is a piece of THIS
/// machine — a screen, a sender, a recorder — and exists before any show is authored against it.
/// Compositions second, because a canvas is a decision about the show, and choosing which outputs a
/// canvas feeds is the moment the two meet. Mapping last, because it is the fine adjustment between
/// them and there is nothing to adjust until both ends exist.
/// </para>
/// <para>
/// It used to run Compositions · Mapping · Outputs, and the add-output dialog asked which composition
/// the new output showed — so the first thing an operator did on a new project was answer a question
/// about a canvas that did not exist yet.
/// </para>
/// <para>
/// Mapping still belongs to an output BINDING rather than to a composition: the same canvas renders
/// warped to a projector and clean to a TV. A composition owns exactly size, frame rate and idle image
/// — there is deliberately no visualizer flag anywhere in this view.
/// </para>
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

        OutputsTab = new SectionTab(OutputsKey, "OUTPUTS");
        CompositionsTab = new SectionTab(CompositionsKey, "COMPOSITIONS");
        MappingTab = new SectionTab(MappingKey, "MAPPING");
        AuditionTab = new SectionTab(AuditionKey, "AUDITION");
        Tabs = [OutputsTab, CompositionsTab, MappingTab, AuditionTab];
        _selectedTab = OutputsTab;
        CountTabs();

        Compositions = Panes();
        Outputs = VideoPresentation.Outputs(project, runtime);
        // The mapped output is the interesting one to land on: a clean feed has nothing to show here.
        _selectedOutput = Outputs.FirstOrDefault(row => row.Map != "clean") ?? Outputs.FirstOrDefault();
        _selectedCompositionId = MappedOutput?.CompositionId;
        RebuildSections();
    }

    public const string OutputsKey = "outputs";
    public const string CompositionsKey = "compositions";
    public const string MappingKey = "mapping";
    public const string AuditionKey = "audition";

    public SectionTab OutputsTab { get; }
    public SectionTab CompositionsTab { get; }
    public SectionTab MappingTab { get; }
    public SectionTab AuditionTab { get; }
    public IReadOnlyList<SectionTab> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompositionsPane))]
    [NotifyPropertyChangedFor(nameof(IsMappingPane))]
    [NotifyPropertyChangedFor(nameof(IsOutputsPane))]
    [NotifyPropertyChangedFor(nameof(IsAuditionPane))]
    [NotifyPropertyChangedFor(nameof(TabHint))]
    private SectionTab _selectedTab;

    public bool IsCompositionsPane => SelectedTab?.Key == CompositionsKey;
    public bool IsMappingPane => SelectedTab?.Key == MappingKey;
    public bool IsOutputsPane => SelectedTab?.Key == OutputsKey;
    public bool IsAuditionPane => SelectedTab?.Key == AuditionKey;

    public string TabHint => SelectedTab?.Key switch
    {
        MappingKey => $"output: {SelectedOutput?.Name ?? "none"} · source: {MappedCompositionLabel}",
        AuditionKey => "same pane appears in Audio · one audition rig",
        CompositionsKey => "a canvas, and which outputs it feeds",
        _ => "what this machine can put a picture on",
    };

    private void CountTabs()
    {
        OutputsTab.Count("OUTPUTS", _project.VideoOutputs.Count);
        CompositionsTab.Count("COMPOSITIONS", _project.Compositions.Count);
    }

    // ── 09 · compositions ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One pane per composition.
    /// </summary>
    /// <remarks>
    /// Rebuilt only when the SET of compositions changes, and mutated in place otherwise. Re-creating
    /// the list on every refresh would replace the items control's containers mid-drag, and a control
    /// that is replaced loses the pointer capture the drag depends on — a placement box would follow
    /// the pointer for exactly one frame. Never rebuilding it at all was worse: an added composition
    /// simply never appeared.
    /// </remarks>
    public IReadOnlyList<CompositionPaneViewModel> Compositions { get; private set; }

    public IReadOnlyList<VideoOutputRow> Outputs { get; private set; }

    /// <summary>A pane per composition, reading the document as it stands.</summary>
    private IReadOnlyList<CompositionPaneViewModel> Panes() =>
    [
        .. _project.Compositions.Select(composition => new CompositionPaneViewModel(
            composition.Id,
            composition.Name,
            $"{composition.Width}×{composition.Height} · {composition.FramesPerSecond:0.##} · idle: "
            + (composition.IdleImagePath.Length == 0 ? "black" : Path.GetFileName(composition.IdleImagePath)),
            (double)composition.Width / composition.Height)
        {
            Layers = VideoPresentation.Layers(_project, composition),
            Feeds = Feeds(composition.Id),
        }),
    ];

    /// <summary>The outputs this composition is sent to, in document order.</summary>
    private IReadOnlyList<CompositionFeedRow> Feeds(Guid compositionId) =>
    [
        .. _project.VideoOutputs
            .Where(output => output.CompositionId == compositionId)
            .Select(output => new CompositionFeedRow(
                output.Id,
                output.Name,
                _runtime.AbsentVideoOutputs.Contains(output.Id) ? "not open" : "live",
                !_runtime.AbsentVideoOutputs.Contains(output.Id))),
    ];

    public bool HasNoCompositions => Compositions.Count == 0;
    public bool HasNoOutputs => Outputs.Count == 0;

    /// <summary>
    /// What to do first, which depends on whether there is a canvas yet.
    /// </summary>
    /// <remarks>
    /// The order is a real dependency and nothing else on the screen states it: an output SHOWS a
    /// composition, so with none authored the Shows picker is empty and a new output points at
    /// nothing. Saying which comes first is cheaper than letting somebody find out.
    /// </remarks>
    public string OutputsEmptyDetail =>
        HasNoCompositions
            ? "Add a COMPOSITION first — an output shows one, and until there is one there is nothing "
              + "for an output to point at."
            : "Nothing receives a composition yet. Add a local screen, an NDI sender, a recorder or a "
              + "stream below.";

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

        // The refusal was about the output that was selected when it happened.
        OutputProblem = "";

        // The composition pane follows the output's own composition: an operator who selects the
        // projector and then goes to look at a canvas expects to be looking at what the projector
        // shows. NOT while they are standing ON the Compositions tab, though — there their own click
        // is the selection, and refreshing the output rows underneath them (a screen going absent, a
        // recorder arming) would otherwise pull the pane back to somewhere they did not choose.
        if (!IsCompositionsPane)
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
        // Through the engine's own reader, so the pane and the window agree about which screen a hint
        // names — including the "2 · 1920×1080" labels the add-output dialog used to store.
        get => MappedOutput is { } output && ProjectVideoOutputs.ScreenNumber(output.TargetHint) is { } display
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

    /// <summary>
    /// The windowed size, as "960×540". Empty means the composition's own.
    /// </summary>
    /// <remarks>
    /// Editable after creation, because how big a monitor window is on THIS machine is exactly the
    /// kind of thing somebody adjusts once they can see it — and only visible while windowed, since a
    /// fullscreen output takes the screen's size and the field would be a control that does nothing.
    /// </remarks>
    public string OutputWindowSize
    {
        get => MappedOutput is { WindowWidth: > 0, WindowHeight: > 0 } output
            ? $"{output.WindowWidth}×{output.WindowHeight}"
            : "";
        set
        {
            if (MappedOutput is not { } output)
                return;

            var (width, height) = Dialogs.WindowSize(value);

            Edit(output, "windowWidth", () => output.WindowWidth, size => output.WindowWidth = size,
                width, $"set “{output.Name}” window width");
            Edit(output, "windowHeight", () => output.WindowHeight, size => output.WindowHeight = size,
                height, $"set “{output.Name}” window size");
        }
    }

    public bool IsOutputWindowed => MappedOutput?.Fullscreen == false;

    /// <summary>
    /// The output's real pixel size, as "1920×1080". Empty follows the composition.
    /// </summary>
    /// <remarks>
    /// The number a video wall is built out of. Mapping destinations are measured in this raster, so a
    /// 1920×2160 stacked canvas split across two 1920×1080 projectors needs each output to say 1080 —
    /// against the canvas both halves would be described as 2160 tall and land at half height.
    /// </remarks>
    public string OutputRaster
    {
        get => MappedOutput is { MappingWidth: > 0, MappingHeight: > 0 } output
            ? $"{output.MappingWidth}×{output.MappingHeight}"
            : "";
        set
        {
            if (MappedOutput is not { } output)
                return;

            var (width, height) = Dialogs.WindowSize(value);

            using (_journal.Composite($"“{output.Name}” output raster", "video"))
            {
                _journal.Do(new SetValueCommand<int>(
                    output.Id, "mappingWidth", "video",
                    () => output.MappingWidth, size => output.MappingWidth = size, width,
                    $"set “{output.Name}” raster width"));
                _journal.Do(new SetValueCommand<int>(
                    output.Id, "mappingHeight", "video",
                    () => output.MappingHeight, size => output.MappingHeight = size, height,
                    $"set “{output.Name}” raster height"));
            }

            _journal.CloseGroup();
            RaiseOutputFields();
            Refresh();
        }
    }

    /// <summary>Which composition an output shows, said in the output's own inspector.</summary>
    public string OutputComposition =>
        MappedOutput?.CompositionId is { } id
        && _project.Compositions.FirstOrDefault(item => item.Id == id) is { } composition
            ? $"{composition.Name} · {composition.Width}×{composition.Height}"
            : "nothing — assign it under COMPOSITIONS";

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

    /// <summary>
    /// How an idle image fills its surface. The same six fits a layer placement offers.
    /// </summary>
    /// <remarks>
    /// A holding slate is rarely the canvas's own shape — it is a logo, or a photograph somebody had —
    /// and it used to be stretched to fill, which is the one option that always looks wrong.
    /// </remarks>
    public static IReadOnlyList<string> IdleFits { get; } =
        ["contain", "cover", "stretch", "centre", "fill width", "fill height"];

    public int OutputIdleFitIndex
    {
        get => MappedOutput is { } output ? (int)output.IdleFallbackFit : -1;
        set
        {
            if (value < 0 || MappedOutput is not { } output || (int)output.IdleFallbackFit == value)
                return;

            Edit(output, "idleFallbackFit", () => output.IdleFallbackFit,
                fit => output.IdleFallbackFit = fit, (LayerFit)value,
                $"“{output.Name}” idle fit: {IdleFits[value]}");
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

    /// <summary>
    /// The last refusal from a press on this pane, or nothing.
    /// </summary>
    /// <remarks>
    /// Held rather than shown in a dialog: IDENTIFY fails for ordinary, recoverable reasons — no show
    /// running, no composition on the output — and a modal for each of them is a modal the operator
    /// dismisses without reading. Cleared by selecting another output, because it was about that one.
    /// </remarks>
    [ObservableProperty]
    private string _outputProblem = "";

    /// <summary>Records a refusal so it survives the click that produced it.</summary>
    public void NoteProblem(string problem) => OutputProblem = problem;

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
        OnPropertyChanged(nameof(OutputComposition));
        OnPropertyChanged(nameof(OutputScreenIndex));
        OnPropertyChanged(nameof(OutputFullscreenIndex));
        OnPropertyChanged(nameof(OutputWindowSize));
        OnPropertyChanged(nameof(IsOutputWindowed));
        OnPropertyChanged(nameof(IsOutputLocal));
        OnPropertyChanged(nameof(OutputRaster));
        OnPropertyChanged(nameof(OutputIdleFallback));
        OnPropertyChanged(nameof(OutputIdleFitIndex));
        OnPropertyChanged(nameof(OutputMappingIndex));
        OnPropertyChanged(nameof(OutputRequired));
        OnPropertyChanged(nameof(MappingNote));
        OnPropertyChanged(nameof(HasOutput));
    }

    /// <summary>Whether the selected output opens a window on this machine.</summary>
    public bool IsOutputLocal => MappedOutput?.Kind == VideoOutputKind.LocalScreen;

    public bool HasOutput => MappedOutput is not null;

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

    /// <summary>How the composition's idle image fills the canvas.</summary>
    public int CompositionIdleFitIndex
    {
        get => SelectedComposition is { } composition ? (int)composition.IdleImageFit : -1;
        set
        {
            if (value < 0
                || SelectedComposition is not { } composition
                || (int)composition.IdleImageFit == value)
                return;

            _journal.Do(new SetValueCommand<LayerFit>(
                composition.Id, "idleImageFit", "video",
                () => composition.IdleImageFit, fit => composition.IdleImageFit = fit, (LayerFit)value,
                $"“{composition.Name}” idle fit: {IdleFits[value]}"));
            _journal.CloseGroup();

            RaiseCompositionFields();
        }
    }

    public string CompositionHeader => SelectedComposition?.Name ?? "no composition";

    public bool HasComposition => SelectedComposition is not null;

    public string CompositionName
    {
        get => SelectedComposition?.Name ?? "";
        set
        {
            var name = (value ?? "").Trim();

            if (SelectedComposition is not { } composition || name.Length == 0 || composition.Name == name)
            {
                OnPropertyChanged(nameof(CompositionName));
                return;
            }

            _journal.Do(new SetValueCommand<string>(
                composition.Id, "name", "video",
                () => composition.Name, text => composition.Name = text, name,
                $"rename composition to “{name}”"));
            _journal.CloseGroup();

            RaiseCompositionFields();
            Refresh();
        }
    }

    // ── which outputs a composition feeds (assignment lives HERE) ──────────────────────────────
    // The document stores the link on the OUTPUT, and it still does. What changed is where it is
    // AUTHORED: an operator builds the canvas and then decides what it goes to, and answering "which
    // composition?" inside the add-output dialog asked about a canvas that did not exist yet.

    /// <summary>The outputs the selected composition is sent to.</summary>
    public IReadOnlyList<CompositionFeedRow> SelectedCompositionFeeds =>
        SelectedComposition is { } composition ? Feeds(composition.Id) : [];

    /// <summary>
    /// The outputs that could be added to the selected composition.
    /// </summary>
    /// <remarks>
    /// Outputs already on ANOTHER composition are offered too, and say so: one output shows one canvas,
    /// so picking a taken output MOVES it. Hiding them would leave an operator hunting for a projector
    /// that is simply pointed elsewhere, with nothing on screen saying where.
    /// </remarks>
    public IReadOnlyList<string> AssignableOutputs =>
        SelectedComposition is not { } composition
            ? []
            :
            [
                .. Assignable(composition.Id).Select(output =>
                    output.CompositionId is { } other
                        ? $"{output.Name} · move from "
                          + (_project.Compositions.FirstOrDefault(item => item.Id == other)?.Name ?? "?")
                        : output.Name),
            ];

    private List<VideoOutputDefinition> Assignable(Guid compositionId) =>
        [.. _project.VideoOutputs.Where(output => output.CompositionId != compositionId)];

    [ObservableProperty]
    private int _assignableIndex;

    public bool CanAssignOutput => SelectedComposition is not null && AssignableOutputs.Count > 0;

    /// <summary>Why there is nothing to assign — a project with no outputs looks the same as a bug.</summary>
    public string AssignHint =>
        SelectedComposition is null
            ? "select a composition"
            : _project.VideoOutputs.Count == 0
                ? "no outputs yet — add one under OUTPUTS, then send this composition to it"
                : AssignableOutputs.Count == 0
                    ? "every output in this show already shows this composition"
                    : "";

    /// <summary>Points the picked output at the selected composition.</summary>
    public void AssignSelectedOutput()
    {
        if (SelectedComposition is not { } composition)
            return;

        var candidates = Assignable(composition.Id);

        if (AssignableIndex < 0 || AssignableIndex >= candidates.Count)
            return;

        Retarget(candidates[AssignableIndex], composition.Id,
            $"“{candidates[AssignableIndex].Name}” shows {composition.Name}");
    }

    /// <summary>Takes one output off whatever composition it shows. The output itself stays.</summary>
    public void UnassignOutput(Guid outputId)
    {
        if (_project.VideoOutputs.FirstOrDefault(output => output.Id == outputId) is not { } output)
            return;

        Retarget(output, null, $"“{output.Name}” shows nothing");
    }

    private void Retarget(VideoOutputDefinition output, Guid? compositionId, string label)
    {
        if (output.CompositionId == compositionId)
            return;

        _journal.Do(new SetValueCommand<Guid?>(
            output.Id, "composition", "video",
            () => output.CompositionId, id => output.CompositionId = id, compositionId, label));
        _journal.CloseGroup();

        AssignableIndex = 0;
        Refresh();
    }

    private void RaiseCompositionFields()
    {
        OnPropertyChanged(nameof(CompositionSize));
        OnPropertyChanged(nameof(CompositionRate));
        OnPropertyChanged(nameof(CompositionIdleImage));
        OnPropertyChanged(nameof(CompositionIdleFitIndex));
        OnPropertyChanged(nameof(CompositionHeader));
        OnPropertyChanged(nameof(CompositionName));
        OnPropertyChanged(nameof(HasComposition));
        OnPropertyChanged(nameof(SelectedCompositionFeeds));
        OnPropertyChanged(nameof(AssignableOutputs));
        OnPropertyChanged(nameof(CanAssignOutput));
        OnPropertyChanged(nameof(AssignHint));
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
        var all = screens.ToList();
        Screens = ["anywhere", .. all];
        OnPropertyChanged(nameof(OutputScreenIndex));

        // "2 · 1920×1080 · primary" carries the size in the middle. Offering THOSE first is the point:
        // on a laptop driving one projector the size you want is nearly always one this box can see.
        Resolutions =
        [
            .. all.Select(SizeIn).OfType<string>().Distinct(),
            .. CommonResolutions,
        ];

        OnPropertyChanged(nameof(Resolutions));
    }

    /// <summary>The "1920×1080" inside a screen label, or null when it carries none.</summary>
    private static string? SizeIn(string label)
    {
        foreach (var part in (label ?? "").Split('·', StringSplitOptions.TrimEntries))
        {
            var (width, height) = Dialogs.WindowSize(part);

            if (width > 0 && height > 0)
                return $"{width}×{height}";
        }

        return null;
    }

    /// <summary>
    /// The sizes worth offering beside a box that still takes anything typed.
    /// </summary>
    /// <remarks>
    /// A picker AND a text box, not one or the other. A dropdown alone teaches the operator that the
    /// listed sizes are the only ones, which is wrong the first time somebody hangs an LED wall of
    /// 1408×768; a bare text box makes them type "1920×1080" by hand every time, which is the common
    /// case and the one worth making free.
    /// </remarks>
    public IReadOnlyList<string> Resolutions { get; private set; } = CommonResolutions;

    private static readonly string[] CommonResolutions =
    [
        "1920×1080",
        "1280×720",
        "3840×2160",
        "2560×1440",
        "1920×1200",
        "1024×768",
        "1080×1920",
        "960×540",
    ];

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
    public IReadOnlyList<MappingSectionRow> Sections { get; private set; } = [];

    public IReadOnlyList<PlacementBox> MappingSource =>
        MappedOutput is null ? [] : VideoPresentation.MappingSource(MappedOutput, SelectedSection);

    public IReadOnlyList<PlacementBox> MappingTarget =>
        MappedOutput is null ? [] : VideoPresentation.MappingTarget(MappedOutput, SelectedSection);

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
                // A copied mesh must not share the original's list, or nudging one handle would move
                // the same handle on both sections — a record's `with` copies the reference.
                WarpOffsets = [.. section.WarpOffsets],
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

    /// <summary>
    /// Moves the selected section one place in DRAW order.
    /// </summary>
    /// <remarks>
    /// Draw order, not a tidy-up: sections are painted in list order, so where a section sits decides
    /// which of two overlapping panels is on top. On an edge blend that is the difference between a
    /// visible seam and none.
    /// </remarks>
    public void MoveSection(int delta)
    {
        if (MappedOutput is not { } output || Section is not { } section)
            return;

        var to = SelectedSection + delta;

        if (to < 0 || to >= output.Mapping.Count)
            return;

        using (_journal.Composite($"reorder “{section.Name}”", "mapping"))
        {
            _journal.Do(new RemoveItemCommand<MappingSection>(
                output.Mapping, section, "mapping", "lift section"));
            _journal.Do(new AddItemCommand<MappingSection>(
                output.Mapping, section, to, "mapping", "drop section"));
        }

        _journal.CloseGroup();
        SelectedSection = to;
        Refresh();
    }

    /// <summary>Whether this section is drawn. Not the same question as whether it exists.</summary>
    public void ToggleSection(Guid sectionId, bool enabled)
    {
        if (MappedOutput?.Mapping.FirstOrDefault(item => item.Id == sectionId) is not { } section
            || section.Enabled == enabled)
            return;

        _journal.Do(new SetValueCommand<bool>(
            section.Id, "enabled", "mapping",
            () => section.Enabled, flag => section.Enabled = flag, enabled,
            enabled ? $"show “{section.Name}”" : $"hide “{section.Name}”"));
        _journal.CloseGroup();
        Refresh();
    }

    public void RenameSection(Guid sectionId, string name)
    {
        var trimmed = (name ?? "").Trim();

        if (MappedOutput?.Mapping.FirstOrDefault(item => item.Id == sectionId) is not { } section
            || trimmed.Length == 0
            || section.Name == trimmed)
            return;

        _journal.Do(new SetValueCommand<string>(
            section.Id, "name", "mapping",
            () => section.Name, text => section.Name = text, trimmed,
            $"rename section to “{trimmed}”"));
        _journal.CloseGroup();
        Refresh();
    }

    // ── the splitter: the fastest correct way to build a wall ─────────────────────────────────

    [ObservableProperty]
    private int _splitColumns = 2;

    [ObservableProperty]
    private int _splitRows = 1;

    /// <summary>
    /// Replaces every section with an even grid, one per panel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason this exists rather than "add section" four times: a two-projector blend is made of
    /// numbers like 0.5 exactly, and nobody hits exactly half by dragging. Splitting first and nudging
    /// afterwards is how the geometry ends up right, and it is the one operation a mapping editor can
    /// do that saves an operator an evening.
    /// </para>
    /// <para>
    /// It REPLACES rather than appends, and says so on the button, because a splitter that added to
    /// what was there would double the panels of anybody who pressed it twice.
    /// </para>
    /// </remarks>
    public void SplitIntoGrid()
    {
        if (MappedOutput is not { } output)
            return;

        var columns = Math.Clamp(SplitColumns, 1, 64);
        var rows = Math.Clamp(SplitRows, 1, 64);

        using (_journal.Composite($"split into {columns}×{rows}", "mapping"))
        {
            foreach (var existing in output.Mapping.ToList())
                _journal.Do(new RemoveItemCommand<MappingSection>(
                    output.Mapping, existing, "mapping", "clear sections"));

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var section = new MappingSection
                    {
                        Name = rows == 1
                            ? $"Panel {column + 1}"
                            : $"Panel r{row + 1} c{column + 1}",
                        SourceX = (double)column / columns,
                        SourceY = (double)row / rows,
                        SourceWidth = 1d / columns,
                        SourceHeight = 1d / rows,
                        // Each panel fills its own output. A wall is N outputs each showing its slice
                        // whole, so the destination is the full frame and only the SOURCE is divided —
                        // dividing both would put a quarter-size picture in the corner of every screen.
                        TargetX = 0,
                        TargetY = 0,
                        TargetWidth = 1,
                        TargetHeight = 1,
                    };

                    _journal.Do(new AddItemCommand<MappingSection>(
                        output.Mapping, section, output.Mapping.Count, "mapping",
                        $"add “{section.Name}”"));
                }
            }
        }

        _journal.CloseGroup();
        SelectedSection = 0;
        Refresh();
    }

    /// <summary>Back to one section showing the whole canvas across the whole output.</summary>
    public void ResetToIdentity()
    {
        if (MappedOutput is not { } output)
            return;

        using (_journal.Composite("reset mapping", "mapping"))
        {
            foreach (var existing in output.Mapping.ToList())
                _journal.Do(new RemoveItemCommand<MappingSection>(
                    output.Mapping, existing, "mapping", "clear sections"));

            _journal.Do(new AddItemCommand<MappingSection>(
                output.Mapping, new MappingSection { Name = "Whole frame" }, 0, "mapping",
                "add whole-frame section"));
        }

        _journal.CloseGroup();
        SelectedSection = 0;
        Refresh();
    }

    // ── the section editor: register item 22's numeric route ───────────────────────────────────
    // The same eight numbers a drag moves, typed. A canvas alone cannot hit "exactly half", which is
    // the number a two-projector blend is made of. Source is in FRACTIONS of the canvas because it
    // has to survive the composition being resized; destination is in OUTPUT PIXELS because that is
    // the number written on the back of the projector.

    private MappingSection? Section =>
        MappedOutput is { } output && SelectedSection >= 0 && SelectedSection < output.Mapping.Count
            ? output.Mapping[SelectedSection]
            : null;

    public string SectionHeader =>
        Section is { } section ? $"Section {SelectedSection + 1} · {section.Name}" : "No section selected";

    public bool HasSection => Section is not null;

    /// <summary>The raster the destination boxes below are measured in.</summary>
    private (int Width, int Height) Raster
    {
        get
        {
            if (MappedOutput is not { } output)
                return (1920, 1080);

            var composition = output.CompositionId is { } id
                ? _project.Compositions.FirstOrDefault(item => item.Id == id)
                : null;

            return OutputMapping.Raster(
                output, composition?.Width ?? 1920, composition?.Height ?? 1080);
        }
    }

    public double RasterWidth => Raster.Width;
    public double RasterHeight => Raster.Height;

    public string RasterNote =>
        MappedOutput is not { } output
            ? ""
            : output is { MappingWidth: > 0, MappingHeight: > 0 }
                ? $"destination boxes are in pixels of {output.MappingWidth}×{output.MappingHeight}"
                : $"destination boxes are in pixels of the composition, {Raster.Width}×{Raster.Height} — "
                  + "set an output raster if this screen is a different size";

    public double SourceX
    {
        get => Section?.SourceX ?? 0;
        set => WriteRect(target: false, RectPart.X, value);
    }

    public double SourceY
    {
        get => Section?.SourceY ?? 0;
        set => WriteRect(target: false, RectPart.Y, value);
    }

    public double SourceWidth
    {
        get => Section?.SourceWidth ?? 1;
        set => WriteRect(target: false, RectPart.Width, value);
    }

    public double SourceHeight
    {
        get => Section?.SourceHeight ?? 1;
        set => WriteRect(target: false, RectPart.Height, value);
    }

    public double DestX
    {
        get => (Section?.TargetX ?? 0) * Raster.Width;
        set => WriteRect(target: true, RectPart.X, value / Raster.Width);
    }

    public double DestY
    {
        get => (Section?.TargetY ?? 0) * Raster.Height;
        set => WriteRect(target: true, RectPart.Y, value / Raster.Height);
    }

    public double DestWidth
    {
        get => (Section?.TargetWidth ?? 1) * Raster.Width;
        set => WriteRect(target: true, RectPart.Width, value / Raster.Width);
    }

    public double DestHeight
    {
        get => (Section?.TargetHeight ?? 1) * Raster.Height;
        set => WriteRect(target: true, RectPart.Height, value / Raster.Height);
    }

    public double RotationDegrees
    {
        get => Section?.RotationDegrees ?? 0;
        set => WriteNumber(value, "rotation", -360, 360,
            section => section.RotationDegrees, (section, number) => section.RotationDegrees = number);
    }

    public double Opacity
    {
        get => Section?.Opacity ?? 1;
        set => WriteNumber(value, "opacity", 0, 1,
            section => section.Opacity, (section, number) => section.Opacity = number);
    }

    public double Brightness
    {
        get => Section?.Brightness ?? 1;
        set => WriteNumber(value, "brightness", 0, 2,
            section => section.Brightness, (section, number) => section.Brightness = number);
    }

    // ── the warp mesh ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this section bends through a control-point grid.
    /// </summary>
    /// <remarks>
    /// A checkbox plus two counts, rather than the old off · 3×3 · 5×5 picker. Panels are not square:
    /// a three-projector blend across a flat cyc wants 5×2, and 5×5 gives the operator fifteen handles
    /// they have to leave alone and can knock by accident.
    /// </remarks>
    public bool MeshEnabled
    {
        get => Section is { MeshColumns: >= 2, MeshRows: >= 2 };
        set
        {
            if (Section is not { } section || value == MeshEnabled)
                return;

            // Seeded at whatever the two counts ALREADY SHOW, which for a section that has never been
            // warped is the default 3×3. Seeding at the minimum instead would tick the box and leave
            // the two fields beside it reading 3 over a mesh that was 2 — the first mesh anyone made
            // would be a size they did not choose.
            SetMesh(section, value ? MeshColumns : 0, value ? MeshRows : 0,
                value ? "warp on" : "warp off");
        }
    }

    public int MeshColumns
    {
        get => Section is { MeshColumns: >= 2 } section ? section.MeshColumns : 3;
        set
        {
            if (Section is not { } section || !MeshEnabled)
                return;

            SetMesh(section, Math.Clamp(value, 2, 16), section.MeshRows, $"warp {value} columns");
        }
    }

    public int MeshRows
    {
        get => Section is { MeshRows: >= 2 } section ? section.MeshRows : 3;
        set
        {
            if (Section is not { } section || !MeshEnabled)
                return;

            SetMesh(section, section.MeshColumns, Math.Clamp(value, 2, 16), $"warp {value} rows");
        }
    }

    /// <summary>Back to the flat identity grid, keeping the mesh's size.</summary>
    public void ResetMesh()
    {
        if (Section is { MeshColumns: >= 2, MeshRows: >= 2 } section)
            SetMesh(section, section.MeshColumns, section.MeshRows, "reset warp mesh", clear: true);
    }

    /// <summary>
    /// Resizes the mesh, carrying the offsets that still have a home.
    /// </summary>
    /// <remarks>
    /// Going from 3×3 to 5×3 used to throw the whole mesh away, which meant an operator who wanted one
    /// more column re-aligned every point they had already placed. Points that exist in both grids keep
    /// their offsets; the new ones start flat.
    /// </remarks>
    private void SetMesh(
        MappingSection section, int columns, int rows, string label, bool clear = false)
    {
        var offsets = new List<double>();

        if (columns >= 2 && rows >= 2)
        {
            var oldColumns = section.MeshColumns;
            var oldRows = section.MeshRows;
            var carry = !clear && section.HasMesh;

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var from = ((row * oldColumns) + column) * 2;
                    var keep = carry && column < oldColumns && row < oldRows
                               && from + 1 < section.WarpOffsets.Count;

                    offsets.Add(keep ? section.WarpOffsets[from] : 0);
                    offsets.Add(keep ? section.WarpOffsets[from + 1] : 0);
                }
            }
        }

        using (_journal.Composite(label, "mapping"))
        {
            _journal.Do(new SetValueCommand<int>(
                section.Id, "meshColumns", "mapping",
                () => section.MeshColumns, number => section.MeshColumns = number, columns, label));
            _journal.Do(new SetValueCommand<int>(
                section.Id, "meshRows", "mapping",
                () => section.MeshRows, number => section.MeshRows = number, rows, label));
            _journal.Do(new SetValueCommand<List<double>>(
                section.Id, "warpOffsets", "mapping",
                () => section.WarpOffsets, values => section.WarpOffsets = values, offsets,
                columns == 0 ? "clear warp mesh" : "size warp mesh"));
        }

        _journal.CloseGroup();
        SelectedWarpPoint = 0;
        Refresh();
    }

    public bool HasWarp => Section?.HasMesh == true;

    public IReadOnlyList<string> WarpPoints => Section is not { } section || !section.HasMesh
        ? []
        : [.. Enumerable.Range(0, section.MeshPointCount)
            .Select(index =>
                $"r{index / section.MeshColumns + 1} c{index % section.MeshColumns + 1}")];

    private int _selectedWarpPoint;

    public int SelectedWarpPoint
    {
        get => _selectedWarpPoint;
        set
        {
            var maximum = Math.Max(0, WarpPoints.Count - 1);
            if (!SetProperty(ref _selectedWarpPoint, Math.Clamp(value, 0, maximum)))
                return;
            OnPropertyChanged(nameof(WarpOffsetX));
            OnPropertyChanged(nameof(WarpOffsetY));
        }
    }

    public double WarpOffsetX
    {
        get => WarpOffset(0);
        set => WriteWarpOffset(0, value);
    }

    public double WarpOffsetY
    {
        get => WarpOffset(1);
        set => WriteWarpOffset(1, value);
    }

    private double WarpOffset(int axis)
    {
        if (Section is not { } section || !section.HasMesh)
            return 0;

        var at = SelectedWarpPoint * 2 + axis;
        return at < section.WarpOffsets.Count ? section.WarpOffsets[at] : 0;
    }

    private void WriteWarpOffset(int axis, double value)
    {
        if (Section is not { } section || !section.HasMesh)
            return;

        var at = SelectedWarpPoint * 2 + axis;
        var changed = section.WarpOffsets.ToList();

        if (at >= changed.Count || Math.Abs(changed[at] - value) < 0.000001)
            return;

        changed[at] = Math.Clamp(value, -1, 1);
        _journal.Do(new SetValueCommand<List<double>>(
            section.Id, $"warpPoint:{SelectedWarpPoint}:{axis}", "mapping",
            () => section.WarpOffsets, values => section.WarpOffsets = values,
            changed, "move warp point"));
        _journal.CloseGroup();
        Refresh();
    }

    /// <summary>Nudges the selected mesh handle by one half-percent of the destination rectangle.</summary>
    public void NudgeWarp(double dx, double dy)
    {
        if (Section is not { } section || !section.HasMesh)
            return;

        var changed = section.WarpOffsets.ToList();
        var at = SelectedWarpPoint * 2;
        changed[at] = Math.Clamp(changed[at] + dx, -1, 1);
        changed[at + 1] = Math.Clamp(changed[at + 1] + dy, -1, 1);
        _journal.Do(new SetValueCommand<List<double>>(
            section.Id, $"warpPoint:{SelectedWarpPoint}", "mapping",
            () => section.WarpOffsets, values => section.WarpOffsets = values,
            changed, "nudge warp point"));
        _journal.CloseGroup();
        Refresh();
    }

    private enum RectPart
    {
        X,
        Y,
        Width,
        Height,
    }

    private void WriteRect(bool target, RectPart part, double value)
    {
        if (Section is not { } section || !double.IsFinite(value))
            return;

        var rect = target
            ? new NormalizedRect(
                section.TargetX, section.TargetY, section.TargetWidth, section.TargetHeight)
            : new NormalizedRect(
                section.SourceX, section.SourceY, section.SourceWidth, section.SourceHeight);

        rect = part switch
        {
            RectPart.X => rect with { X = value },
            RectPart.Y => rect with { Y = value },
            RectPart.Width => rect with { Width = value },
            _ => rect with { Height = value },
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
        double value, string property, double minimum, double maximum,
        Func<MappingSection, double> read, Action<MappingSection, double> write)
    {
        if (Section is not { } section || !double.IsFinite(value))
            return;

        var clamped = Math.Clamp(value, minimum, maximum);

        if (Math.Abs(read(section) - clamped) < 0.000001)
            return;

        _journal.Do(new SetValueCommand<double>(
            section.Id, property, "mapping",
            () => read(section), number => write(section, number), clamped,
            $"set section {property}"));
        _journal.CloseGroup();
        Refresh();
    }


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
        // The SET first: a composition added, removed or renamed needs new panes, and one only edited
        // needs its existing pane left alone so a drag in progress keeps its container.
        var wanted = _project.Compositions.Select(item => item.Id).ToList();

        if (!wanted.SequenceEqual(Compositions.Select(pane => pane.Id))
            || Compositions.Any(Stale))
        {
            Compositions = Panes();
            OnPropertyChanged(nameof(Compositions));
            OnPropertyChanged(nameof(HasNoCompositions));
            OnPropertyChanged(nameof(OutputsEmptyDetail));
        }

        foreach (var pane in Compositions)
        {
            var composition = _project.Compositions.FirstOrDefault(item => item.Id == pane.Id);
            if (composition is null)
                continue;

            pane.Layers = VideoPresentation.Layers(_project, composition);
            pane.Feeds = Feeds(composition.Id);
            pane.IsSelected = SelectedComposition?.Id == pane.Id;
        }

        // Rows, not panes: nothing drags in this list, so it is rebuilt outright and the selection is
        // carried across by id.
        var selected = SelectedOutput?.Id;
        Outputs = VideoPresentation.Outputs(_project, _runtime);
        OnPropertyChanged(nameof(Outputs));
        OnPropertyChanged(nameof(HasNoOutputs));
        SelectedOutput = Outputs.FirstOrDefault(row => row.Id == selected) ?? Outputs.FirstOrDefault();

        // Changing outputs/sections can replace a 5×5 mesh with a 3×2 one. Keep the selected handle
        // inside the new mesh before a keyboard nudge indexes its offset pair.
        _selectedWarpPoint = Math.Clamp(
            _selectedWarpPoint, 0, Math.Max(0, (Section?.MeshPointCount ?? 0) - 1));

        RebuildSections();
        CountTabs();

        OnPropertyChanged(nameof(MappingSource));
        OnPropertyChanged(nameof(MappingTarget));
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(SectionHeader));
        OnPropertyChanged(nameof(RasterWidth));
        OnPropertyChanged(nameof(RasterHeight));
        OnPropertyChanged(nameof(RasterNote));
        RaiseSectionFields();
        RaiseCompositionFields();
    }

    /// <summary>
    /// Re-reads the section list, replacing the ROWS only when the set or the order changed.
    /// </summary>
    /// <remarks>
    /// The same rule the composition panes follow, for a sharper reason: two of these rows' fields are
    /// edited in place, and a rename goes through the journal — which refreshes the whole view. Rebuilt
    /// unconditionally, the text box the operator was typing into would be replaced after every
    /// keystroke, taking the caret with it, so a section could only ever be renamed one letter at a
    /// time. A section that merely CHANGED is mutated where it stands.
    /// </remarks>
    private void RebuildSections()
    {
        var mapping = MappedOutput?.Mapping ?? [];

        if (!mapping.Select(section => section.Id).SequenceEqual(Sections.Select(row => row.Id)))
        {
            Sections =
            [
                .. mapping.Select((section, index) => new MappingSectionRow(
                    section.Id,
                    index,
                    Label(section, index),
                    section.Enabled,
                    Warp(section),
                    ToggleSection,
                    RenameSection)),
            ];

            OnPropertyChanged(nameof(Sections));
            return;
        }

        for (var index = 0; index < Sections.Count; index++)
            Sections[index].Adopt(Label(mapping[index], index), mapping[index].Enabled, Warp(mapping[index]));
    }

    private static string Label(MappingSection section, int index) =>
        section.Name.Length == 0 ? $"Section {index + 1}" : section.Name;

    private static string Warp(MappingSection section) =>
        section.HasMesh ? $"warp {section.MeshColumns}×{section.MeshRows}" : "";

    /// <summary>Whether a pane's header no longer matches the composition it is showing.</summary>
    private bool Stale(CompositionPaneViewModel pane) =>
        _project.Compositions.FirstOrDefault(item => item.Id == pane.Id) is { } composition
        && (composition.Name != pane.Name
            || !pane.Hint.StartsWith(
                $"{composition.Width}×{composition.Height} · {composition.FramesPerSecond:0.##}",
                StringComparison.Ordinal));

    private void RaiseSectionFields()
    {
        OnPropertyChanged(nameof(SourceX));
        OnPropertyChanged(nameof(SourceY));
        OnPropertyChanged(nameof(SourceWidth));
        OnPropertyChanged(nameof(SourceHeight));
        OnPropertyChanged(nameof(DestX));
        OnPropertyChanged(nameof(DestY));
        OnPropertyChanged(nameof(DestWidth));
        OnPropertyChanged(nameof(DestHeight));
        OnPropertyChanged(nameof(RotationDegrees));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(MeshEnabled));
        OnPropertyChanged(nameof(MeshColumns));
        OnPropertyChanged(nameof(MeshRows));
        OnPropertyChanged(nameof(HasWarp));
        OnPropertyChanged(nameof(WarpPoints));
        OnPropertyChanged(nameof(SelectedWarpPoint));
        OnPropertyChanged(nameof(WarpOffsetX));
        OnPropertyChanged(nameof(WarpOffsetY));
    }
}

/// <summary>
/// One mapping section as the list shows it: drawn or not, named, and where it sits in draw order.
/// </summary>
/// <remarks>
/// Observable and callback-carrying rather than a plain record, because two of its fields are EDITED
/// in place — the enable checkbox and the inline name — and a row that could only be read would send
/// the operator to a second pane to rename the thing they are looking at. The callbacks go back to the
/// view-model so the journal stays in one place; the row itself knows nothing about undo.
/// </remarks>
public sealed partial class MappingSectionRow : ObservableObject
{
    private readonly Action<Guid, bool> _toggle;
    private readonly Action<Guid, string> _rename;
    private bool _quiet;

    public MappingSectionRow(
        Guid id,
        int index,
        string name,
        bool enabled,
        string warp,
        Action<Guid, bool> toggle,
        Action<Guid, string> rename)
    {
        Id = id;
        Index = index;
        _toggle = toggle;
        _rename = rename;

        // Set behind the notification guard: assigning through the generated setters would fire the
        // journal callbacks while the row is still being built, so simply LISTING the sections would
        // write an undo step per section.
        Adopt(name, enabled, warp);
    }

    public Guid Id { get; }
    public int Index { get; }

    public bool HasWarp => Warp.Length > 0;

    /// <summary>The number the canvases label this section with, so the two lists agree.</summary>
    public string Position => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarp))]
    private string _warp = "";

    /// <summary>
    /// Takes the document's current values without calling back into the journal.
    /// </summary>
    /// <remarks>
    /// How a row is updated in place. Assigning through the generated setters would journal a rename
    /// for every refresh — an edit anywhere in the show would write "rename section" into the undo
    /// stack for every section on the selected output.
    /// </remarks>
    public void Adopt(string name, bool enabled, string warp)
    {
        _quiet = true;
        Name = name;
        Enabled = enabled;
        Warp = warp;
        _quiet = false;
    }

    partial void OnNameChanged(string value)
    {
        if (!_quiet)
            _rename(Id, value);
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_quiet)
            _toggle(Id, value);
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

    /// <summary>
    /// The outputs this canvas is sent to, named on the canvas itself.
    /// </summary>
    /// <remarks>
    /// On the pane rather than only in the inspector because "where does this go" is the question an
    /// operator asks while LOOKING at the canvas — a composition feeding nothing looks identical to one
    /// feeding three projectors, and that is the single most expensive thing to discover at a get-in.
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<CompositionFeedRow> _feeds = [];

    [ObservableProperty]
    private bool _isSelected;

    public bool HasNoFeeds => Feeds.Count == 0;

    partial void OnFeedsChanged(IReadOnlyList<CompositionFeedRow> value) =>
        OnPropertyChanged(nameof(HasNoFeeds));
}

/// <summary>One output a composition is sent to, as the composition pane lists it.</summary>
/// <param name="State">"live" or "not open" — a machine fact, so it can differ per booth.</param>
public sealed record CompositionFeedRow(Guid OutputId, string Name, string State, bool IsLive);
