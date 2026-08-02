using Avalonia.Media;
using HaCue2.ViewModels;

namespace HaCue2.Sample;

/// <summary>
/// The sample data that is NOT about the show document: machine preferences, the launcher's recents,
/// the app's own endpoint table, and the curve library the pickers offer.
/// </summary>
/// <remarks>
/// Everything a <c>HaCueProject</c> can express moved to <see cref="SampleProject"/>, and everything a
/// running session would supply moved to <see cref="SampleRuntime"/>. What is left here is genuinely
/// app- or machine-scoped — a recents list is not show data, and neither is the theme.
/// </remarks>
public static class SampleShow
{
    // ── screen 01 · launcher ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<RecentProjectRow> Recents { get; } =
    [
        new()
        {
            Name = "midsummer-2026", Path = "~/shows/midsummer-2026.hacue2proj",
            Contents = "84 cues · 3 lists · 11 logical outs", Opened = "today 14:02", IsCurrent = true,
        },
        new()
        {
            Name = "queen-tribute-tour", Path = "~/shows/queen-tribute.hacue2proj",
            Contents = "412 cues · 1 list · 8 logical outs", Opened = "Jul 28",
        },
        new()
        {
            Name = "corp-keynote-demo", Path = "~/shows/keynote.hacue2proj",
            Contents = "23 cues · 1 list · 4 logical outs", Opened = "Jul 19",
        },
        new()
        {
            Name = "bar-mitzvah-04", Path = "file missing · /media/usb0/…",
            Contents = "—", Opened = "Jun 30", IsMissing = true,
        },
    ];

    public const string RecoveryNotice = "midsummer-2026 has an autosave newer than its file (14:02, +3 edits)";

    public static IReadOnlyList<LogLine> MachineChecks { get; } =
    [
        new("", "", "audio", "PortAudio · 3 devices"),
        new("", "", "video", "2 screens · GL ok"),
        new("", "", "ndi", "runtime 6.1 found"),
        new("", "", "midi", "2 inputs · 1 output"),
    ];

    // ── screen 04 · the curve library ─────────────────────────────────────────────────────────

    /// <summary>
    /// Thumbnail geometries in a 44 × 26 box, drawn top-left (unity) to bottom-right (silence) so a
    /// fade-out reads as a descent. These are the built-in laws; a project's own custom curves are
    /// saved as presets on its document.
    /// </summary>
    public static IReadOnlyList<CurveOption> FadeCurves { get; } =
    [
        new("linear", "M2,2 L42,24"),
        new("eq-power", "M2,2 Q30,4 42,24"),
        new("expo", "M2,2 Q10,22 42,24"),
        new("s-curve", "M2,2 C18,2 26,24 42,24"),
        new("custom ✎", "M2,2 L14,6 L22,18 L32,12 L42,24"),
    ];

    public static Geometry CustomCurve { get; } = Geometry.Parse("M2,3 L28,8 L48,28 L70,20 L98,38");

    public static IReadOnlyList<CurvePoint> CustomCurvePoints { get; } =
    [
        new(0.02, 0.075), new(0.28, 0.20), new(0.48, 0.70, IsSelected: true),
        new(0.70, 0.50), new(0.98, 0.95),
    ];

    // ── screen 08 · the record pattern's help line ────────────────────────────────────────────

    public const string RecordPatternTokens =
        "{date} {time} {project} {list} {n} — preview: show-2026-08-01-3.flac";

    // ── screens 12/13 · settings navigation ───────────────────────────────────────────────────

    public static IReadOnlyList<SettingsPane> ApplicationPanes { get; } =
    [
        new() { Name = "Appearance & layout" },
        new() { Name = "Transport defaults", Tally = "1 override", TallyGel = Gel.Amber },
        new() { Name = "Hotkeys", Tally = "project", TallyGel = Gel.Amber },
        new() { Name = "New project defaults" },
        new() { Name = "Remote API", Tally = "project", TallyGel = Gel.Amber },
        new() { Name = "Media cache" },
        new() { Name = "Logging & crash reports" },
    ];

    public static IReadOnlyList<OverrideRow> Overrides { get; } =
    [
        new("Panic fade", "0.15 s", "0.25 s"),
        new("Remote API", "off", "on · port 8420"),
    ];

    // ── screen 11b · the app's own remote API ─────────────────────────────────────────────────

    public static IReadOnlyList<EndpointRow> RemoteEndpoints { get; } =
    [
        new("POST", "/cues/go", "fire the standby cue", "241"),
        new("POST", "/cues/{id}/fire", "fire a specific cue", "12"),
        new("POST", "/transport/stop · /panic · /pause", "transport controls", "3"),
        new("POST", "/standby/{id}", "move standby", "0"),
        new("POST", "/lists/{id}/go", "fire the standby cue of one list", "0"),
        new("GET", "/status", "active cues, standby, issues", "1 402"),
        new("GET", "/lists", "cue lists + numbers/labels", "7"),
    ];

    // ── screen 15 · telemetry the engine will supply ──────────────────────────────────────────
    // Reached only through ShowRuntime, never bound directly: invented numbers standing in for
    // AudioPatchBay.SnapshotDiagnostics() and the log ring.

    public static IReadOnlyList<BayRow> BayRows { get; } =
    [
        new()
        {
            Name = "18i20 · out (master)", State = new("advancing", Gel.Green), InFlight = "2", Capacity = "4",
            Enqueued = "219 480", Processed = "219 478", Dropped = new("0"), Latency = new("21.3 ms"),
            Epoch = "7", Rate = "48 000",
        },
        new()
        {
            Name = "NDI Prog · out", State = new("open", Gel.Green), InFlight = "3", Capacity = "8",
            Enqueued = "219 480", Processed = "219 477", Dropped = new("0"), Latency = new("62.5 ms"),
            Epoch = "7", Rate = "48 000 −1 ppm",
        },
        new()
        {
            Name = "Record · out", State = new("armed · behind", Gel.Amber), InFlight = "7", Capacity = "8",
            Enqueued = "219 480", Processed = "219 461", Dropped = new("12", Gel.Amber),
            Latency = new("79.1 ms", Gel.Amber), Epoch = "7", Rate = "44 100 rs",
        },
        new()
        {
            Name = "Wedge · out", State = new("absent", Gel.Red), Dropped = new("—"), Latency = new("—"),
        },
        new()
        {
            Name = "lease · Q12 Preshow bed", State = new("sounding", Gel.Green), InFlight = "4", Capacity = "8",
            Enqueued = "219 480", Processed = "219 476", Dropped = new("0"), Latency = new("40.0 ms"),
            Epoch = "7", IsLease = true,
        },
        new()
        {
            Name = "lease · Q13.1 Storm bed", State = new("sounding", Gel.Green), InFlight = "4", Capacity = "8",
            Enqueued = "36 960", Processed = "36 956", Dropped = new("0"), Latency = new("40.0 ms"),
            Epoch = "7", IsLease = true,
        },
        new()
        {
            Name = "lease · Q2 walk-in (fading)", State = new("releasing", Gel.Amber), InFlight = "2",
            Capacity = "8", Enqueued = "412 800", Processed = "412 798", Dropped = new("0"),
            Latency = new("40.0 ms"), Epoch = "7", IsLease = true,
        },
        new()
        {
            Name = "monitor · audition", State = new("idle"), Dropped = new("—"), Latency = new("—"),
            IsLease = true,
        },
    ];

    public static IReadOnlyList<LogLine> LogTail { get; } =
    [
        new("14:02:11", "WARN", "S.Media.Routing", "OutputPump Record: Submit took 118 ms", Gel.Amber),
        new("13:58:40", "ERROR", "HaCue2.Audio", "logical output Lobby unpatched — 2 senders routed silent", Gel.Red),
        new("13:58:40", "WARN", "HaCue2.Status", "project status: 2 errors, 2 warnings", Gel.Amber),
        new("13:44:02", "INFO", "S.Media.Session", "clock master 18i20 epoch 7 · 48 000 native"),
    ];

    public static IReadOnlyList<LogLine> TriggerMonitor { get; } =
    [
        new("14:01:22", "MIDI in", "", "APC mini · note-on 3 ch 1 vel 127 → Q16", Gel.Congo),
        new("13:58:41", "OSC in", "", ":9000 · /hacue/go → GO", Gel.Steel),
        new("13:58:12", "key", "", "Space → GO"),
    ];
}
