using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using HaCue2.Sample;
using HaCue2.Session;

namespace HaCue2.ViewModels;

/// <summary>Screen 01 — recents, inline recovery, and the cheap machine checks.</summary>
/// <remarks>
/// The import door is gone (register item 29): HaCue2 is a clean start and a <c>.haplayproj</c>
/// converter is a separate companion tool with no priority, so the launcher stops promising one.
/// </remarks>
public partial class LauncherViewModel : ObservableObject
{
    /// <summary>Raised when a recent is opened; the App swaps the launcher for the shell.</summary>
    public event Action<RecentProjectRow>? ProjectRequested;

    public IReadOnlyList<RecentProjectRow> Recents { get; } = SampleShow.Recents;
    public IReadOnlyList<LogLine> MachineChecks { get; } = SampleShow.MachineChecks;
    public string RecoveryNotice { get; } = SampleShow.RecoveryNotice;

    [ObservableProperty]
    private bool _hasRecovery = true;

    [ObservableProperty]
    private RecentProjectRow? _selectedRecent;

    public string SeedNote { get; } =
        "New projects are seeded from your defaults (Settings · Application · New project defaults): "
        + "a Main L/R logical pair patched to the machine's default device.";

    public void Open(RecentProjectRow? project)
    {
        project ??= Recents[0];
        if (project.IsMissing)
            return;
        ProjectRequested?.Invoke(project);
    }
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
    public string Title { get; } = "Fade curve · Q13.1 fade out";
    public string Hint { get; } = "same control everywhere a curve is picked";

    public IReadOnlyList<CurveOption> Curves { get; } = SampleShow.FadeCurves;
    public Avalonia.Media.Geometry Curve { get; } = SampleShow.CustomCurve;
    public IReadOnlyList<CurvePoint> Points { get; } = SampleShow.CustomCurvePoints;

    public IReadOnlyList<string> Scales { get; } = ["dB", "linear"];
    public IReadOnlyList<string> Segments { get; } = ["smooth", "hold"];

    [ObservableProperty] private string _scale = "dB";
    [ObservableProperty] private string _segment = "smooth";
    [ObservableProperty] private string _selectedPoint = "48 % · −14.2 dB";
    [ObservableProperty] private string _presetName = "“slow tail” · project preset";

    public string EditHint { get; } =
        "double-click adds a point · drag off the canvas removes · audition plays the fade on the cue's preview";
}

/// <summary>Screens 12 and 13 — application scope (not journaled) and project scope (journaled).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ProjectSettings _settings;

    public SettingsViewModel() : this(SampleProject.Create())
    {
    }

    public SettingsViewModel(HaCueProject project)
    {
        _settings = project.Settings;
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
    }

    public IReadOnlyList<SettingsPane> ApplicationPanes { get; } = SampleShow.ApplicationPanes;
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

    public string ScopeFile => IsApplicationScope ? "app-settings.json" : "4 unsaved edits";

    // ── appearance ────────────────────────────────────────────────────────────────────────────
    public IReadOnlyList<string> Themes { get; } = ["booth dark", "dark", "light"];
    public IReadOnlyList<string> Densities { get; } = ["compact", "normal", "relaxed"];
    public IReadOnlyList<string> RowSizes { get; } = ["26 px", "30 px", "38 px touch"];
    public IReadOnlyList<string> Ballistics { get; } = ["PPM fast", "VU"];
    public IReadOnlyList<string> ClipResets { get; } = ["on click", "3 s auto"];

    [ObservableProperty] private string _theme = "booth dark";
    [ObservableProperty] private string _density = "normal";
    [ObservableProperty] private string _rowSize = "30 px";
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
    public ProjectStatusViewModel(HaCueProject project, IProjectEnvironment? environment = null)
    {
        Report = ProjectStatus.Run(project, environment: environment);
        Title = $"Project status — {project.Title}";

        Checks =
        [
            .. Report.Checks.Select(check => new CheckRow
            {
                Check = check.Name,
                Result = new Status(Result(check), Gel(check.Outcome)),
                Detail = check.Issues.Count > 0 ? check.Issues[0].Message : check.Detail,
                Fix = check.Fix,
            }),
        ];

        // The relink pane offers the first missing file, because that is the one the operator is
        // looking at when they arrive here.
        _missingFile = Report.Checks
            .FirstOrDefault(check => check.Name == "Media files")?.Issues
            .FirstOrDefault()?.Message ?? "nothing missing";
    }

    public ProjectStatusReport Report { get; }
    public IReadOnlyList<CheckRow> Checks { get; }
    public string Title { get; }

    public string Summary => Report.Summary;

    public string ErrorNote => Report.Errors == 0
        ? "no errors — the status token is green"
        : $"{Report.Errors} error{(Report.Errors == 1 ? "" : "s")} keep the status token red";

    /// <summary>The headless twin is <c>hacue2-check</c>, exit 1 while errors remain (register item 25).</summary>
    public string HeadlessNote { get; } = "hacue2-check exits 1 while errors remain";

    [ObservableProperty] private string _missingFile;
    [ObservableProperty] private string _consolidateInto = "~/shows/midsummer-tour/";
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
public partial class DiagnosticsViewModel(ShowRuntime runtime) : ObservableObject
{
    public IReadOnlyList<BayRow> BayRows { get; } = runtime.BayRows;
    public IReadOnlyList<CompositionStatsRow> Compositions { get; } = runtime.CompositionStats;
    public IReadOnlyList<LogLine> Log { get; } = runtime.Log;

    public IReadOnlyList<string> Levels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty]
    private string _minimumLevel = "Warning";

    public string BayHeader { get; } = runtime.BaySummary + " · 480-sample chunks";

    public string BayWarning { get; } =
        "7 of 8 chunks in flight, 12 dropped — the encoder is not draining. Recording will gap before it fails.";

    public string CountersSince { get; } = "Counters since 13:44:02";
}
