using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Timeline;
using HaCue2.Core.Validation;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;
using S.Media.Core.Diagnostics;
using S.Media.Session;
using S.Media.Source.YouTube;

namespace HaCue2.ViewModels;

/// <summary>Screen 01 - recents, inline recovery, and the cheap machine checks.</summary>
/// <remarks>
/// The import door is gone (register item 29): HaCue2 is a clean start and a <c>.haplayproj</c>
/// converter is a separate companion tool with no priority, so the launcher stops promising one.
/// </remarks>
public partial class LauncherViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly MachineFacts _machine;
    private IReadOnlyList<RecoveryCandidate> _recoveries = [];

    public LauncherViewModel() : this(new AppSettings(), MachineFacts.Nothing)
    {
    }

    public LauncherViewModel(AppSettings settings, MachineFacts machine)
    {
        _settings = settings;
        _machine = machine;

        Recents = Rows(settings);
        _recoveries = RecoveryStore.Scan();
        MachineChecks = Checks(machine);
    }

    /// <summary>The application's real settings object, shared with the launcher settings window.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Raised with a project the launcher loaded or created, and where it came from.</summary>
    public event Action<HaCueProject, string>? ProjectOpened;

    /// <summary>What went wrong with the last open, for the launcher's own line.</summary>
    [ObservableProperty]
    private string _openFailure = "";

    /// <summary>Hands a loaded project to the shell.</summary>
    public void Adopt(HaCueProject project, string path) => ProjectOpened?.Invoke(project, path);

    /// <summary>
    /// The prompt behind "New project…".
    /// </summary>
    /// <remarks>
    /// It asks WHAT, and the shell asks WHERE the moment this closes. Split that way because a file
    /// picker is a platform window the launcher cannot host inside a field, and because the name typed
    /// here is what the picker suggests as the filename.
    /// </remarks>
    public PromptViewModel NewProject() =>
        new(
            "New project",
            "seeded with a Main L/R pair and one cue list · you will be asked where to save it next",
            [
                new PromptField { Label = "Name", Value = "Untitled show" },
                new PromptField
                {
                    Label = "Media root",
                    Kind = PromptFieldKind.Folder,
                    Value = "",
                    Hint = "where this show's media lives · relinking searches under it",
                },
            ],
            prompt => Adopt(
                ProjectFiles.Create(
                    prompt["Name"].Value.Trim(),
                    prompt["Media root"].Value.Trim(),
                    _settings),
                ""),
            confirm: "CREATE");

    /// <summary>What the operator has opened before, newest first, straight from app-settings.json.</summary>
    public IReadOnlyList<RecentProjectRow> Recents { get; private set; }

    public bool HasRecents => Recents.Count > 0;

    /// <summary>What this box has, checked before any project is chosen.</summary>
    public IReadOnlyList<LogLine> MachineChecks { get; }

    /// <summary>Whether an autosave newer than its project file was found.</summary>
    public bool HasRecovery => _recoveries.Count > 0;

    public string RecoveryNotice => _recoveries.Count switch
    {
        0 => "",
        1 => _recoveries[0].Notice,
        var many => $"{_recoveries[0].Notice} · and {many - 1} more",
    };

    [ObservableProperty]
    private RecentProjectRow? _selectedRecent;

    public string SeedNote { get; } =
        "New projects are seeded from your defaults (Settings · Application · New project defaults): "
        + "a Main L/R logical pair patched to the machine's default device.";

    /// <summary>
    /// Opens a recent by loading its file.
    /// </summary>
    /// <remarks>
    /// It used to raise an event the app answered by opening the SAMPLE show, whichever row was
    /// clicked - the recents list looked real and did the same thing every time. Now the row's path is
    /// what gets loaded, and a file that has since gone is reported rather than opened.
    /// </remarks>
    public async Task OpenAsync(RecentProjectRow? row)
    {
        row ??= Recents.FirstOrDefault();

        if (row is null)
            return;

        if (row.IsMissing)
        {
            OpenFailure = $"{Path.GetFileName(row.Path)} is no longer at that path.";
            return;
        }

        var (project, result) = await ProjectFiles.OpenAsync(row.Path).ConfigureAwait(true);

        if (project is null)
        {
            OpenFailure = result.Message;
            return;
        }

        Adopt(project, result.Path);
    }

    /// <summary>
    /// Opens the autosave instead of the file - the RECOVER answer.
    /// </summary>
    /// <remarks>
    /// The recovered document is adopted under its ORIGINAL path, so the next save writes where the
    /// operator expects. It arrives dirty by construction: it differs from the file on disk, which is
    /// the entire reason it was offered.
    /// </remarks>
    public async Task RecoverAsync()
    {
        if (_recoveries.FirstOrDefault() is not { } candidate)
            return;

        var (project, result) = await ProjectFiles.OpenAsync(candidate.CopyPath).ConfigureAwait(true);

        if (project is null)
        {
            OpenFailure = result.Message;
            return;
        }

        // The copy has served its purpose. Leaving it would offer the same recovery again at the next
        // launch, after the operator has already answered.
        RecoveryStore.Discard(candidate);
        Adopt(project, candidate.OriginalPath);
    }

    /// <summary>Throws the autosave away - the operator has decided the file on disk is the truth.</summary>
    public void DiscardRecovery()
    {
        foreach (var candidate in _recoveries)
            RecoveryStore.Discard(candidate);

        _recoveries = [];
        OnPropertyChanged(nameof(HasRecovery));
        OnPropertyChanged(nameof(RecoveryNotice));
    }

    /// <summary>The recents list as rows, with a cheap existence check per entry.</summary>
    private static IReadOnlyList<RecentProjectRow> Rows(AppSettings settings) =>
    [
        .. settings.Recents.Select((recent, index) => new RecentProjectRow
        {
            Name = recent.Title.Length > 0 ? recent.Title : Path.GetFileNameWithoutExtension(recent.Path),
            Path = recent.Path,
            Contents = recent.Summary.Length > 0 ? recent.Summary : "-",
            Opened = Ago(recent.LastOpened),
            // File.Exists, not an open: the launcher must not stall on a disconnected volume, and
            // "gone" is the only answer that changes what the row can do.
            IsMissing = !File.Exists(recent.Path),
            IsCurrent = index == 0,
        }),
    ];

    /// <summary>"today 14:02", "yesterday", "Jul 28" - the resolution that is actually useful.</summary>
    private static string Ago(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        if (local.Date == today)
            return $"today {local:HH:mm}";

        return local.Date == today.AddDays(-1) ? "yesterday" : local.ToString("MMM d");
    }

    /// <summary>
    /// The cheap machine checks, before any project is chosen.
    /// </summary>
    /// <remarks>
    /// Only audio can be answered today, and the rest say so rather than reporting a plausible number.
    /// "not checked" is a different answer from "none found", and a launcher that claimed to have
    /// verified NDI would be believed.
    /// </remarks>
    private static IReadOnlyList<LogLine> Checks(MachineFacts machine) =>
    [
        new("", "", "audio", machine.DevicesEnumerated
            ? $"{machine.OutputDeviceNames.Count} output device(s)"
            : "not checked - no backend"),
        new("", "", "video", "not checked yet"),
        new("", "", "ndi", "not checked yet"),
        new("", "", "midi", "not checked yet"),
    ];
}

/// <summary>
/// Screen 04's curve editor - the "custom" option behind every curve picker in the app.
/// </summary>
/// <remarks>
/// Custom curves save as PROJECT presets ("slow tail"), so the preset row itself is the library and
/// there is no separate management UI. The dB ⇄ linear toggle is not decoration: which reading is
/// useful depends on the fade, and forcing one makes half of them unreadable.
/// </remarks>
public partial class CurveEditorViewModel : ObservableObject
{
    private readonly ProjectJournal? _journal;
    private readonly ICurveTarget? _target;
    private readonly TimeSpan? _duration;
    private IDisposable? _drag;
    private Guid? _managedPresetId;

    /// <summary>The dummy editor, for a preview with no document behind it.</summary>
    public CurveEditorViewModel()
    {
        Title = "Fade curve";
        Hint = "same control everywhere a curve is picked";
        _knots = [new CurveKnot(0, 1), new CurveKnot(0.35, 0.55), new CurveKnot(1, 0)];
    }

    /// <param name="crossfade">
    /// True when this ONE curve drives two ramps at once, so the editor draws both. See
    /// <see cref="Companion"/>.
    /// </param>
    public CurveEditorViewModel(
        ProjectJournal journal,
        ICurveTarget target,
        string title,
        TimeSpan? duration = null,
        bool crossfade = false)
    {
        _journal = journal;
        _target = target;
        _crossfade = crossfade;
        Title = title;
        Hint = duration is { TotalMilliseconds: > 0 }
            ? $"{ClipTimes.Format((int)duration.Value.TotalMilliseconds)} · double-click to add a timed point"
            : "double-click to add a point · exact percentages are editable beside the canvas";
        _knots = target.Read();
        _supportsHold = target.SupportsHold;
        _duration = duration is { TotalMilliseconds: > 0 } ? duration : null;
        _managedPresetId = target.PresetId ?? journal.Project.CurvePresets.FirstOrDefault()?.Id;
        _managedPresetName = journal.Project.CurvePresets
            .FirstOrDefault(preset => preset.Id == _managedPresetId)?.Name ?? "";
    }

    public string Title { get; }
    public string Hint { get; }

    private IReadOnlyList<CurveKnot> _knots;
    private readonly bool _supportsHold = true;
    private readonly bool _crossfade;

    public IReadOnlyList<CurveOption> Curves => _journal is { } journal
        ? CurveLibrary.Choices(journal.Project)
        : CurveLibrary.Curves;

    /// <summary>The index the picker's last entry sits at - "custom ✎".</summary>
    private int CustomIndex => Curves.Count - 1;

    /// <summary>
    /// Which curve the picker is showing, and picking one is what CHOOSES it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn points beat the named law - <c>CurveSpec.Resolve</c> follows preset → points → law - so a
    /// target with anything drawn on it reads as "custom" whatever its law says, and picking a named
    /// entry has to drop those points. Both halves travel as ONE undo step, or an undo would leave the
    /// law of one curve under the shape of another.
    /// </para>
    /// <para>
    /// A preset and an automation lane have no law at all: they ARE the drawn shape. The picker parks
    /// on "custom" for them and <see cref="CanPickLaw"/> turns it off, rather than offering four
    /// choices that would do nothing.
    /// </para>
    /// </remarks>
    public int SelectedCurve
    {
        get
        {
            if (_target is not { } target)
                return CustomIndex;

            if (target.PresetId is { } presetId)
                return IndexOf(option => option.PresetId == presetId);

            if (target.HasStored || target.Law is not { } law)
                return CustomIndex;

            return IndexOf(option => option.Law == law);
        }
        set
        {
            if (_target is null || _journal is null || value < 0 || value >= Curves.Count)
                return;

            var option = Curves[value];
            IProjectCommand? command = option switch
            {
                { Law: { } law } => CurveEdits.PickLaw(_target, law),
                { PresetId: { } presetId } => CurveEdits.PickPreset(_target, presetId, option.Name),
                { IsCustom: true } when !_target.HasStored || _target.PresetId is not null =>
                    new SetCurveCommand(_target, _target.Read(), "draw a custom curve"),
                _ => null,
            };

            if (command is null)
                return;

            _journal.Do(command);
            _journal.CloseGroup();
            SelectedIndex = -1;
            Reload();
        }
    }

    /// <summary>Whether there is a named law to pick. False for a preset and for an automation lane.</summary>
    public bool CanPickLaw => _target?.Law is not null;

    /// <summary>
    /// The handles, in CANVAS space: y is flipped, because a level of 1 is drawn at the top.
    /// </summary>
    /// <remarks>
    /// Flipped here rather than in the control, which knows nothing about levels - the same split the
    /// timeline's effect lanes already make.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Points =>
    [
        .. _knots.Select((knot, index) => new CurvePoint(
            knot.X, 1 - knot.Y, _selection.Contains(index), knot.Hold)),
    ];

    public IReadOnlyList<CurveTangent> Tangents => CurveLibrary.Tangents(_knots, _selection);

    /// <summary>
    /// Every selected keyframe. <see cref="SelectedIndex"/> is the PRIMARY one within it - the numeric
    /// fields and the segment picker edit a single keyframe, so they need one to name.
    /// </summary>
    /// <remarks>
    /// The canvas raises Ctrl/Shift-click and Ctrl+A in both hosts. Until this existed only the inline
    /// timeline lane acted on them, so the same control silently did less in the window that presents
    /// itself as the fuller editor.
    /// </remarks>
    private readonly HashSet<int> _selection = [];

    private int _anchor = -1;

    public int SelectionCount => _selection.Count;

    public bool HasMultipleSelected => _selection.Count > 1;

    /// <summary>
    /// The polyline. A held point gets a corner the point list does not contain.
    /// </summary>
    /// <remarks>
    /// Without the extra corner a hold would be drawn as the ramp it explicitly is not - the picture
    /// would show the operator a fade that the engine will not play.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Shape
        => CurveLibrary.Shape(_knots);

    /// <summary>
    /// The OTHER half of a crossfade, drawn behind the one being edited.
    /// </summary>
    /// <remarks>
    /// A crossfade is one curve and two ramps: the cue going out rides the drawing forwards, the cue
    /// coming in reads the same drawing from its far end. Only one of them was ever on screen, so an
    /// operator shaping a crossfade could see the departure and had to imagine the arrival - which is
    /// the half that decides whether the join has a hole in it or a bump. Empty for every other curve,
    /// which genuinely has one side.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Companion =>
        _crossfade ? CurveLibrary.Mirror(Shape) : [];

    public bool IsCrossfade => _crossfade;

    /// <summary>Which line is which, because two curves in one box need saying.</summary>
    public string CompanionNote =>
        _crossfade ? "the cue going OUT rides the curve · the cue coming IN reads it backwards" : "";

    public IReadOnlyList<string> Scales { get; } = ["dB", "linear"];
    public IReadOnlyList<string> Segments => _supportsHold
        ? ["linear", "equal-power", "exponential", "s-curve", "bezier", "hold"]
        : ["linear", "equal-power", "exponential", "s-curve", "bezier"];

    [ObservableProperty] private string _scale = "dB";
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private string _presetStatus = "";
    [ObservableProperty] private string _managedPresetName = "";

    /// <summary>
    /// The preset library with a use count each.
    /// </summary>
    /// <remarks>
    /// The whole-project walk is hoisted OUT of the per-preset projection and its counts tallied in
    /// one pass: it used to run once per preset, and this list is read again by
    /// <see cref="SelectedPresetIndex"/> and <see cref="HasManagedPreset"/>, so a library of ten
    /// presets re-walked every cue in the show thirty times per bind.
    /// </remarks>
    public IReadOnlyList<CurvePresetRow> Presets
    {
        get
        {
            if (_journal is null)
                return [];

            var uses = new Dictionary<Guid, int>();
            foreach (var binding in CurveBindings(_journal.Project))
                if (binding.Spec.PresetId is { } id)
                    uses[id] = uses.GetValueOrDefault(id) + 1;

            return
            [
                .. _journal.Project.CurvePresets.Select(preset => new CurvePresetRow(
                    preset.Id, preset.Name, uses.GetValueOrDefault(preset.Id))),
            ];
        }
    }

    public int SelectedPresetIndex
    {
        get
        {
            var presets = Presets;
            for (var index = 0; index < presets.Count; index++)
                if (presets[index].Id == _managedPresetId)
                    return index;
            return -1;
        }
        set
        {
            var presets = Presets;
            if (value < 0 || value >= presets.Count || presets[value].Id == _managedPresetId)
                return;

            var picked = presets[value];
            _managedPresetId = picked.Id;
            ManagedPresetName = picked.Name;
            PresetStatus = $"{picked.Name} · {picked.ReferenceLabel}";
            OnPropertyChanged(nameof(SelectedPresetIndex));
            OnPropertyChanged(nameof(HasManagedPreset));
        }
    }

    public bool HasManagedPreset => SelectedPresetIndex >= 0;

    public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < _knots.Count;

    public bool SupportsHold => _supportsHold;

    /// <summary>The selected point's position, as the numeric route onto the same edit.</summary>
    public string SelectedPoint => HasSelection
        ? $"{PointTime} · {PointValue}"
        : "-";

    /// <summary>The selected keyframe's exact time. With a known cue/fade duration this is a clock
    /// value; otherwise it is a normalized percentage.</summary>
    public string PointTime
    {
        get
        {
            if (!HasSelection)
                return "-";
            return _duration is { } duration
                ? ClipTimes.Format((int)Math.Round(_knots[SelectedIndex].X * duration.TotalMilliseconds))
                : $"{_knots[SelectedIndex].X * 100:0.###} %";
        }
        set
        {
            if (!HasSelection || ParseTime(value) is not { } x)
                return;
            MoveSelected(x, _knots[SelectedIndex].Y);
        }
    }

    /// <summary>The selected value in the chosen scale. dB is useful for volume fades; linear is the
    /// direct normalized value used by opacity and outbound lanes.</summary>
    public string PointValue
    {
        get
        {
            if (!HasSelection)
                return "-";
            var level = _knots[SelectedIndex].Y;
            if (Scale == "linear")
                return level.ToString("0.###", CultureInfo.InvariantCulture);
            if (level <= 0.001)
                return "−60 dB";
            return $"{20 * Math.Log10(level):0.##} dB".Replace('-', '−');
        }
        set
        {
            if (!HasSelection || ParseValue(value) is not { } y)
                return;
            MoveSelected(_knots[SelectedIndex].X, y);
        }
    }

    /// <summary>Whether the selected point holds. Bound to the smooth/hold segment picker.</summary>
    public string Segment
    {
        get => !HasSelection
            ? "linear"
            : _knots[SelectedIndex].Hold
                ? "hold"
                : HasBezier(SelectedIndex)
                    ? "bezier"
                : SegmentName(_knots[SelectedIndex].CurveToNext);
        set
        {
            if (_target is null || _journal is null || !HasSelection)
                return;

            var command = value switch
            {
                "hold" => CurveEdits.SetHold(_target, SelectedIndex, hold: true),
                "bezier" => CurveEdits.SetBezier(_target, SelectedIndex),
                _ => CurveEdits.SetSegment(_target, SelectedIndex, SegmentCurve(value)),
            };

            if (command is null)
            {
                // The picker has already moved itself to what was clicked. Nothing was written - most
                // often "bezier" on the LAST knot, which has no following segment to shape - so the
                // list has to be told to go back, or it displays a law the document does not have.
                OnPropertyChanged(nameof(Segment));
                return;
            }

            _journal.Do(command);
            _journal.CloseGroup();
            Reload();
        }
    }

    public string EditHint =>
        "double-click adds · drag points/tangents · drag off removes · right-click toggles hold";

    /// <summary>Saves the current drawing into the project and immediately uses it on this cue. This
    /// is the action the old “Save as” textbox promised but never performed.</summary>
    public void SavePreset()
    {
        if (_journal is null || _target is null)
            return;

        var name = PresetName.Trim();
        if (name.Length == 0)
        {
            PresetStatus = "name the preset first";
            return;
        }

        var points = _target.Read()
            .Select(knot => new FadeCurvePoint(
                knot.X, knot.Y, knot.Hold, knot.CurveToNext,
                knot.OutHandleX, knot.OutHandleY, knot.InHandleX, knot.InHandleY))
            .ToList();
        var existing = _journal.Project.CurvePresets.FirstOrDefault(
            preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        var selected = false;

        using (_journal.Composite(existing is null ? "save curve preset" : "update curve preset", "cues"))
        {
            if (existing is null)
            {
                existing = new CurvePreset { Name = name, Points = points };
                _journal.Do(new AddItemCommand<CurvePreset>(
                    _journal.Project.CurvePresets,
                    existing,
                    _journal.Project.CurvePresets.Count,
                    "cues",
                    $"save curve preset {name}"));
            }
            else
            {
                var preset = existing;
                _journal.Do(new SetValueCommand<List<FadeCurvePoint>>(
                    preset.Id,
                    "curvePresetPoints",
                    "cues",
                    () => preset.Points,
                    value => preset.Points = value,
                    points,
                    $"update curve preset {name}"));
            }

            if (_target.Law is not null
                && CurveEdits.PickPreset(_target, existing.Id, existing.Name) is { } pick)
            {
                _journal.Do(pick);
                selected = true;
            }
        }

        // Only a fade spec can point AT a preset; an automation lane stores its own points, so saving
        // from one is a copy into the library and nothing more. Saying "and selected" there described
        // a link that was never made.
        PresetStatus = selected
            ? $"{existing.Name} saved and selected"
            : $"{existing.Name} saved to the library";
        _managedPresetId = existing.Id;
        ManagedPresetName = existing.Name;
        OnPropertyChanged(nameof(Curves));
        RefreshPresetLibrary();
        Reload();
    }

    /// <summary>Renames the selected preset without touching references: consumers bind by stable id.</summary>
    public void RenamePreset()
    {
        if (_journal is null || _managedPresetId is not { } id
            || _journal.Project.CurvePresets.FirstOrDefault(preset => preset.Id == id) is not { } preset)
            return;

        var name = ManagedPresetName.Trim();
        if (name.Length == 0)
        {
            PresetStatus = "enter a preset name";
            return;
        }
        if (_journal.Project.CurvePresets.Any(candidate => candidate.Id != id
                && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            PresetStatus = "that preset name is already in use";
            return;
        }
        if (preset.Name == name)
            return;

        _journal.Do(new SetValueCommand<string>(
            preset.Id, "curvePresetName", "cues",
            () => preset.Name, value => preset.Name = value, name,
            $"rename curve preset to {name}"));
        _journal.CloseGroup();
        PresetStatus = $"renamed to {name}";
        OnPropertyChanged(nameof(Curves));
        RefreshPresetLibrary();
    }

    /// <summary>Deletes a preset without changing playback. Every reference is first converted to an
    /// inline copy of the preset, and the conversion plus removal is one undoable operation.</summary>
    public void DeletePreset()
    {
        if (_journal is null || _managedPresetId is not { } id
            || _journal.Project.CurvePresets.FirstOrDefault(preset => preset.Id == id) is not { } preset)
            return;

        var points = preset.Points.ToList();
        var references = CurveBindings(_journal.Project)
            .Where(binding => binding.Spec.PresetId == id)
            .ToList();
        using (_journal.Composite($"delete curve preset {preset.Name}", "cues"))
        {
            foreach (var binding in references)
            {
                var spec = binding.Spec;
                _journal.Do(new SetValueCommand<List<FadeCurvePoint>?>(
                    binding.Subject, "curvePoints", "cues",
                    () => spec.Points, value => spec.Points = value, points.ToList(),
                    "preserve preset as an inline curve"));
                _journal.Do(new SetValueCommand<Guid?>(
                    binding.Subject, "curvePreset", "cues",
                    () => spec.PresetId, value => spec.PresetId = value, null,
                    "detach deleted curve preset"));
            }
            _journal.Do(new RemoveItemCommand<CurvePreset>(
                _journal.Project.CurvePresets, preset, "cues", $"delete curve preset {preset.Name}"));
        }

        _managedPresetId = _journal.Project.CurvePresets.FirstOrDefault()?.Id;
        ManagedPresetName = _journal.Project.CurvePresets.FirstOrDefault()?.Name ?? "";
        PresetStatus = references.Count == 0
            ? $"{preset.Name} deleted"
            : $"{preset.Name} deleted · {references.Count} use(s) preserved inline";
        OnPropertyChanged(nameof(Curves));
        RefreshPresetLibrary();
        Reload();
    }

    /// <summary>Imports valid curve presets from another HaCue2 project. IDs are rotated and names are
    /// uniquified so importing the same library twice never retargets existing cues.</summary>
    public void ImportPresets(HaCueProject source)
    {
        if (_journal is null)
            return;

        var imported = new List<CurvePreset>();
        using (_journal.Composite("import curve presets", "cues"))
        {
            foreach (var sourcePreset in source.CurvePresets.Where(CanImport))
            {
                var name = UniquePresetName(sourcePreset.Name);
                var copy = new CurvePreset
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Points = sourcePreset.Points.ToList(),
                };
                _journal.Do(new AddItemCommand<CurvePreset>(
                    _journal.Project.CurvePresets, copy, _journal.Project.CurvePresets.Count,
                    "cues", $"import curve preset {name}"));
                imported.Add(copy);
            }
        }

        if (imported.Count == 0)
        {
            PresetStatus = "that project has no valid curve presets";
            return;
        }
        _managedPresetId = imported[0].Id;
        ManagedPresetName = imported[0].Name;
        PresetStatus = $"imported {imported.Count} preset(s)";
        OnPropertyChanged(nameof(Curves));
        RefreshPresetLibrary();
    }

    public void ReportPresetError(string message) => PresetStatus = message;

    private void RefreshPresetLibrary()
    {
        OnPropertyChanged(nameof(Presets));
        OnPropertyChanged(nameof(SelectedPresetIndex));
        OnPropertyChanged(nameof(HasManagedPreset));
    }

    private string UniquePresetName(string proposed)
    {
        var root = string.IsNullOrWhiteSpace(proposed) ? "Imported curve" : proposed.Trim();
        var name = root;
        for (var suffix = 2; _journal!.Project.CurvePresets.Any(preset =>
                 string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{root} ({suffix})";
        return name;
    }

    private static bool CanImport(CurvePreset preset)
    {
        if (preset.Points.Count < 2 || preset.Points.Any(point =>
                !double.IsFinite(point.Progress) || !double.IsFinite(point.Level)
                || point.Progress is < 0 or > 1 || point.Level is < 0 or > 1))
            return false;
        try
        {
            _ = new CustomFadeCurve(preset.Points);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<(Guid Subject, CurveSpec Spec)> CurveBindings(HaCueProject project)
    {
        var bindings = new List<(Guid, CurveSpec)> { (Guid.Empty, project.Settings.StopFadeCurve) };
        foreach (var cue in project.AllCues())
            switch (cue)
            {
                case MediaCueNode media:
                    bindings.Add((cue.Id, media.FadeInCurve));
                    bindings.Add((cue.Id, media.FadeOutCurve));
                    break;
                case TextCueNode text:
                    bindings.Add((cue.Id, text.FadeInCurve));
                    bindings.Add((cue.Id, text.FadeOutCurve));
                    break;
                case GroupCueNode group:
                    bindings.Add((cue.Id, group.CrossfadeCurve));
                    break;
                case FadeCueNode fade:
                    bindings.Add((cue.Id, fade.Curve));
                    break;
                case PatchCueNode patch:
                    bindings.Add((cue.Id, patch.FadeCurve));
                    break;
            }
        return bindings;
    }

    /// <summary>A drag, a nudge, an add or a remove - every route ends in one command.</summary>
    public void Apply(CurveGesture gesture)
    {
        if (_target is null || _journal is null)
            return;

        if (gesture.Kind == CurveGestureKind.Select)
        {
            // A press on an ALREADY selected keyframe keeps the group, so a multi-selection can be
            // dragged. Anywhere else it becomes the selection, matching every other list in the app.
            if (!_selection.Contains(gesture.Index))
            {
                _selection.Clear();
                _selection.Add(gesture.Index);
            }
            _anchor = gesture.Index;
            SelectedIndex = gesture.Index;
            return;
        }
        if (gesture.Kind == CurveGestureKind.ToggleSelection)
        {
            if (!_selection.Add(gesture.Index))
                _selection.Remove(gesture.Index);
            _anchor = gesture.Index;
            SelectedIndex = _selection.Contains(gesture.Index)
                ? gesture.Index
                : _selection.Count > 0 ? _selection.Min() : -1;
            return;
        }
        if (gesture.Kind == CurveGestureKind.RangeSelection)
        {
            var anchor = _anchor < 0 ? gesture.Index : _anchor;
            _selection.Clear();
            for (var index = Math.Min(anchor, gesture.Index); index <= Math.Max(anchor, gesture.Index); index++)
                _selection.Add(index);
            SelectedIndex = gesture.Index;
            return;
        }
        if (gesture.Kind == CurveGestureKind.ClearSelection)
        {
            _selection.Clear();
            _anchor = -1;
            SelectedIndex = -1;
            return;
        }

        // The gesture arrives in canvas space; the document stores levels, so y flips back here.
        var x = gesture.X;
        var y = 1 - gesture.Y;

        var command = gesture.Kind switch
        {
            // A drag on one of several selected keyframes moves the whole group, spacing and tangents
            // preserved - the same edit the inline timeline lane performs.
            CurveGestureKind.Move when _selection.Count > 1 && _selection.Contains(gesture.Index) =>
                CurveEdits.MoveMany(_target, _selection, gesture.Index, x, y),
            CurveGestureKind.Move => CurveEdits.Move(_target, gesture.Index, x, y),
            CurveGestureKind.Add when !CurveEdits.HasPointNear(_target, x) => CurveEdits.Add(_target, x, y),
            CurveGestureKind.Remove => CurveEdits.Remove(_target, gesture.Index),
            CurveGestureKind.RemoveSelection when _selection.Count > 1 =>
                CurveEdits.RemoveMany(_target, _selection),
            CurveGestureKind.RemoveSelection when HasSelection => CurveEdits.Remove(_target, SelectedIndex),
            CurveGestureKind.MoveIncomingTangent =>
                CurveEdits.MoveTangent(_target, gesture.Index, incoming: true, x, y),
            CurveGestureKind.MoveOutgoingTangent =>
                CurveEdits.MoveTangent(_target, gesture.Index, incoming: false, x, y),
            _ => null,
        };

        if (command is null)
            return;

        _drag ??= _journal.Composite(command.Description, "cues");
        _journal.Do(command);

        if (gesture.Kind is CurveGestureKind.Remove or CurveGestureKind.RemoveSelection)
        {
            _selection.Clear();
            _anchor = -1;
            SelectedIndex = -1;
        }
        else if (gesture.Kind == CurveGestureKind.Add)
        {
            // Select what was just added, so the numeric fields describe it without a second click.
            var added = NearestIndex(x, y);
            _selection.Clear();
            _selection.Add(added);
            _anchor = added;
            SelectedIndex = added;
            // Only reached when the Add produced a command, i.e. it was not refused as too near a point.
            gesture.Accepted = true;
        }

        Reload();
    }

    /// <summary>Selects every keyframe - the canvas's Ctrl+A.</summary>
    public void SelectAll()
    {
        _selection.Clear();
        for (var index = 0; index < _knots.Count; index++)
            _selection.Add(index);
        _anchor = 0;
        SelectedIndex = _knots.Count > 0 ? 0 : -1;
        PresetStatus = $"selected {_selection.Count} keyframe(s)";
        Reload();
    }

    /// <summary>The selected keyframes as clipboard text, or null when nothing is selected.</summary>
    public string? Copy()
    {
        if (_selection.Count == 0)
        {
            PresetStatus = "select one or more keyframes to copy";
            return null;
        }

        PresetStatus = $"copied {_selection.Count} keyframe(s)";
        return LaneKeyframeClipboard.Encode(_knots, _selection);
    }

    /// <summary>
    /// Pastes keyframes after the primary selection, or at the start when nothing is selected.
    /// </summary>
    /// <remarks>
    /// The pasted span REPLACES what it lands on rather than piling on top of it: two keyframes at the
    /// same instant have no defined shape between them, and the operator asked for the copied shape to
    /// be here - not for it to be interleaved with whatever was.
    /// </remarks>
    public bool Paste(string? text)
    {
        if (_target is null || _journal is null
            || LaneKeyframeClipboard.DecodeKnots(text) is not { Count: > 0 } decoded)
        {
            PresetStatus = "clipboard has no HaCue2 keyframes";
            return false;
        }

        var span = decoded[^1].X - decoded[0].X;
        var at = Math.Clamp(HasSelection ? _knots[SelectedIndex].X : 0, 0, Math.Max(0, 1 - span));
        var placed = decoded
            .Select(knot => knot with
            {
                X = at + knot.X,
                OutHandleX = knot.OutHandleX is { } outX ? at + outX : null,
                InHandleX = knot.InHandleX is { } inX ? at + inX : null,
            })
            .ToList();

        var kept = _knots
            .Where(knot => knot.X < placed[0].X || knot.X > placed[^1].X)
            .ToList();
        _journal.Do(CurveEdits.Replace(
            _target, kept.Concat(placed), $"paste {placed.Count} keyframes"));
        _journal.CloseGroup();

        _knots = _target.Read();
        _selection.Clear();
        foreach (var knot in placed)
            _selection.Add(NearestIndex(knot.X, knot.Y));
        _anchor = _selection.Count > 0 ? _selection.Min() : -1;
        SelectedIndex = _anchor;
        PresetStatus = $"pasted {placed.Count} keyframe(s)";
        Reload();
        return true;
    }

    /// <summary>The stored knot closest to a point, for re-finding one an edit has just re-sorted.</summary>
    private int NearestIndex(double x, double y) =>
        _knots
            .Select((knot, index) => (Distance: Math.Abs(knot.X - x) + Math.Abs(knot.Y - y), index))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.index)
            .FirstOrDefault(-1);

    public void ToggleHold(int index)
    {
        // A right-click acts on the ONE keyframe under it, so it replaces any group selection rather
        // than joining it - otherwise hold would toggle on a point nowhere near the pointer.
        _selection.Clear();
        _anchor = index;
        SelectedIndex = index;
        Segment = HasSelection && _knots[index].Hold ? "smooth" : "hold";
    }

    /// <summary>Ends the gesture, closing its undo step.</summary>
    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    /// <summary>Re-reads the curve from the document - after an edit here, or an undo anywhere.</summary>
    public void Reload()
    {
        if (_target is not null)
            _knots = _target.Read();

        // An undo can shorten the curve under a selection made against the longer one. Indices that
        // no longer exist would draw handles off the end and delete the wrong keyframes.
        _selection.RemoveWhere(index => index < 0 || index >= _knots.Count);
        if (_anchor >= _knots.Count)
            _anchor = -1;

        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(Shape));
        OnPropertyChanged(nameof(Companion));
        OnPropertyChanged(nameof(Tangents));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Segment));
        OnPropertyChanged(nameof(PointTime));
        OnPropertyChanged(nameof(PointValue));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        // Drawing on the canvas moves the picker to "custom" on its own: the drawn points are now what
        // the engine will play, and a picker still highlighting "eq-power" would be describing a curve
        // the show no longer has.
        OnPropertyChanged(nameof(SelectedCurve));
    }

    partial void OnSelectedIndexChanged(int value)
    {
        // The primary is always IN the selection: the canvas routes plain clicks through Apply, but
        // ToggleHold and the tests set it directly, and a primary outside the group would edit a
        // keyframe the canvas is not drawing as selected.
        if (value >= 0 && _selection.Add(value))
            _anchor = value;

        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(Tangents));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Segment));
        OnPropertyChanged(nameof(PointTime));
        OnPropertyChanged(nameof(PointValue));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
    }

    partial void OnScaleChanged(string value)
    {
        OnPropertyChanged(nameof(PointValue));
        OnPropertyChanged(nameof(SelectedPoint));
    }

    private void MoveSelected(double x, double y)
    {
        if (_journal is null || _target is null || !HasSelection)
            return;

        _journal.Do(CurveEdits.Move(_target, SelectedIndex, x, y));
        _journal.CloseGroup();
        _knots = _target.Read();

        // Typing a time can move the keyframe PAST a neighbour, and the store is kept sorted - so the
        // index it had is now somebody else's. Follow the keyframe to where it landed, or the next
        // edit in these fields would silently be applied to the wrong one.
        _selection.Clear();
        var moved = NearestIndex(x, y);
        _anchor = moved;
        SelectedIndex = moved;
        Reload();
    }

    private double? ParseTime(string text)
    {
        var trimmed = text.Trim().Replace('−', '-');
        if (trimmed.EndsWith('%')
            && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return Math.Clamp(percent / 100, 0, 1);

        if (_duration is { TotalMilliseconds: > 0 } duration
            && ClipTimes.Parse(trimmed, duration) is { } milliseconds)
            return Math.Clamp(milliseconds / duration.TotalMilliseconds, 0, 1);

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
            ? Math.Clamp(fraction, 0, 1)
            : null;
    }

    private double? ParseValue(string text)
    {
        var trimmed = text.Trim().Replace('−', '-');
        if (trimmed.EndsWith('%')
            && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return Math.Clamp(percent / 100, 0, 1);

        var isDb = trimmed.Contains("db", StringComparison.OrdinalIgnoreCase) || Scale == "dB";
        trimmed = trimmed.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;

        return isDb
            ? Math.Clamp(Math.Pow(10, parsed / 20), 0, 1)
            : Math.Clamp(parsed, 0, 1);
    }

    private int IndexOf(Func<CurveOption, bool> predicate)
    {
        for (var index = 0; index < Curves.Count; index++)
            if (predicate(Curves[index]))
                return index;
        return CustomIndex;
    }

    private bool HasBezier(int index) =>
        index >= 0 && index + 1 < _knots.Count
        && _knots[index].OutHandleX is not null
        && _knots[index + 1].InHandleX is not null;

    private static string SegmentName(FadeCurve curve) => curve switch
    {
        FadeCurve.EqualPower => "equal-power",
        FadeCurve.Exponential => "exponential",
        FadeCurve.SCurve => "s-curve",
        _ => "linear",
    };

    private static FadeCurve SegmentCurve(string name) => name switch
    {
        "equal-power" => FadeCurve.EqualPower,
        "exponential" => FadeCurve.Exponential,
        "s-curve" => FadeCurve.SCurve,
        _ => FadeCurve.Linear,
    };
}

/// <summary>Screens 12 and 13 - application scope (not journaled) and project scope (journaled).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ProjectSettings _settings;
    private readonly HaCueProject _project;
    private readonly ProjectJournal? _journal;

    /// <summary>
    /// True while the constructor is seeding the fields from the document.
    /// </summary>
    /// <remarks>
    /// Without it every seeded value would be written straight back through the journal as an "edit",
    /// so a project would open with a dozen undo steps nobody made and a dirty flag on an untouched
    /// file.
    /// </remarks>
    private readonly bool _loading = true;

    private readonly AppSettings _app;
    private readonly Action? _applicationChanged;
    private readonly string _activeCacheRoot;

    public SettingsViewModel() : this(SampleProject.Create())
    {
    }

    public SettingsViewModel(
        HaCueProject project,
        ProjectJournal? journal = null,
        AppSettings? app = null,
        Action? applicationChanged = null)
    {
        _project = project;
        _settings = project.Settings;
        _journal = journal;
        _app = app ?? new AppSettings();
        _applicationChanged = applicationChanged;
        _activeCacheRoot = MediaCache.RootFor(_app);

        // The Remote API row says "project" only when this project actually overrides it. It used to
        // say so on every project, including ones with no override at all.
        ApplicationPanes =
        [
            new() { Name = "Appearance & layout" },
            new() { Name = "Transport defaults" },
            new() { Name = "Hotkeys" },
            new() { Name = "New project defaults" },
            new()
            {
                Name = "Remote API",
                Tally = _settings.RemoteApi is null ? "" : "project",
                TallyGel = Gel.Amber,
            },
            new() { Name = "Media cache" },
            new() { Name = "Logging & crash reports" },
        ];

        _selectedPane = ApplicationPanes[0];

        // The PROJECT half of this screen reads the document. The application half does not, and
        // cannot: a show that carried the operator's font size to the next venue would be carrying
        // the wrong thing.
        ProjectPanes =
        [
            new() { Name = "Show behaviour" },
            new() { Name = "Authoring defaults" },
            new() { Name = "Overrides", Tally = OverrideTally(_settings) },
            new() { Name = "Save, autosave & recovery" },
            new() { Name = "Project status" },
        ];

        _openMode = _settings.OpenLocked ? "locked" : "editing";
        _listEndPolicy = _settings.AtListEnd.ToString().ToLowerInvariant();
        _runChecksOnOpen = _settings.RunStatusChecksOnOpen;
        _externalInputOffOnOpen = _settings.ExternalInputOffOnOpen;
        _clickMovesStandby = _settings.ClickMovesStandby;
        _triggerMode = _settings.NewCueTrigger.ToString().ToLowerInvariant();
        _autoRenumber = _settings.AutoRenumberOnInsert;
        _avOffset = $"{_settings.AvOffsetMs} ms";
        _followLead = $"{_settings.FollowLeadMs} ms";
        _autosaveCadence = $"{_settings.AutosaveSeconds} s";
        _recoveryCopies = _settings.RecoveryCopies.ToString();
        _saveOnGo = _settings.SaveOnGo;
        _outsideMediaPolicy = _settings.OutsideMedia switch
        {
            Core.Model.OutsideMediaPolicy.MoveToRoot => "move to root",
            Core.Model.OutsideMediaPolicy.CopyToRoot => "copy to root",
            _ => "keep in place",
        };

        // Application scope, from app-settings.json. These are MACHINE preferences: they are seeded
        // here and written straight back, never journaled, because a show that carried the operator's
        // font size to the next venue would be carrying the wrong thing.
        _theme = _app.Theme;
        _density = _app.Density;
        _rowSize = _app.RowSize;
        _fontScale = _app.FontScale;
        _ballistic = _app.MeterBallistics;
        _clipReset = _app.ClipReset;
        _rememberInspectorTab = _app.RememberInspectorTab;
        _flatActiveList = _app.FlatActiveList;
        _openDrawerOnLaunch = _app.OpenDrawerOnLaunch;
        _spaceRule = _app.SpaceRule;
        _doubleGoGuard = _app.DoubleGoGuard;
        _confirmStopAll = _app.ConfirmStopAll;
        _standbyFollowsClickDefault = _app.StandbyFollowsClick;
        _hotkeyProfile = AppHotkeys.Profiles.Contains(_app.HotkeyProfile) ? _app.HotkeyProfile : AppHotkeys.Profiles[0];
        RebuildHotkeys();
        _autoRenumberDefault = _app.AutoRenumberDefault;
        _remoteDefault = _app.RemoteDefault;
        _remotePort = _app.RemotePort;
        _remoteLanAllowed = _app.RemoteLanAllowed;
        _audioBackend = _app.AudioBackend;
        _cacheRoot = MediaCache.RootFor(_app);
        _waveformBudget = _app.WaveformBudget;
        _youTubeBudget = _app.YouTubeBudget;
        _fileLogLevel = _app.FileLogLevel;
        _logDirectory = _app.LogDirectory.Length > 0 ? _app.LogDirectory : StoragePaths.LogRoot;
        _logRetention = _app.LogRetention;
        _crashDumps = _app.CrashDumps;

        _loading = false;
    }

    /// <summary>
    /// Writes one application setting and saves immediately.
    /// </summary>
    /// <remarks>
    /// No undo and no commit step, which is the scope split made concrete (register item 26): the
    /// project half is journaled and travels in the file; this half is a machine preference that takes
    /// effect when you change it. Saving on every keystroke is affordable - the file is small and the
    /// write is atomic.
    /// </remarks>
    private void WriteApp(Action<AppSettings> change)
    {
        if (_loading)
            return;

        change(_app);
        SettingsProblem = AppSettingsStore.Save(_app)
            ? ""
            : "settings could not be saved - check the profile directory and free disk space";
        _applicationChanged?.Invoke();
    }

    /// <summary>A save failure is visible; machine preferences must never pretend to have persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsProblem))]
    private string _settingsProblem = "";

    public bool HasSettingsProblem => SettingsProblem.Length > 0;

    /// <summary>Project controls are visibly disabled when the shell's journal is locked.</summary>
    public bool CanEditProject => _journal is null || !_journal.IsReadOnly;

    partial void OnBallisticChanged(string value) => WriteApp(app => app.MeterBallistics = value);
    partial void OnClipResetChanged(string value) => WriteApp(app => app.ClipReset = value);
    partial void OnRememberInspectorTabChanged(bool value) => WriteApp(app => app.RememberInspectorTab = value);
    partial void OnFlatActiveListChanged(bool value) => WriteApp(app => app.FlatActiveList = value);
    partial void OnOpenDrawerOnLaunchChanged(bool value) => WriteApp(app => app.OpenDrawerOnLaunch = value);
    partial void OnSpaceRuleChanged(string value) => WriteApp(app => app.SpaceRule = value);
    partial void OnDoubleGoGuardChanged(string value) => WriteApp(app => app.DoubleGoGuard = value);
    partial void OnConfirmStopAllChanged(string value) => WriteApp(app => app.ConfirmStopAll = value);
    partial void OnStandbyFollowsClickDefaultChanged(bool value) => WriteApp(app => app.StandbyFollowsClick = value);
    partial void OnAutoRenumberDefaultChanged(bool value) => WriteApp(app => app.AutoRenumberDefault = value);
    partial void OnRemoteDefaultChanged(string value) => WriteApp(app => app.RemoteDefault = value);
    partial void OnRemotePortChanged(string value) => WriteApp(app => app.RemotePort = value);
    partial void OnRemoteLanAllowedChanged(bool value) => WriteApp(app => app.RemoteLanAllowed = value);
    partial void OnWaveformBudgetChanged(string value) => WriteApp(app => app.WaveformBudget = value);
    partial void OnYouTubeBudgetChanged(string value) => WriteApp(app => app.YouTubeBudget = value);
    partial void OnCacheRootChanged(string value) => WriteApp(app =>
        app.CacheRoot = string.Equals(value.Trim(), MediaCache.RootFor(new AppSettings()), StringComparison.Ordinal)
            ? ""
            : value.Trim());
    /// <summary>
    /// Records the backend choice WITHOUT changing the running one.
    /// </summary>
    /// <remarks>
    /// The note beside it goes stale on purpose the moment these differ: swapping the backend under a
    /// running show would close every device mid-cue, so the honest behaviour is to save the choice and
    /// say when it applies.
    /// </remarks>
    partial void OnAudioBackendChanged(string value)
    {
        WriteApp(app => app.AudioBackend = value);
        OnPropertyChanged(nameof(AudioBackendNote));
    }

    partial void OnFileLogLevelChanged(string value) => WriteApp(app => app.FileLogLevel = value);
    partial void OnLogDirectoryChanged(string value) => WriteApp(app =>
        app.LogDirectory = string.Equals(value.Trim(), StoragePaths.LogRoot, StringComparison.Ordinal)
            ? ""
            : value.Trim());
    partial void OnLogRetentionChanged(string value) => WriteApp(app => app.LogRetention = value);
    partial void OnCrashDumpsChanged(bool value) => WriteApp(app => app.CrashDumps = value);

    // ── writing back (register items 26 and 28) ───────────────────────────────────────────────
    // Project-scope settings are JOURNALED: they travel in the file and ⌘Z works on them, exactly as
    // it does on a cue label. Application-scope ones are machine preferences and are not - they have
    // their own store and no undo, which is what the scope split means.

    /// <summary>Writes one project setting through the journal, so it is saved AND undoable.</summary>
    private void Write<T>(string property, Func<T> read, Action<T> write, T value, string description)
    {
        if (_loading || _journal is null || EqualityComparer<T>.Default.Equals(read(), value))
            return;

        _journal.Do(new SetValueCommand<T>(
            Guid.Empty, $"settings:{property}", "settings", read, write, value, description));
        _journal.CloseGroup();
    }

    partial void OnOpenModeChanged(string value) =>
        Write("openLocked", () => _settings.OpenLocked, locked => _settings.OpenLocked = locked,
            value == "locked", $"open {value}");

    partial void OnListEndPolicyChanged(string value) =>
        Write("atListEnd", () => _settings.AtListEnd, at => _settings.AtListEnd = at,
            value switch
            {
                "loop" => AtListEnd.Loop,
                "next list" => AtListEnd.NextList,
                _ => AtListEnd.Hold,
            },
            $"at list end: {value}");

    partial void OnRunChecksOnOpenChanged(bool value) =>
        Write("runChecks", () => _settings.RunStatusChecksOnOpen,
            on => _settings.RunStatusChecksOnOpen = on, value, "run checks on open");

    partial void OnExternalInputOffOnOpenChanged(bool value) =>
        Write("externalInputOff", () => _settings.ExternalInputOffOnOpen,
            on => _settings.ExternalInputOffOnOpen = on, value, "external input off on open");

    partial void OnClickMovesStandbyChanged(bool value) =>
        Write("clickMovesStandby", () => _settings.ClickMovesStandby,
            on => _settings.ClickMovesStandby = on, value, "click moves standby");

    partial void OnTriggerModeChanged(string value) =>
        Write("newCueTrigger", () => _settings.NewCueTrigger, mode => _settings.NewCueTrigger = mode,
            value switch
            {
                "follow" => CueTrigger.Follow,
                "continue" => CueTrigger.Continue,
                _ => CueTrigger.Manual,
            },
            $"new cues are {value}");

    partial void OnAutoRenumberChanged(bool value) =>
        Write("autoRenumber", () => _settings.AutoRenumberOnInsert,
            on => _settings.AutoRenumberOnInsert = on, value, "auto-renumber on insert");

    partial void OnSaveOnGoChanged(bool value) =>
        Write("saveOnGo", () => _settings.SaveOnGo, on => _settings.SaveOnGo = on, value, "save on GO");

    partial void OnOutsideMediaPolicyChanged(string value) =>
        Write("outsideMedia", () => _settings.OutsideMedia, policy => _settings.OutsideMedia = policy,
            value switch
            {
                "move to root" => Core.Model.OutsideMediaPolicy.MoveToRoot,
                "copy to root" => Core.Model.OutsideMediaPolicy.CopyToRoot,
                _ => Core.Model.OutsideMediaPolicy.KeepInPlace,
            },
            $"media outside the root: {value}");

    partial void OnAvOffsetChanged(string value) =>
        Write("avOffset", () => _settings.AvOffsetMs, ms => _settings.AvOffsetMs = ms,
            SignedDigits(value, _settings.AvOffsetMs), "A/V offset");

    partial void OnFollowLeadChanged(string value) =>
        Write("followLead", () => _settings.FollowLeadMs, ms => _settings.FollowLeadMs = ms,
            Digits(value, _settings.FollowLeadMs), "follow lead");

    partial void OnAutosaveCadenceChanged(string value) =>
        Write("autosave", () => _settings.AutosaveSeconds, seconds => _settings.AutosaveSeconds = seconds,
            Digits(value, _settings.AutosaveSeconds), "autosave cadence");

    partial void OnRecoveryCopiesChanged(string value) =>
        Write("recoveryCopies", () => _settings.RecoveryCopies, count => _settings.RecoveryCopies = count,
            Digits(value, _settings.RecoveryCopies), "recovery copies");

    /// <summary>Reads the leading number out of a field like "30 s", keeping the old value if there is none.</summary>
    private static int Digits(string text, int fallback)
    {
        var digits = new string([.. text.TakeWhile(char.IsAsciiDigit)]);
        return int.TryParse(digits, out var value) ? value : fallback;
    }

    /// <summary>The signed variant, for a field like "-33 ms" - an A/V trim goes both ways.</summary>
    private static int SignedDigits(string text, int fallback)
    {
        var trimmed = text.TrimStart();
        var negative = trimmed.StartsWith('-');
        var value = Digits(negative ? trimmed[1..] : trimmed, int.MinValue);
        return value == int.MinValue ? fallback : negative ? -value : value;
    }

    /// <summary>
    /// The application-scope navigation - the inventory this screen is a contract for.
    /// </summary>
    /// <remarks>
    /// Every pane listed here has to exist, or a nav row leads to nothing and the reader cannot tell
    /// "not built" from "empty". The override tallies are DERIVED from the project rather than
    /// authored, so a row saying "project" is one the loaded show actually defeats.
    /// </remarks>
    public IReadOnlyList<SettingsPane> ApplicationPanes { get; }

    public IReadOnlyList<SettingsPane> ProjectPanes { get; }

    /// <summary>
    /// What this project defeats about the machine (register item 26).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overridable set is frozen at panic fade, remote API and hotkeys. Each row is derived from
    /// the two scopes rather than authored, so the ledger cannot claim an override the document does
    /// not hold - which is exactly what the sample version did, permanently listing two.
    /// </para>
    /// <para>
    /// A project override always WINS and is always VISIBLE in both scopes, which is why the row shows
    /// the app value beside it: an operator looking at the application pane has to be able to see that
    /// the number in front of them is not the one in force.
    /// </para>
    /// </remarks>
    public IReadOnlyList<OverrideRow> Overrides =>
    [
        .. new[]
        {
            _settings.PanicFadeMs is { } panic
                ? new OverrideRow("Panic fade", Seconds(_app.PanicFadeMs), Seconds(panic))
                : null,

            _settings.RemoteApi is { } remote
                ? new OverrideRow(
                    "Remote API",
                    _app.RemoteDefault == "on" ? $"on · port {_app.RemotePort}" : "off",
                    remote.Enabled ? $"on · port {remote.Port}" : "off")
                : null,
        }.OfType<OverrideRow>(),
    ];

    public bool HasOverrides => Overrides.Count > 0;

    /// <summary>Said out loud rather than left as an empty table nobody can interpret.</summary>
    public string OverrideNote => HasOverrides
        ? "A project override always wins, and is shown in both scopes."
        : "This project overrides nothing - every setting here is the machine's.";

    /// <summary>
    /// Clears one override, so the project inherits the machine's value again.
    /// </summary>
    /// <remarks>
    /// Journaled, because removing an override changes what the show DOES: a project that had pinned a
    /// 150 ms panic fade and now inherits 250 ms behaves differently, and that is exactly the sort of
    /// change somebody needs to be able to take back.
    /// </remarks>
    public void RevertOverride(string setting)
    {
        if (_journal is null)
            return;

        switch (setting)
        {
            case "Panic fade":
                Write("panicFade", () => _settings.PanicFadeMs, value => _settings.PanicFadeMs = value,
                    (int?)null, "inherit the machine's panic fade");
                break;

            case "Remote API":
                Write("remoteApi", () => _settings.RemoteApi, value => _settings.RemoteApi = value,
                    (RemoteApiOverride?)null, "inherit the machine's remote API setting");
                break;

            default:
                return;
        }

        OnPropertyChanged(nameof(Overrides));
        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(OverrideNote));
        OnPropertyChanged(nameof(ApplicationPanes));
    }

    /// <summary>How many overrides this project actually holds - nothing when it holds none.</summary>
    private static string OverrideTally(ProjectSettings settings)
    {
        var count = (settings.PanicFadeMs is null ? 0 : 1) + (settings.RemoteApi is null ? 0 : 1);
        return count == 0 ? "" : $"{count} active";
    }

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000d).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearancePane))]
    [NotifyPropertyChangedFor(nameof(IsTransportPane))]
    [NotifyPropertyChangedFor(nameof(IsHotkeysPane))]
    [NotifyPropertyChangedFor(nameof(IsNewProjectPane))]
    [NotifyPropertyChangedFor(nameof(IsRemoteApiPane))]
    [NotifyPropertyChangedFor(nameof(IsCachePane))]
    [NotifyPropertyChangedFor(nameof(IsLoggingPane))]
    [NotifyPropertyChangedFor(nameof(IsShowBehaviourPane))]
    [NotifyPropertyChangedFor(nameof(IsAuthoringPane))]
    [NotifyPropertyChangedFor(nameof(IsOverridesPane))]
    [NotifyPropertyChangedFor(nameof(IsSavePane))]
    [NotifyPropertyChangedFor(nameof(IsProjectStatusPane))]
    [NotifyPropertyChangedFor(nameof(IsApplicationScope))]
    [NotifyPropertyChangedFor(nameof(SelectedApplicationPane))]
    [NotifyPropertyChangedFor(nameof(SelectedProjectPane))]
    [NotifyPropertyChangedFor(nameof(ScopeNote))]
    [NotifyPropertyChangedFor(nameof(ScopeFile))]
    // Nullable because a ListBox WILL write null into whatever its SelectedItem binds to the moment
    // the chosen item is not among its own items. The two properties below are what the navs bind, and
    // they refuse that null; this stays honest about the fact that the write can happen at all.
    private SettingsPane? _selectedPane;

    /// <summary>
    /// The application list's own selection - null while a PROJECT pane is showing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both navs used to bind <c>SelectedItem</c> straight to <see cref="SelectedPane"/>. Two
    /// selectors sharing one selection do not co-operate: choosing "Show behaviour" set the shared
    /// property, the APPLICATION list then found its own selection missing from its items, cleared
    /// itself, and wrote that null straight back - so every property below dereferenced null the
    /// moment the operator touched the project half, and the right-hand side went blank.
    /// </para>
    /// <para>
    /// Each list gets its own view of the one selection instead, and a null write (a list clearing
    /// itself because the choice belongs to the other one) is ignored rather than adopted.
    /// </para>
    /// </remarks>
    public SettingsPane? SelectedApplicationPane
    {
        get => IsApplicationScope ? SelectedPane : null;
        set
        {
            if (value is not null)
                SelectedPane = value;
        }
    }

    /// <summary>The project list's own selection - null while an APPLICATION pane is showing.</summary>
    public SettingsPane? SelectedProjectPane
    {
        get => IsApplicationScope ? null : SelectedPane;
        set
        {
            if (value is not null)
                SelectedPane = value;
        }
    }

    public bool IsApplicationScope => ApplicationPanes.Contains(SelectedPane);

    // The inventory table on screen 12 is the living contract for this view: every pane it lists has
    // to exist, or a nav row leads to nothing and the reader cannot tell "not built" from "empty".
    //
    // Read through a null-conditional even though the setters above filter nulls: these are bound
    // from XAML, this is the failure that blanked the whole window, and a pane that quietly reads
    // "not selected" is a far better failure than an exception per property.
    public bool IsAppearancePane => SelectedPane?.Name == "Appearance & layout";
    public bool IsTransportPane => SelectedPane?.Name == "Transport defaults";
    public bool IsHotkeysPane => SelectedPane?.Name == "Hotkeys";
    public bool IsNewProjectPane => SelectedPane?.Name == "New project defaults";
    public bool IsRemoteApiPane => SelectedPane?.Name == "Remote API";
    public bool IsCachePane => SelectedPane?.Name == "Media cache";
    public bool IsLoggingPane => SelectedPane?.Name == "Logging & crash reports";
    public bool IsShowBehaviourPane => SelectedPane?.Name == "Show behaviour";
    public bool IsAuthoringPane => SelectedPane?.Name == "Authoring defaults";
    public bool IsOverridesPane => SelectedPane?.Name == "Overrides";
    public bool IsSavePane => SelectedPane?.Name == "Save, autosave & recovery";
    public bool IsProjectStatusPane => SelectedPane?.Name == "Project status";

    /// <summary>
    /// Opens the Project status window, when the host offered one.
    /// </summary>
    /// <remarks>
    /// The nav has listed "Project status" since screen 13 was built and nothing was behind it: the
    /// right-hand side simply went empty, which reads as a broken pane rather than as a link. The
    /// report itself belongs in its own window - it is a page of checks, not a settings form - so this
    /// pane is the door to it. The launcher, which has no project open, leaves this null and the pane
    /// says so instead of offering a button that would do nothing.
    /// </remarks>
    public Action? OpenProjectStatus { get; set; }

    public bool CanOpenProjectStatus => OpenProjectStatus is not null;

    /// <summary>
    /// The scope split is the whole point of this screen: application settings save immediately and
    /// have no undo, project settings are journaled edits that travel in the file (register item 26).
    /// </summary>
    public string ScopeNote => IsApplicationScope
        ? "Application settings save immediately · no undo"
        : "Project settings are journaled - ⌘Z works here";

    /// <summary>
    /// Where the selected scope's settings live, and whether this project has unsaved ones.
    /// </summary>
    /// <remarks>
    /// Read from the journal rather than authored. It used to say "4 unsaved edits" on every project
    /// forever, which is the kind of number a reader trusts once and then stops trusting anything on
    /// the screen.
    /// </remarks>
    public string ScopeFile
    {
        get
        {
            if (IsApplicationScope)
                return "app-settings.json";

            if (_journal is null)
                return "not connected to a document";

            var count = _journal.Log.Count(command => command.Domain == "settings");
            return count == 0
                ? "no unsaved setting changes"
                : $"{count} setting change{(count == 1 ? "" : "s")} in this session";
        }
    }

    // ── appearance ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> Themes { get; } = Appearance.Palettes;

    /// <summary>
    /// Why the theme row does not take effect immediately.
    /// </summary>
    /// <remarks>
    /// Said out loud rather than left for the operator to discover. Every colour in the app is looked
    /// up once when the theme is built, so a palette swap needs the app to build it again - the
    /// alternative is a live lookup on 840-odd references, which is a refactor, not a preference.
    /// </remarks>
    public string ThemeNote =>
        "booth dark is the skin the show was designed in · light is for plotting at a desk";
    public IReadOnlyList<string> Densities { get; } = ["compact", "normal", "relaxed"];
    public IReadOnlyList<string> RowSizes { get; } = ["26 px", "30 px", "38 px touch"];
    public IReadOnlyList<string> Ballistics { get; } = ["PPM fast", "VU"];
    public IReadOnlyList<string> ClipResets { get; } = ["on click", "3 s auto"];

    [ObservableProperty] private string _theme = "booth dark";
    [ObservableProperty] private string _density = "normal";
    [ObservableProperty] private string _rowSize = "26 px";
    [ObservableProperty] private string _fontScale = "100 %";

    // Density, row size and font scale are LIVE: they push resource overrides that every control reads
    // dynamically, so the app re-lays-out as the operator moves the segment. That is the half of the
    // Appearance pane that can honestly work without a restart.

    partial void OnDensityChanged(string value)
    {
        Appearance.Current.Set(value switch
        {
            "compact" => Session.Density.Compact,
            "relaxed" => Session.Density.Relaxed,
            _ => Session.Density.Normal,
        });

        WriteApp(app => app.Density = value);
    }

    partial void OnRowSizeChanged(string value)
    {
        Appearance.Current.SetRowHeight(Appearance.ParseRowHeight(value));
        WriteApp(app => app.RowSize = value);
    }

    partial void OnFontScaleChanged(string value)
    {
        Appearance.Current.SetFontScale(Appearance.ParseFontScale(value));
        WriteApp(app => app.FontScale = value);
    }

    partial void OnThemeChanged(string value)
    {
        Appearance.Current.Palette = value;
        WriteApp(app => app.Theme = value);
    }
    [ObservableProperty] private string _ballistic = "PPM fast";
    [ObservableProperty] private string _clipReset = "on click";
    [ObservableProperty] private bool _rememberInspectorTab = true;
    [ObservableProperty] private bool _flatActiveList;
    [ObservableProperty] private bool _openDrawerOnLaunch;

    // ── transport defaults ────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> SpaceRules { get; } = ["always GO", "GO unless typing", "never"];

    [ObservableProperty] private string _spaceRule = "GO unless typing";
    [ObservableProperty] private string _doubleGoGuard = "250 ms";

    // ── durations, as the operator types them ─────────────────────────────────────────────────
    // Every one of these boxes rendered a literal before: the pane looked like a settings screen and
    // was a picture of one. They are plain properties over the settings record rather than observable
    // fields, so the getter always reports what is actually stored - which is what makes refusing a
    // value simply a matter of re-announcing it.

    /// <summary>Reads "0.75 s", "750 ms" or "0.75". Null when it is none of those.</summary>
    private static int? Milliseconds(string text)
    {
        var trimmed = text.Trim();
        var isMs = trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase);

        var number = trimmed.TrimEnd('s', 'S', 'm', 'M', ' ').Replace(',', '.');

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0)
            return null;

        var ms = (int)Math.Round(isMs ? value : value * 1000);
        return ms is >= 0 and <= 600_000 ? ms : null;
    }

    /// <summary>
    /// Applies a duration box, or leaves the setting alone and puts the stored value back.
    /// </summary>
    /// <remarks>
    /// Refused rather than coerced to zero: a stop fade of 0 ms is a click on every stop in the show,
    /// and it is not what somebody who typed "seven" meant.
    /// </remarks>
    private void Duration(string value, Action<int> write, string property)
    {
        if (Milliseconds(value) is { } ms)
            WriteApp(_ => write(ms));

        OnPropertyChanged(property);
    }

    public string PeakHold
    {
        get => Seconds(_app.PeakHoldMs);
        set => Duration(value, ms => _app.PeakHoldMs = ms, nameof(PeakHold));
    }

    public string AppStopFade
    {
        get => Seconds(_app.StopFadeMs);
        set => Duration(value, ms => _app.StopFadeMs = ms, nameof(AppStopFade));
    }

    public string AppPanicFade
    {
        get => Seconds(_app.PanicFadeMs);
        set => Duration(value, ms => _app.PanicFadeMs = ms, nameof(AppPanicFade));
    }

    // ── new-project defaults ──────────────────────────────────────────────────────────────────

    public string NewProjectMixRate
    {
        get => $"{_app.NewProjectMixRate:N0} Hz";
        set
        {
            var digits = new string([.. value.Where(char.IsAsciiDigit)]);

            if (int.TryParse(digits, out var rate) && rate is >= 8_000 and <= 384_000)
                WriteApp(app => app.NewProjectMixRate = rate);

            OnPropertyChanged(nameof(NewProjectMixRate));
        }
    }

    public string NewProjectFadeIn
    {
        get => Seconds(_app.NewProjectFadeInMs);
        set => Duration(value, ms => _app.NewProjectFadeInMs = ms, nameof(NewProjectFadeIn));
    }

    public string NewProjectFadeOut
    {
        get => Seconds(_app.NewProjectFadeOutMs);
        set => Duration(value, ms => _app.NewProjectFadeOutMs = ms, nameof(NewProjectFadeOut));
    }

    // ── project scope ─────────────────────────────────────────────────────────────────────────

    /// <summary>The show's own stop fade. Journaled: it changes what every STOP in the show does.</summary>
    public string ProjectStopFade
    {
        get => Seconds(_settings.StopFadeMs);
        set
        {
            if (Milliseconds(value) is { } ms && ms != _settings.StopFadeMs)
                Write("stopFade", () => _settings.StopFadeMs, number => _settings.StopFadeMs = number,
                    ms, $"stop fade {Seconds(ms)}");

            OnPropertyChanged(nameof(ProjectStopFade));
        }
    }

    /// <summary>
    /// The project's panic-fade OVERRIDE, or empty to inherit the machine's (register item 26).
    /// </summary>
    /// <remarks>
    /// Empty is a real value here and the reason the model field is nullable: clearing the box gives
    /// the machine's setting back rather than pinning whatever number happened to be showing. The
    /// override ledger lists it exactly while it is set.
    /// </remarks>
    public string ProjectPanicFade
    {
        get => _settings.PanicFadeMs is { } ms ? Seconds(ms) : "";
        set
        {
            var wanted = value.Trim().Length == 0 ? (int?)null : Milliseconds(value);

            if (value.Trim().Length > 0 && wanted is null)
            {
                OnPropertyChanged(nameof(ProjectPanicFade));
                return;
            }

            if (wanted != _settings.PanicFadeMs)
                Write("panicFade", () => _settings.PanicFadeMs, number => _settings.PanicFadeMs = number,
                    wanted, wanted is { } ms ? $"panic fade {Seconds(ms)}" : "panic fade follows the machine");

            OnPropertyChanged(nameof(ProjectPanicFade));
            OnPropertyChanged(nameof(PanicFadeNote));
            OnPropertyChanged(nameof(Overrides));
            OnPropertyChanged(nameof(HasOverrides));
        }
    }

    /// <summary>Whether the panic box is overriding, and what it would inherit if cleared.</summary>
    public string PanicFadeNote => _settings.PanicFadeMs is null
        ? $"follows this machine - {Seconds(_app.PanicFadeMs)}"
        : $"overrides this machine's {Seconds(_app.PanicFadeMs)} ⚑";

    /// <summary>A new cue's default fade in, in the project's own settings.</summary>
    public string ProjectFadeIn
    {
        get => Seconds(_settings.DefaultFadeInMs);
        set
        {
            if (Milliseconds(value) is { } ms && ms != _settings.DefaultFadeInMs)
                Write("fadeIn", () => _settings.DefaultFadeInMs, n => _settings.DefaultFadeInMs = n, ms,
                    $"default fade in {Seconds(ms)}");

            OnPropertyChanged(nameof(ProjectFadeIn));
        }
    }

    public string ProjectFadeOut
    {
        get => Seconds(_settings.DefaultFadeOutMs);
        set
        {
            if (Milliseconds(value) is { } ms && ms != _settings.DefaultFadeOutMs)
                Write("fadeOut", () => _settings.DefaultFadeOutMs, n => _settings.DefaultFadeOutMs = n, ms,
                    $"default fade out {Seconds(ms)}");

            OnPropertyChanged(nameof(ProjectFadeOut));
        }
    }

    /// <summary>The law the show's stop fade follows, named as the curve library names it.</summary>
    public string StopFadeCurveName => _settings.StopFadeCurve.PresetId is not null
        ? "project preset"
        : _settings.StopFadeCurve.Law switch
        {
            S.Media.Session.FadeCurve.Linear => "linear",
            S.Media.Session.FadeCurve.Exponential => "exponential",
            S.Media.Session.FadeCurve.SCurve => "s-curve",
            _ => "equal-power",
        };

    public IReadOnlyList<CurveOption> StopFadeCurveChoices => CurveLibrary.Choices(_project);

    /// <summary>The project stop fade uses the same picker and editor as cue fades. It used to be a
    /// read-only name even though the document stored a full CurveSpec.</summary>
    public int StopFadeCurveIndex
    {
        get
        {
            var choices = StopFadeCurveChoices;
            for (var index = 0; index < choices.Count; index++)
            {
                var choice = choices[index];
                if (_settings.StopFadeCurve.PresetId is { } presetId
                    ? choice.PresetId == presetId
                    : _settings.StopFadeCurve.Points is { Count: > 1 }
                        ? choice.IsCustom
                        : choice.Law == _settings.StopFadeCurve.Law)
                    return index;
            }
            return -1;
        }
        set
        {
            if (_journal is null
                || value < 0
                || value >= StopFadeCurveChoices.Count
                || value == StopFadeCurveIndex)
                return;

            var target = StopFadeTarget();
            var choice = StopFadeCurveChoices[value];
            IProjectCommand? command = choice switch
            {
                { Law: { } law } => CurveEdits.PickLaw(target, law),
                { PresetId: { } presetId } => CurveEdits.PickPreset(target, presetId, choice.Name),
                { IsCustom: true } => new SetCurveCommand(target, target.Read(), "draw a custom stop fade"),
                _ => null,
            };
            if (command is null)
                return;

            _journal.Do(command);
            _journal.CloseGroup();
            OnPropertyChanged(nameof(StopFadeCurveIndex));
            OnPropertyChanged(nameof(StopFadeCurveName));
        }
    }

    public CurveEditorViewModel? StopFadeEditor() => _journal is null
        ? null
        : new CurveEditorViewModel(
            _journal,
            StopFadeTarget(),
            "Project · stop fade",
            _settings.StopFadeMs > 0 ? TimeSpan.FromMilliseconds(_settings.StopFadeMs) : null);

    public void RefreshStopFade()
    {
        OnPropertyChanged(nameof(StopFadeCurveChoices));
        OnPropertyChanged(nameof(StopFadeCurveIndex));
        OnPropertyChanged(nameof(StopFadeCurveName));
    }

    private CurveSpecTarget StopFadeTarget() =>
        new(
            Guid.Empty,
            "projectStopFade",
            _settings.StopFadeCurve,
            _project,
            CurveLibrary.EmptyShape("projectStopFade"));

    /// <summary>The nav's project heading - the show's own name, not a fixture's.</summary>
    public string ProjectScopeHeading => $"PROJECT · {_project.Title.ToUpperInvariant()}";

    /// <summary>
    /// The remote token, masked.
    /// </summary>
    /// <remarks>
    /// Never rendered in full. It is machine-scope and grants the ability to fire the show; a settings
    /// pane left open on a booth machine should not be a way to read it off the screen.
    /// </remarks>
    public string RemoteTokenMask =>
        _app.RemoteToken.Length == 0 ? "not yet minted" : new string('•', 8) + " · set";

    /// <summary>Mints a new token, invalidating every client using the old one.</summary>
    public void RotateRemoteToken()
    {
        WriteApp(app => app.RemoteToken = "");
        _app.EnsureRemoteToken();
        WriteApp(_ => { });

        OnPropertyChanged(nameof(RemoteTokenMask));
    }

    /// <summary>Where the show's media lives; relative paths in the document resolve against it.</summary>
    public string ProjectMediaRoot
    {
        get => _settings.MediaRoot;
        set
        {
            if (_settings.MediaRoot == value)
                return;

            Write("mediaRoot", () => _settings.MediaRoot, path => _settings.MediaRoot = path, value,
                "media root");

            OnPropertyChanged(nameof(ProjectMediaRoot));
        }
    }

    [ObservableProperty] private string _confirmStopAll = "3 cues";
    [ObservableProperty] private bool _standbyFollowsClickDefault;

    // ── hotkeys ───────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> HotkeyProfiles => AppHotkeys.Profiles;

    [ObservableProperty] private string _hotkeyProfile = "Cue standard";

    [ObservableProperty] private IReadOnlyList<HotkeyRow> _hotkeys = [];

    partial void OnHotkeyProfileChanged(string value)
    {
        if (_loading || !AppHotkeys.Profiles.Contains(value))
            return;
        WriteApp(app =>
        {
            app.HotkeyProfile = value;
            AppHotkeys.Reset(app);
        });
        RebuildHotkeys();
    }

    private void RebuildHotkeys()
    {
        Hotkeys = [.. AppHotkeys.Commands.Select(command =>
            new HotkeyRow(command.Id, command.Name, AppHotkeys.Gesture(_app, command.Id), command.Group,
                gesture =>
                {
                    WriteApp(app => app.HotkeyBindings[command.Id] = gesture);
                    OnPropertyChanged(nameof(HotkeyCollisionNote));
                }))];
        OnPropertyChanged(nameof(HotkeyCollisionNote));
    }

    public void ResetHotkeys()
    {
        WriteApp(AppHotkeys.Reset);
        RebuildHotkeys();
    }

    public string HotkeyCollisionNote
    {
        get
        {
            var reserved = Hotkeys.FirstOrDefault(row =>
                string.Equals(row.Gesture, "Esc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Gesture, "Esc×2", StringComparison.OrdinalIgnoreCase));
            if (reserved is not null)
                return $"Reserved: Esc / Esc×2 is the fixed Stop/Panic safety sequence; {reserved.Command} will not intercept it.";
            var duplicate = Hotkeys.Where(row => row.Gesture.Length > 0)
                .GroupBy(row => row.Gesture, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            return duplicate is null
                ? "Esc stops; a second Esc within 700 ms panics. These safety keys are reserved."
                : $"Conflict: {duplicate.Key} is assigned to {string.Join(" and ", duplicate.Select(row => row.Command))}.";
        }
    }

    // ── new project defaults ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _autoRenumberDefault = true;

    // ── remote API (app default; the server and its endpoints live in Targets) ────────────────
    public IReadOnlyList<string> RemoteDefaults { get; } = ["off", "on"];

    [ObservableProperty] private string _remoteDefault = "off";
    [ObservableProperty] private string _remotePort = "8420";
    [ObservableProperty] private bool _remoteLanAllowed;

    // ── advanced ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Which library opens local sound devices.</summary>
    /// <remarks>
    /// The two enumerate DIFFERENT devices on the same box, so this changes what the whole app thinks
    /// the rig is. It is read at start-up, before a window exists, which is why the pane says so
    /// instead of pretending the change is live.
    /// </remarks>
    public IReadOnlyList<string> AudioBackends { get; } = ["portaudio", "miniaudio"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioBackendNote))]
    private string _audioBackend = "portaudio";

    public string AudioBackendNote =>
        string.Equals(AudioBackend, _app.AudioBackend, StringComparison.OrdinalIgnoreCase)
            ? "the backend this session opened devices with"
            : "restart HaCue2 for this to take effect";

    // ── media cache ───────────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheRootNote))]
    private string _cacheRoot = "~/.local/share/hacue2/cache";
    [ObservableProperty] private string _waveformBudget = "2.0 GB";
    [ObservableProperty] private string _youTubeBudget = "20 GB";
    /// <summary>What the cache is actually using. Measured, not stated.</summary>
    public string CacheInUse => MediaCache.DescribeRoot(_activeCacheRoot);

    public string CacheRootNote => string.Equals(CacheRoot, _activeCacheRoot, StringComparison.Ordinal)
        ? "waveforms and prepared YouTube media share this root"
        : $"restart HaCue2 to move the active cache from {_activeCacheRoot}";

    public string LoggingActivationNote =>
        "file level, directory, retention and crash-report changes take effect after restarting HaCue2";

    /// <summary>What the last clear freed, beside the buttons that did it.</summary>
    [ObservableProperty]
    private string _cacheNote = "";

    /// <summary>Deletes waveform peaks, which re-derive from the source media.</summary>
    public void ClearWaveformCache() => ClearCache("waveforms");

    public void ClearYouTubeCache() => ClearCache("youtube");

    private void ClearCache(params string[] kinds)
    {
        CacheNote = MediaCache.ClearRoot(_activeCacheRoot, kinds);
        OnPropertyChanged(nameof(CacheInUse));
    }

    /// <summary>
    /// Opens the log folder in the machine's file manager.
    /// </summary>
    /// <remarks>
    /// The one action in this pane that leaves the app, and the reason it is worth having: "send me
    /// your logs" is otherwise a request to go hunting through a hidden data directory.
    /// </remarks>
    public string OpenLogFolder()
    {
        var path = _app.LogDirectory.Length > 0 ? _app.LogDirectory : StoragePaths.LogRoot;

        try
        {
            StoragePaths.EnsureDirectory(path);

            using var opened = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

            return path;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A headless or locked-down machine has no file manager to ask. Reporting the path is
            // still useful - it is the thing the operator actually needs.
            return $"{path} (could not open a file manager: {failure.Message})";
        }
    }

    // ── logging & crash reports ───────────────────────────────────────────────────────────────
    public IReadOnlyList<string> LogLevels { get; } =
        ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty] private string _fileLogLevel = "Information";
    [ObservableProperty] private string _logDirectory = "~/.local/share/hacue2/logs";
    [ObservableProperty] private string _logRetention = "14 days";
    [ObservableProperty] private bool _crashDumps = true;

    // ── save, autosave & recovery ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _autosaveCadence = "30 s";

    /// <summary>Venue A/V trim, e.g. "20 ms" or "-15 ms" - positive shows the picture earlier.</summary>
    [ObservableProperty] private string _avOffset = "0 ms";
    [ObservableProperty] private string _followLead = "0 ms";
    [ObservableProperty] private string _recoveryCopies = "5";
    [ObservableProperty] private string _recoveryLocation = "beside the project file";

    /// <summary>Off by default: a GO is a performance action, and a disk write on it is a stall.</summary>
    [ObservableProperty] private bool _saveOnGo;

    // ── show behaviour ────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> OpenModes { get; } = ["editing", "locked"];
    public IReadOnlyList<string> ListEndPolicies { get; } = ["hold", "loop", "next list"];

    [ObservableProperty] private string _openMode = "editing";
    [ObservableProperty] private string _listEndPolicy = "hold";
    [ObservableProperty] private bool _runChecksOnOpen = true;
    [ObservableProperty] private bool _externalInputOffOnOpen = true;

    /// <summary>
    /// Register item 6 - default OFF: a single click view-selects and double-click or the explicit
    /// ↑/↓ Stby commands move standby. Shows that want QLab-style click-to-target flip this.
    /// </summary>
    [ObservableProperty] private bool _clickMovesStandby;

    // ── authoring defaults ────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> TriggerModes { get; } = ["manual", "follow", "continue"];
    public IReadOnlyList<string> OutsideMediaPolicies { get; } = ["keep in place", "move to root", "copy to root"];

    [ObservableProperty] private string _triggerMode = "manual";
    [ObservableProperty] private string _outsideMediaPolicy = "keep in place";
    [ObservableProperty] private bool _autoRenumber = true;
}

/// <summary>
/// Screen 14 - the renamed Preflight, running the real pass over the loaded document.
/// </summary>
/// <remarks>
/// Nothing on this screen is authored any more: the rows, their severities and the summary all come
/// from <see cref="ProjectStatus"/>, which is the same code <c>hacue2-check</c> runs. A status view
/// that listed its own findings could disagree with the CLI, and the first time it did nobody would
/// know which to believe.
/// </remarks>
public partial class ProjectStatusViewModel : ObservableObject, IDisposable
{
    private readonly ProjectJournal? _journal;
    private readonly IProjectEnvironment? _environment;
    private readonly string? _projectPath;
    private readonly YouTubePreparationQueue? _youTubeDownloads;

    public ProjectStatusViewModel(
        HaCueProject project,
        IProjectEnvironment? environment = null,
        ProjectJournal? journal = null,
        string? projectPath = null,
        YouTubePreparationQueue? youTubeDownloads = null)
    {
        _journal = journal;
        _environment = environment;
        _projectPath = projectPath;
        _youTubeDownloads = youTubeDownloads;
        Project = project;
        Report = ProjectStatus.Run(project, projectPath, environment);
        Title = $"Project status - {project.Title}";
        Checks = RowsOf(Report);

        // The relink pane offers the first missing file, because that is the one the operator is
        // looking at when they arrive here.
        _missingFile = Report.Checks
            .FirstOrDefault(check => check.Name == "Media files")?.Issues
            .FirstOrDefault()?.Message ?? "nothing missing";

        if (_youTubeDownloads is not null)
        {
            _youTubeDownloads.Changed += OnYouTubeDownloadChanged;
            _youTubeDownloads.ReadinessChanged += OnYouTubeReadinessChanged;
        }
    }

    public ProjectStatusReport Report { get; private set; }

    /// <summary>The check table. Rebuilt by <see cref="Rerun"/> - the rows ARE the report.</summary>
    public IReadOnlyList<CheckRow> Checks { get; private set; }

    public string Title { get; }

    private static IReadOnlyList<CheckRow> RowsOf(ProjectStatusReport report) =>
    [
        .. report.Checks.Select(check => new CheckRow
        {
            Check = check.Name,
            Result = new Status(Result(check), Gel(check.Outcome)),
            Detail = check.Issues.Count > 0 ? check.Issues[0].Message : check.Detail,
            Fix = check.Fix,
        }),
    ];

    /// <summary>The document, for the relink actions this window offers.</summary>
    public HaCueProject Project { get; } = null!;

    /// <summary>
    /// Relinks every missing file by searching a new root.
    /// </summary>
    /// <remarks>
    /// Register-item behaviour worth restating: relink only touches MISSING references. Rewriting ones
    /// that already resolve would, on a machine where the old root is still mounted, silently move the
    /// show onto a different copy of the same media. The unresolved list is reported rather than
    /// swallowed - a relink that fixed nine of ten files and said "done" fails on the tenth cue,
    /// mid-performance, with no record of which one.
    /// </remarks>
    public PromptViewModel? RelinkUnderRoot()
    {
        if (_journal is null)
            return null;

        return new PromptViewModel(
            "Relink under a new root",
            "only MISSING files are touched",
            [
                new PromptField
                {
                    Label = "Root",
                    Kind = PromptFieldKind.Folder,
                    Value = Project.Settings.MediaRoot,
                    Hint = "the folder to search",
                },
                new PromptField
                {
                    Label = "Match",
                    Kind = PromptFieldKind.Choice,
                    Options = ["by filename anywhere", "by the same sub-path"],
                    Hint = "filename survives a reorganised tree · sub-path keeps the structure",
                },
            ],
            prompt =>
            {
                var root = prompt["Root"].Value.Trim();
                if (root.Length == 0)
                    return;

                LastRelink = MediaEdits.Relink(
                    _journal,
                    root,
                    prompt["Match"].SelectedIndex == 0
                        ? RelinkStrategy.ByFileName
                        : RelinkStrategy.BySubPath,
                    _projectPath);

                Rerun();
            },
            confirm: "RELINK");
    }

    /// <summary>Points ONE missing reference at a file the operator chose.</summary>
    public void RelinkOne(string cuePath, string chosen)
    {
        if (_journal is null || chosen.Length == 0)
            return;

        MediaEdits.RelinkOne(_journal, cuePath, chosen, _projectPath);
        Rerun();
    }

    /// <summary>The path a manual relink would replace, or empty when nothing is missing.</summary>
    public string MissingPath =>
        MediaPaths.ReferencesIn(Project)
            .Select(reference => reference.Path)
            .FirstOrDefault(path => !FileSystemEnvironment.Instance.MediaExists(
                MediaPaths.Resolve(Project, path, _projectPath)))
        ?? "";

    /// <summary>What the last relink changed and what it could not find.</summary>
    public MediaEditResult? LastRelink { get; private set; }

    public string RelinkSummary => LastRelink is not { } result
        ? ""
        : result.IsComplete
            ? $"relinked {result.Changed.Count} file(s)"
            : $"relinked {result.Changed.Count} · {result.Unresolved.Count} still missing";

    public bool HasRelinked => LastRelink is not null;

    /// <summary>Whether the last copy landed, so the button can say so instead of doing it silently.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _hasCopied;

    public string CopyLabel => HasCopied ? "COPIED" : "COPY REPORT";

    public void NoteCopied() => HasCopied = true;

    /// <summary>
    /// Runs the checks again over the current document and machine.
    /// </summary>
    /// <remarks>
    /// Public because the operator asks for it directly: they have just plugged the interface in, or
    /// put the media back, and want to know whether that fixed it without reopening the show.
    /// </remarks>
    public void Rerun()
    {
        HasCopied = false;
        Report = ProjectStatus.Run(Project, _projectPath, _environment);
        Checks = RowsOf(Report);
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(Checks));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RelinkSummary));
        OnPropertyChanged(nameof(HasRelinked));
        OnPropertyChanged(nameof(MissingPath));

        // The window's own missing-file readout, re-derived the same way the constructor did it.
        MissingFile = Report.Checks
            .FirstOrDefault(check => check.Name == "Media files")?.Issues
            .Select(issue => issue.Message)
            .FirstOrDefault()
            ?? "nothing is missing";
    }

    /// <summary>Queues every non-ready YouTube cue; completion continues after this window closes.</summary>
    public void QueueMissingYouTube()
    {
        if (_youTubeDownloads is null)
            return;

        var queued = 0;
        foreach (var cue in Project.AllCues().OfType<MediaCueNode>()
                     .Where(cue => SourceUri.KindOf(cue.MediaPath) == SourceKind.YouTube)
                     .Where(cue => _youTubeDownloads.StateOf(cue.MediaPath) != YouTubeCacheState.Ready))
        {
            var original = cue.MediaPath;
            var completion = _youTubeDownloads.Enqueue(original);
            _ = AdoptResolvedSelectionAsync(cue.Id, original, completion);
            queued++;
        }

        DownloadRepairNote = queued == 0
            ? "all YouTube cues are already ready"
            : $"queued {queued} YouTube cue{(queued == 1 ? "" : "s")} - downloads continue in the background";
        Rerun();
    }

    private async Task AdoptResolvedSelectionAsync(
        Guid cueId,
        string originalUri,
        Task<YouTubePreparationResult> completion)
    {
        var result = await completion.ConfigureAwait(false);
        if (result.Prepared is not { } prepared)
            return;

        var resolvedUri = YouTubeSourceUri.Build(prepared.Manifest.VideoId, prepared.ResolvedSelection);
        if (string.Equals(resolvedUri, originalUri, StringComparison.Ordinal))
            return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_journal is null
                || _journal.IsReadOnly
                || Project.FindCue(cueId) is not MediaCueNode cue
                || !string.Equals(cue.MediaPath, originalUri, StringComparison.Ordinal))
                return;

            _journal.Do(new SetValueCommand<string>(
                cue.Id, "mediaPath", "cues",
                () => cue.MediaPath, value => cue.MediaPath = value,
                resolvedUri, "pin resolved YouTube streams"));
            Rerun();
        });
    }

    private void OnYouTubeDownloadChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshDownloadRepairNote);

    private void OnYouTubeReadinessChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        RefreshDownloadRepairNote();
        Rerun();
    });

    private void RefreshDownloadRepairNote()
    {
        if (_youTubeDownloads is null)
            return;

        var snapshot = _youTubeDownloads.Snapshot(
            Project.AllCues().OfType<MediaCueNode>().Select(cue => cue.MediaPath));
        DownloadRepairNote = snapshot.Downloading > 0
            ? $"downloading YouTube {snapshot.Fraction * 100:0}%"
              + (snapshot.Queued > 0 ? $" · {snapshot.Queued} queued" : "")
            : snapshot.Queued > 0
                ? $"{snapshot.Queued} YouTube cue{(snapshot.Queued == 1 ? "" : "s")} queued"
                : snapshot.Failed > 0
                    ? $"download failed - {snapshot.LastError ?? "use Fix to retry"}"
                    : DownloadRepairNote.StartsWith("queued ", StringComparison.Ordinal)
                        ? "YouTube downloads finished"
                        : DownloadRepairNote;
    }

    [ObservableProperty]
    private string _downloadRepairNote = "";

    public void Dispose()
    {
        if (_youTubeDownloads is null)
            return;
        _youTubeDownloads.Changed -= OnYouTubeDownloadChanged;
        _youTubeDownloads.ReadinessChanged -= OnYouTubeReadinessChanged;
    }

    public string Summary => Report.Summary;

    public string ErrorNote => Report.Errors == 0
        ? Report.Warnings == 0
            ? "all checks passed - nothing to fix before the show"
            : $"no errors · {Report.Warnings} warning{(Report.Warnings == 1 ? "" : "s")} - the status token is green"
        : $"{Report.Errors} error{(Report.Errors == 1 ? "" : "s")} keep the status token red";

    /// <summary>
    /// Whether the foot of the window should read GREEN.
    /// </summary>
    /// <remarks>
    /// The note was painted with the error colour unconditionally, so a show with nothing wrong
    /// announced "no errors" in red - the one glance an operator takes before doors, answered in the
    /// colour that means stop. Errors are what turn it red, because errors are what
    /// <c>hacue2-check</c> exits 1 on; warnings keep it green and say so in the text.
    /// </remarks>
    public bool IsClean => Report.Errors == 0;

    /// <summary>The headless twin is <c>hacue2-check</c>, exit 1 while errors remain (register item 25).</summary>
    public string HeadlessNote { get; } = "hacue2-check exits 1 while errors remain";

    /// <summary>
    /// Copies every referenced file into one directory and repoints the show at it.
    /// </summary>
    /// <remarks>
    /// Journaled, because it rewrites paths in the document and an operator who consolidated into the
    /// wrong directory needs a way back. What it could not copy is reported by name - a project that
    /// looks consolidated and half-works at the venue is the worst of both outcomes.
    /// </remarks>
    public void Consolidate()
    {
        if (_journal is null || ConsolidateInto.Trim().Length == 0)
            return;

        var result = MediaEdits.Consolidate(_journal, ConsolidateInto.Trim(), _projectPath);

        ConsolidateNote = result.IsComplete
            ? $"copied {result.Changed.Count} file{(result.Changed.Count == 1 ? "" : "s")}"
            : $"copied {result.Changed.Count} · {result.Unresolved.Count} could not be copied: "
              + string.Join(", ", result.Unresolved.Take(3).Select(Path.GetFileName))
              + (result.Unresolved.Count > 3 ? " …" : "");

        Rerun();
        OnPropertyChanged(nameof(ConsolidateNote));
    }

    /// <summary>What the last consolidate did. Empty until one has run.</summary>
    public string ConsolidateNote { get; private set; } = "";

    /// <summary>Whether there is a journal behind this window - false in a preview.</summary>
    public bool CanEdit => _journal is { IsReadOnly: false };

    [ObservableProperty] private string _missingFile;
    [ObservableProperty] private string _consolidateInto = "";
    [ObservableProperty] private bool _copyMedia = true;
    [ObservableProperty] private bool _includeReport = true;
    [ObservableProperty] private bool _zipWhenDone;

    private static string Result(StatusCheck check) => check.Outcome switch
    {
        CheckOutcome.Passed => "ok",
        CheckOutcome.NotChecked => "not checked",
        _ => check.Detail,
    };

    private static Gel Gel(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Passed => ViewModels.Gel.Green,
        CheckOutcome.Warning => ViewModels.Gel.Amber,
        CheckOutcome.Failed => ViewModels.Gel.Red,
        _ => ViewModels.Gel.Neutral,
    };
}

/// <summary>Screen 15 - bay counters, composition telemetry, and a level-filtered log tail.</summary>
/// <remarks>
/// The event panel is a tail of the <c>Microsoft.Extensions.Logging</c> pipeline with a selectable
/// minimum level - the same sink the file log uses (register item 27). One logging system, two readers;
/// a second event collector would drift from the archive the moment either changed.
/// </remarks>
public partial class DiagnosticsViewModel(ShowRuntime runtime, ShowHost? host = null) : ObservableObject
{
    // Read THROUGH the runtime rather than copied out of it: this window sits open on a second monitor
    // for a whole show, and a snapshot taken when it opened would freeze at the moment nothing was
    // wrong yet.
    public IReadOnlyList<BayRow> BayRows => runtime.BayRows;
    public IReadOnlyList<CompositionStatsRow> Compositions => runtime.CompositionStats;

    /// <summary>
    /// The live tail of the app's ONE logging pipeline (register item 27).
    /// </summary>
    /// <remarks>
    /// Read straight off the ring rather than copied through the runtime, because the ring already is
    /// the bounded window this panel wants - a second buffer would be a second thing to keep in step.
    /// The FILTER is applied here rather than at the sink so turning it down shows what has already
    /// been captured, instead of only what arrives afterwards. A fault that reproduces once is the
    /// reason that distinction matters.
    /// </remarks>
    public IReadOnlyList<LogLine> Log =>
        AppLogging.Current is not { } logging
            ? []
            : [.. logging.Ring.Snapshot()
                .Where(entry => entry.Level >= Threshold)
                .OrderByDescending(entry => entry.Timestamp)
                .Select(Line)];

    public IReadOnlyList<string> Levels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Log))]
    [NotifyPropertyChangedFor(nameof(LogSummary))]
    private string _minimumLevel = "Information";

    private Microsoft.Extensions.Logging.LogLevel Threshold => MinimumLevel switch
    {
        "Trace" => Microsoft.Extensions.Logging.LogLevel.Trace,
        "Debug" => Microsoft.Extensions.Logging.LogLevel.Debug,
        "Warning" => Microsoft.Extensions.Logging.LogLevel.Warning,
        "Error" => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Information,
    };

    /// <summary>How much of the window is being shown, and what the ring had to drop.</summary>
    /// <remarks>
    /// The drop count is worth surfacing: a burst that overflowed the ring means the interesting line
    /// may already be gone, and an operator reading a tail that silently lost records would draw the
    /// wrong conclusion from what is left.
    /// </remarks>
    public string LogSummary
    {
        get
        {
            if (AppLogging.Current is not { } logging)
                return "no logging pipeline";

            var dropped = logging.Ring.DroppedCount;
            var shown = Log.Count;

            return dropped == 0
                ? $"{shown} line(s) at {MinimumLevel} and above"
                : $"{shown} line(s) · {dropped} older line(s) dropped from the ring";
        }
    }

    private static LogLine Line(LogRingEntry entry) => new(
        entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
        entry.Level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => "TRACE",
            Microsoft.Extensions.Logging.LogLevel.Debug => "DEBUG",
            Microsoft.Extensions.Logging.LogLevel.Warning => "WARN",
            Microsoft.Extensions.Logging.LogLevel.Error => "ERROR",
            Microsoft.Extensions.Logging.LogLevel.Critical => "CRIT",
            _ => "INFO",
        },
        // Short category: "S.Media.Routing.AudioRouter" is a column nobody can read at that width, and
        // the last segment is the part that identifies the source.
        entry.Category.Split('.') is { Length: > 0 } parts ? parts[^1] : entry.Category,
        entry.Exception is null ? entry.Message : $"{entry.Message} - {entry.Exception.Message}",
        entry.Level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Warning => Gel.Amber,
            >= Microsoft.Extensions.Logging.LogLevel.Error => Gel.Red,
            Microsoft.Extensions.Logging.LogLevel.Debug or Microsoft.Extensions.Logging.LogLevel.Trace => Gel.Steel,
            _ => Gel.Neutral,
        });

    public string BayHeader => runtime.BaySummary.Length > 0 ? runtime.BaySummary : "no session";

    /// <summary>
    /// What is actually wrong, or nothing.
    /// </summary>
    /// <remarks>
    /// Derived from the rows rather than authored. It used to be a fixed sentence about a recording
    /// encoder that was not draining - permanently on screen, true only by coincidence.
    /// </remarks>
    public string BayWarning
    {
        get
        {
            var problems = host?.Problems ?? [];

            if (problems.Count > 0)
                return problems[0];

            var behind = runtime.BayRows.Where(row => row.State.IsWarn || row.State.IsBad).ToList();

            return behind.Count == 0
                ? ""
                : $"{behind[0].Name}: {behind[0].State.Text}"
                  + (behind.Count > 1 ? $" (+{behind.Count - 1} more)" : "");
        }
    }

    public bool HasWarning => BayWarning.Length > 0;

    public string CountersSince => host is null ? "no session - nothing to count" : "counters since the show started";

    /// <summary>Re-reads everything. Driven by the same tick that fills the runtime.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(BayRows));
        OnPropertyChanged(nameof(Compositions));
        OnPropertyChanged(nameof(Log));
        OnPropertyChanged(nameof(LogSummary));
        OnPropertyChanged(nameof(BayHeader));
        OnPropertyChanged(nameof(BayWarning));
        OnPropertyChanged(nameof(HasWarning));
    }

    /// <summary>Forgets the accumulated problem lines. The counters themselves belong to the bay.</summary>
    public void ResetCounters()
    {
        host?.ResetMeterClips();
        host?.ClearProblems();
        AppLogging.Current?.Ring.Clear();
        Refresh();
    }

    /// <summary>The whole bay as plain text, for pasting to somebody who is not in the building.</summary>
    public string Report() => host?.Report() ?? "No session is running - there is nothing to report.";
}
