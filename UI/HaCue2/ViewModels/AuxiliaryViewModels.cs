using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Controls;
using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.Sample;
using HaCue2.Session;
using S.Media.Core.Diagnostics;

namespace HaCue2.ViewModels;

/// <summary>Screen 01 — recents, inline recovery, and the cheap machine checks.</summary>
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

    /// <summary>Raised with a project the launcher loaded or created, and where it came from.</summary>
    public event Action<HaCueProject, string>? ProjectOpened;

    /// <summary>What went wrong with the last open, for the launcher's own line.</summary>
    [ObservableProperty]
    private string _openFailure = "";

    /// <summary>Hands a loaded project to the shell.</summary>
    public void Adopt(HaCueProject project, string path) => ProjectOpened?.Invoke(project, path);

    /// <summary>The prompt behind "New project…".</summary>
    public PromptViewModel NewProject() =>
        new(
            "New project",
            "seeded with a Main L/R pair and one cue list",
            [
                new PromptField { Label = "Name", Value = "Untitled show" },
                new PromptField
                {
                    Label = "Media root",
                    Value = "",
                    Hint = "where this show's media lives · relinking searches under it",
                },
            ],
            prompt => Adopt(
                ProjectFiles.Create(prompt["Name"].Value.Trim(), prompt["Media root"].Value.Trim()),
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
    /// clicked — the recents list looked real and did the same thing every time. Now the row's path is
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
    /// Opens the autosave instead of the file — the RECOVER answer.
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

    /// <summary>Throws the autosave away — the operator has decided the file on disk is the truth.</summary>
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
            Contents = recent.Summary.Length > 0 ? recent.Summary : "—",
            Opened = Ago(recent.LastOpened),
            // File.Exists, not an open: the launcher must not stall on a disconnected volume, and
            // "gone" is the only answer that changes what the row can do.
            IsMissing = !File.Exists(recent.Path),
            IsCurrent = index == 0,
        }),
    ];

    /// <summary>"today 14:02", "yesterday", "Jul 28" — the resolution that is actually useful.</summary>
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
            : "not checked — no backend"),
        new("", "", "video", "not checked yet"),
        new("", "", "ndi", "not checked yet"),
        new("", "", "midi", "not checked yet"),
    ];
}

/// <summary>
/// Screen 04's curve editor — the "custom" option behind every curve picker in the app.
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
    private IDisposable? _drag;

    /// <summary>The dummy editor, for a preview with no document behind it.</summary>
    public CurveEditorViewModel()
    {
        Title = "Fade curve";
        Hint = "same control everywhere a curve is picked";
        _knots = [new CurveKnot(0, 1), new CurveKnot(0.35, 0.55), new CurveKnot(1, 0)];
    }

    public CurveEditorViewModel(ProjectJournal journal, ICurveTarget target, string title)
    {
        _journal = journal;
        _target = target;
        Title = title;
        Hint = "same control everywhere a curve is picked";
        _knots = target.Read();
        _supportsHold = target.SupportsHold;
    }

    public string Title { get; }
    public string Hint { get; }

    private IReadOnlyList<CurveKnot> _knots;
    private readonly bool _supportsHold = true;

    public IReadOnlyList<CurveOption> Curves { get; } = CurveLibrary.Curves;

    /// <summary>
    /// The handles, in CANVAS space: y is flipped, because a level of 1 is drawn at the top.
    /// </summary>
    /// <remarks>
    /// Flipped here rather than in the control, which knows nothing about levels — the same split the
    /// timeline's effect lanes already make.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Points =>
    [
        .. _knots.Select((knot, index) => new CurvePoint(
            knot.X, 1 - knot.Y, index == SelectedIndex, knot.Hold)),
    ];

    /// <summary>
    /// The polyline. A held point gets a corner the point list does not contain.
    /// </summary>
    /// <remarks>
    /// Without the extra corner a hold would be drawn as the ramp it explicitly is not — the picture
    /// would show the operator a fade that the engine will not play.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Shape
    {
        get
        {
            var shape = new List<CurvePoint>();

            for (var index = 0; index < _knots.Count; index++)
            {
                var knot = _knots[index];
                shape.Add(new CurvePoint(knot.X, 1 - knot.Y));

                if (knot.Hold && index + 1 < _knots.Count)
                    shape.Add(new CurvePoint(_knots[index + 1].X, 1 - knot.Y));
            }

            return shape;
        }
    }

    public IReadOnlyList<string> Scales { get; } = ["dB", "linear"];
    public IReadOnlyList<string> Segments { get; } = ["smooth", "hold"];

    [ObservableProperty] private string _scale = "dB";
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _presetName = "";

    public bool HasSelection => SelectedIndex >= 0 && SelectedIndex < _knots.Count;

    public bool SupportsHold => _supportsHold;

    /// <summary>The selected point's position, as the numeric route onto the same edit.</summary>
    public string SelectedPoint => HasSelection
        ? $"{_knots[SelectedIndex].X * 100:0.#} % · {_knots[SelectedIndex].Y * 100:0.#} %"
        : "—";

    /// <summary>Whether the selected point holds. Bound to the smooth/hold segment picker.</summary>
    public string Segment
    {
        get => HasSelection && _knots[SelectedIndex].Hold ? "hold" : "smooth";
        set
        {
            if (_target is null || _journal is null || !HasSelection)
                return;

            if (CurveEdits.SetHold(_target, SelectedIndex, value == "hold") is { } command)
            {
                _journal.Do(command);
                _journal.CloseGroup();
                Reload();
            }
        }
    }

    public string EditHint =>
        "double-click adds a point · drag off the canvas removes · right-click holds the segment";

    /// <summary>A drag, a nudge, an add or a remove — every route ends in one command.</summary>
    public void Apply(CurveGesture gesture)
    {
        if (_target is null || _journal is null)
            return;

        if (gesture.Kind == CurveGestureKind.Select)
        {
            SelectedIndex = gesture.Index;
            return;
        }

        // The gesture arrives in canvas space; the document stores levels, so y flips back here.
        var x = gesture.X;
        var y = 1 - gesture.Y;

        var command = gesture.Kind switch
        {
            CurveGestureKind.Move => CurveEdits.Move(_target, gesture.Index, x, y),
            CurveGestureKind.Add when !CurveEdits.HasPointNear(_target, x) => CurveEdits.Add(_target, x, y),
            CurveGestureKind.Remove => CurveEdits.Remove(_target, gesture.Index),
            _ => null,
        };

        if (command is null)
            return;

        _drag ??= _journal.Composite(command.Description, "cues");
        _journal.Do(command);

        if (gesture.Kind == CurveGestureKind.Remove)
            SelectedIndex = -1;

        Reload();
    }

    public void ToggleHold(int index)
    {
        SelectedIndex = index;
        Segment = HasSelection && _knots[index].Hold ? "smooth" : "hold";
    }

    /// <summary>Ends the gesture, closing its undo step.</summary>
    public void EndGesture()
    {
        _drag?.Dispose();
        _drag = null;
    }

    /// <summary>Re-reads the curve from the document — after an edit here, or an undo anywhere.</summary>
    public void Reload()
    {
        if (_target is not null)
            _knots = _target.Read();

        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(Shape));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Segment));
    }

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Segment));
    }
}

/// <summary>Screens 12 and 13 — application scope (not journaled) and project scope (journaled).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ProjectSettings _settings;
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

    public SettingsViewModel() : this(SampleProject.Create())
    {
    }

    public SettingsViewModel(
        HaCueProject project, ProjectJournal? journal = null, AppSettings? app = null)
    {
        _settings = project.Settings;
        _journal = journal;
        _app = app ?? new AppSettings();

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
            new() { Name = "Overrides", Tally = _settings.RemoteApi is null ? "1 active" : "2 active" },
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
        _rememberTimelineDock = _app.RememberTimelineDock;
        _flatActiveList = _app.FlatActiveList;
        _openDrawerOnLaunch = _app.OpenDrawerOnLaunch;
        _spaceRule = _app.SpaceRule;
        _doubleGoGuard = _app.DoubleGoGuard;
        _confirmStopAll = _app.ConfirmStopAll;
        _standbyFollowsClickDefault = _app.StandbyFollowsClick;
        _autoRenumberDefault = _app.AutoRenumberDefault;
        _remoteDefault = _app.RemoteDefault;
        _remotePort = _app.RemotePort;
        _remoteLanAllowed = _app.RemoteLanAllowed;
        _cacheRoot = _app.CacheRoot.Length > 0 ? _app.CacheRoot : "(shared framework cache)";
        _waveformBudget = _app.WaveformBudget;
        _thumbnailBudget = _app.ThumbnailBudget;
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
    /// effect when you change it. Saving on every keystroke is affordable — the file is small and the
    /// write is atomic.
    /// </remarks>
    private void WriteApp(Action<AppSettings> change)
    {
        if (_loading)
            return;

        change(_app);
        AppSettingsStore.Save(_app);
    }

    partial void OnBallisticChanged(string value) => WriteApp(app => app.MeterBallistics = value);
    partial void OnClipResetChanged(string value) => WriteApp(app => app.ClipReset = value);
    partial void OnRememberInspectorTabChanged(bool value) => WriteApp(app => app.RememberInspectorTab = value);
    partial void OnRememberTimelineDockChanged(bool value) => WriteApp(app => app.RememberTimelineDock = value);
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
    partial void OnThumbnailBudgetChanged(string value) => WriteApp(app => app.ThumbnailBudget = value);
    partial void OnFileLogLevelChanged(string value) => WriteApp(app => app.FileLogLevel = value);
    partial void OnLogRetentionChanged(string value) => WriteApp(app => app.LogRetention = value);
    partial void OnCrashDumpsChanged(bool value) => WriteApp(app => app.CrashDumps = value);

    // ── writing back (register items 26 and 28) ───────────────────────────────────────────────
    // Project-scope settings are JOURNALED: they travel in the file and ⌘Z works on them, exactly as
    // it does on a cue label. Application-scope ones are machine preferences and are not — they have
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

    /// <summary>
    /// The application-scope navigation — the inventory this screen is a contract for.
    /// </summary>
    /// <remarks>
    /// Every pane listed here has to exist, or a nav row leads to nothing and the reader cannot tell
    /// "not built" from "empty". The override tallies are DERIVED from the project rather than
    /// authored, so a row saying "project" is one the loaded show actually defeats.
    /// </remarks>
    public IReadOnlyList<SettingsPane> ApplicationPanes { get; }

    public IReadOnlyList<SettingsPane> ProjectPanes { get; }
    public IReadOnlyList<OverrideRow> Overrides { get; } = SampleShow.Overrides;

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
    [NotifyPropertyChangedFor(nameof(IsApplicationScope))]
    [NotifyPropertyChangedFor(nameof(ScopeNote))]
    [NotifyPropertyChangedFor(nameof(ScopeFile))]
    private SettingsPane _selectedPane;

    public bool IsApplicationScope => ApplicationPanes.Contains(SelectedPane);

    // The inventory table on screen 12 is the living contract for this view: every pane it lists has
    // to exist, or a nav row leads to nothing and the reader cannot tell "not built" from "empty".
    public bool IsAppearancePane => SelectedPane.Name == "Appearance & layout";
    public bool IsTransportPane => SelectedPane.Name == "Transport defaults";
    public bool IsHotkeysPane => SelectedPane.Name == "Hotkeys";
    public bool IsNewProjectPane => SelectedPane.Name == "New project defaults";
    public bool IsRemoteApiPane => SelectedPane.Name == "Remote API";
    public bool IsCachePane => SelectedPane.Name == "Media cache";
    public bool IsLoggingPane => SelectedPane.Name == "Logging & crash reports";
    public bool IsShowBehaviourPane => SelectedPane.Name == "Show behaviour";
    public bool IsAuthoringPane => SelectedPane.Name == "Authoring defaults";
    public bool IsOverridesPane => SelectedPane.Name == "Overrides";
    public bool IsSavePane => SelectedPane.Name == "Save, autosave & recovery";

    /// <summary>
    /// The scope split is the whole point of this screen: application settings save immediately and
    /// have no undo, project settings are journaled edits that travel in the file (register item 26).
    /// </summary>
    public string ScopeNote => IsApplicationScope
        ? "Application settings save immediately · no undo"
        : "Project settings are journaled — ⌘Z works here";

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
    /// up once when the theme is built, so a palette swap needs the app to build it again — the
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
    [ObservableProperty] private bool _rememberTimelineDock = true;
    [ObservableProperty] private bool _flatActiveList;
    [ObservableProperty] private bool _openDrawerOnLaunch;

    // ── transport defaults ────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> SpaceRules { get; } = ["always GO", "GO unless typing", "never"];

    [ObservableProperty] private string _spaceRule = "GO unless typing";
    [ObservableProperty] private string _doubleGoGuard = "250 ms";
    [ObservableProperty] private string _confirmStopAll = "3 cues";
    [ObservableProperty] private bool _standbyFollowsClickDefault;

    // ── hotkeys ───────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Deliberately a stub. The round-3 decision was that the hotkey grid stays WIP until most other
    /// parts are built and the command list is stable — a binding surface over a command list that is
    /// still moving would have to be redone, and worse, would look settled while it wasn't.
    /// </summary>
    public IReadOnlyList<HotkeyRow> Hotkeys { get; } =
    [
        new("GO", "Space", "transport", ""),
        new("Stop", "Esc", "transport", ""),
        new("Panic", "Ctrl+Esc", "transport", ""),
        new("Standby up / down", "↑ / ↓", "transport", ""),
        new("Output info drawer", "F9", "shell", ""),
        new("Preview on audition", "Ctrl+P", "cue", "Ctrl+Shift+P"),
    ];

    // ── new project defaults ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _autoRenumberDefault = true;

    // ── remote API (app default; the server and its endpoints live in Targets) ────────────────
    public IReadOnlyList<string> RemoteDefaults { get; } = ["off", "on"];

    [ObservableProperty] private string _remoteDefault = "off";
    [ObservableProperty] private string _remotePort = "8420";
    [ObservableProperty] private bool _remoteLanAllowed;

    // ── media cache ───────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _cacheRoot = "~/.local/share/hacue2/cache";
    [ObservableProperty] private string _waveformBudget = "2.0 GB";
    [ObservableProperty] private string _thumbnailBudget = "512 MB";
    public string CacheInUse { get; } = "waveforms 1.2 GB · probes 44 MB · thumbnails 180 MB";

    // ── logging & crash reports ───────────────────────────────────────────────────────────────
    public IReadOnlyList<string> LogLevels { get; } =
        ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty] private string _fileLogLevel = "Information";
    [ObservableProperty] private string _logDirectory = "~/.local/share/hacue2/logs";
    [ObservableProperty] private string _logRetention = "14 days";
    [ObservableProperty] private bool _crashDumps = true;

    // ── save, autosave & recovery ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _autosaveCadence = "30 s";
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
    /// Register item 6 — default OFF: a single click view-selects and double-click or the explicit
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
/// Screen 14 — the renamed Preflight, running the real pass over the loaded document.
/// </summary>
/// <remarks>
/// Nothing on this screen is authored any more: the rows, their severities and the summary all come
/// from <see cref="ProjectStatus"/>, which is the same code <c>hacue2-check</c> runs. A status view
/// that listed its own findings could disagree with the CLI, and the first time it did nobody would
/// know which to believe.
/// </remarks>
public partial class ProjectStatusViewModel : ObservableObject
{
    private readonly ProjectJournal? _journal;
    private readonly IProjectEnvironment? _environment;

    public ProjectStatusViewModel(
        HaCueProject project,
        IProjectEnvironment? environment = null,
        ProjectJournal? journal = null)
    {
        _journal = journal;
        _environment = environment;
        Project = project;
        Report = ProjectStatus.Run(project, environment: environment);
        Title = $"Project status — {project.Title}";
        Checks = RowsOf(Report);

        // The relink pane offers the first missing file, because that is the one the operator is
        // looking at when they arrive here.
        _missingFile = Report.Checks
            .FirstOrDefault(check => check.Name == "Media files")?.Issues
            .FirstOrDefault()?.Message ?? "nothing missing";
    }

    public ProjectStatusReport Report { get; private set; }

    /// <summary>The check table. Rebuilt by <see cref="Rerun"/> — the rows ARE the report.</summary>
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
    /// swallowed — a relink that fixed nine of ten files and said "done" fails on the tenth cue,
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
                new PromptField { Label = "Root", Value = Project.Settings.MediaRoot, Hint = "the folder to search" },
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
                        : RelinkStrategy.BySubPath);

                Rerun();
            },
            confirm: "RELINK");
    }

    /// <summary>Points ONE missing reference at a file the operator chose.</summary>
    public void RelinkOne(string cuePath, string chosen)
    {
        if (_journal is null || chosen.Length == 0)
            return;

        MediaEdits.RelinkOne(_journal, cuePath, chosen);
        Rerun();
    }

    /// <summary>The path a manual relink would replace, or empty when nothing is missing.</summary>
    public string MissingPath =>
        MediaPaths.ReferencesIn(Project)
            .Select(reference => reference.Path)
            .FirstOrDefault(path => !FileSystemEnvironment.Instance.MediaExists(
                MediaPaths.Resolve(Project, path, null)))
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
        Report = ProjectStatus.Run(Project, environment: _environment);
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

    public string Summary => Report.Summary;

    public string ErrorNote => Report.Errors == 0
        ? "no errors — the status token is green"
        : $"{Report.Errors} error{(Report.Errors == 1 ? "" : "s")} keep the status token red";

    /// <summary>The headless twin is <c>hacue2-check</c>, exit 1 while errors remain (register item 25).</summary>
    public string HeadlessNote { get; } = "hacue2-check exits 1 while errors remain";

    /// <summary>
    /// Copies every referenced file into one directory and repoints the show at it.
    /// </summary>
    /// <remarks>
    /// Journaled, because it rewrites paths in the document and an operator who consolidated into the
    /// wrong directory needs a way back. What it could not copy is reported by name — a project that
    /// looks consolidated and half-works at the venue is the worst of both outcomes.
    /// </remarks>
    public void Consolidate()
    {
        if (_journal is null || ConsolidateInto.Trim().Length == 0)
            return;

        var result = MediaEdits.Consolidate(_journal, ConsolidateInto.Trim());

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

    /// <summary>Whether there is a journal behind this window — false in a preview.</summary>
    public bool CanEdit => _journal is not null;

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

/// <summary>Screen 15 — bay counters, composition telemetry, and a level-filtered log tail.</summary>
/// <remarks>
/// The event panel is a tail of the <c>Microsoft.Extensions.Logging</c> pipeline with a selectable
/// minimum level — the same sink the file log uses (register item 27). One logging system, two readers;
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
    /// the bounded window this panel wants — a second buffer would be a second thing to keep in step.
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
        entry.Exception is null ? entry.Message : $"{entry.Message} — {entry.Exception.Message}",
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
    /// encoder that was not draining — permanently on screen, true only by coincidence.
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

    public string CountersSince => host is null ? "no session — nothing to count" : "counters since the show started";

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
        host?.ClearProblems();
        AppLogging.Current?.Ring.Clear();
        Refresh();
    }

    /// <summary>The whole bay as plain text, for pasting to somebody who is not in the building.</summary>
    public string Report() => host?.Report() ?? "No session is running — there is nothing to report.";
}
