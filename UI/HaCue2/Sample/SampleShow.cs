using Avalonia.Media;
using HaCue2.ViewModels;

namespace HaCue2.Sample;

/// <summary>
/// The fictional <c>midsummer-2026</c> show the mockup is drawn with — every table, tree, matrix,
/// canvas and log line in this shell comes from here.
/// </summary>
/// <remarks>
/// It is deliberately ONE file, and deliberately the only file in the project that knows any of these
/// strings. Phase 5 of the extraction plan replaces it with a loaded <c>HaCueProject</c>; the view-models
/// take their content through a constructor argument so that swap is a change to this class's callers
/// and nothing else.
/// <para>
/// The content is copied faithfully from the mockup, including the two failure states screen 06 exists
/// to catch (Orchestra patched-but-unfed, Lobby fed-but-unpatched) and the absent Wedge interface.
/// Fixture data that is all-green is fixture data that never proves the error styling works.
/// </para>
/// </remarks>
public static class SampleShow
{
    public const string ProjectName = "midsummer-2026";
    public const string ProjectFile = "midsummer-2026.hacue2proj";
    public const string MixRate = "48 000 Hz";
    public const string ClockMaster = "18i20";

    // ── screen 01 · launcher ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<RecentProjectRow> Recents { get; } =
    [
        new()
        {
            Name = "midsummer-2026", Path = "~/shows/midsummer-2026.hacue2proj",
            Contents = "84 cues · 3 lists · 12 logical outs", Opened = "today 14:02", IsCurrent = true,
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

    // ── screen 02 · the cue tree ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<CueRow> Act1Cues { get; } =
    [
        new()
        {
            Number = "12", Label = "Preshow bed", Kind = CueKind.Media, Source = "preshow-loop.wav",
            Fade = "3.0", Length = "6:00", Level = "−6.0", IsRunning = true,
            Badges = [new Badge("loop")],
        },
        new()
        {
            Number = "12.5", Label = "House to half", Kind = CueKind.Action, Source = "OSC /eos/cue/2/fire",
            IsStandby = true, Badges = [new Badge("OSC", Gel.Steel)],
        },
        new()
        {
            Number = "13", Label = "Act 1 · Opening sequence", Kind = CueKind.Group,
            Source = "timeline group · 4", Length = "2:14", Level = "0.0", IsRunning = true,
            Badges = [new Badge("timeline")],
        },
        new()
        {
            Number = "13.1", Label = "Storm bed", Kind = CueKind.Media, Source = "sfx/storm-bed.flac",
            Fade = "3.0", Length = "2:14", Level = "−3.0", Depth = 1,
        },
        new()
        {
            Number = "13.2", Label = "Projection · rain", Kind = CueKind.Video, Source = "video/rain-loop.mov",
            Fade = "1.5", Length = "0:48", Depth = 1, IsRunning = true, Badges = [new Badge("Cyc")],
        },
        new()
        {
            Number = "13.3", Label = "Thunder crack (cut for previews)", Kind = CueKind.Media,
            Source = "sfx/thunder-03.wav", Length = "0:04", Level = "+2.0", Depth = 1,
            IsDisabled = true, Badges = [new Badge("disabled")],
        },
        new()
        {
            Number = "14", Label = "Patch · Act 1 foldback up", Kind = CueKind.Patch,
            Source = "snapshot “Act 1”", Fade = "4.0", Badges = [new Badge("patch")],
        },
        new()
        {
            Number = "15", Label = "Interval music", Kind = CueKind.Media,
            Source = "media offline · interval.wav", Fade = "6.0", Level = "−9.0", IsBroken = true,
            Badges = [new Badge("offline", Gel.Red)],
        },
    ];

    /// <summary>Screen 03 — the same tree scoped to one song group, 28 cues of which 6 are drawn.</summary>
    public static IReadOnlyList<CueRow> ScopedCues { get; } =
    [
        new()
        {
            Number = "7", Label = "Bohemian Rhapsody", Kind = CueKind.Group, Source = "timeline group · 28",
            Length = "5:55", Level = "0.0", IsRunning = true, Badges = [new Badge("timeline")],
        },
        new()
        {
            Number = "7.1", Label = "Track", Kind = CueKind.Media, Source = "songs/07-bohemian.flac",
            Fade = "1.0", Length = "5:55", Level = "−4.0", Depth = 1, IsRunning = true,
            Badges = [new Badge("env 6")],
        },
        new()
        {
            Number = "7.2", Label = "Ballad look", Kind = CueKind.Action, Source = "OSC /eos/cue/7.2",
            Depth = 1, IsRunning = true, Badges = [new Badge("OSC", Gel.Steel)],
        },
        new()
        {
            Number = "7.3", Label = "Opera section — strobe", Kind = CueKind.Action, Source = "OSC /eos/cue/7.3",
            Depth = 1, IsStandby = true, Badges = [new Badge("OSC", Gel.Steel)],
        },
        new()
        {
            Number = "7.4", Label = "Projection · silhouettes", Kind = CueKind.Video,
            Source = "video/silhouette.mov", Fade = "2.0", Length = "1:12", Depth = 1,
            Badges = [new Badge("Cyc"), new Badge("opac 4")],
        },
        new()
        {
            Number = "7.5", Label = "Rock section — full rig", Kind = CueKind.Action, Source = "OSC /eos/cue/7.5",
            Depth = 1, Badges = [new Badge("OSC", Gel.Steel)],
        },
    ];

    public static IReadOnlyList<ActiveCueRow> ActiveCues { get; } =
    [
        new()
        {
            Number = "12", Label = "Preshow bed", Clock = "02:41 / 06:00", Progress = 0.44,
            Destination = "Main L/R",
        },
        new()
        {
            Number = "13", Label = "Act 1 · Opening sequence", Qualifier = "timeline · 3 of 4",
            Clock = "00:38 / 02:14", Progress = 0.28, IsGroup = true,
        },
        new()
        {
            Number = "13.1", Label = "Storm bed", Clock = "00:38 / 02:14", Progress = 0.28,
            Destination = "Main, Fold", IsChild = true,
        },
        new()
        {
            Number = "13.2", Label = "Projection · rain", Clock = "00:41 / 00:48", Progress = 0.85,
            Destination = "Cyc", IsChild = true, IsNearEnd = true,
        },
        new()
        {
            Number = "9", Label = "Walk-in music", Qualifier = "Preshow", Clock = "fade 2.1 s",
            Progress = 0.62, Destination = "Main L/R", IsFading = true,
        },
    ];

    /// <summary>Screen 03's active list — proves scope never hides a sounding cue.</summary>
    public static IReadOnlyList<ActiveCueRow> ScopedActiveCues { get; } =
    [
        new()
        {
            Number = "7", Label = "Bohemian Rhapsody", Qualifier = "timeline · 3 of 28",
            Clock = "01:12 / 5:55", Progress = 0.20, IsGroup = true,
        },
        new()
        {
            Number = "2", Label = "Haze loop", Qualifier = "Preshow · out of scope", Clock = "12:41 / ∞",
            Progress = 1.0,
        },
    ];

    // ── screen 02b · the Output info drawer ───────────────────────────────────────────────────

    public static IReadOnlyList<ProgramMeter> ProgramMeters { get; } =
    [
        new("ML", 0.58, 0.71),
        new("MR", 0.54, 0.68),
        new("FL", 0.31, 0.44),
        new("FR", 0.29, 0.41),
        new("SUB", 0.97, 0.99, IsClipping: true),
    ];

    public static IReadOnlyList<OutputLineChip> LineChips { get; } =
    [
        new() { Name = "18i20", Suffix = "master", Detail = "48k · 0 drop · 21 ms · 2/4" },
        new() { Name = "NDI Prog", Detail = "2 rx · 0 drop · 3/8" },
        new() { Name = "Record", Detail = "41:20 · 12 drop · 7/8", Gel = Gel.Amber },
        new() { Name = "Wedge", Detail = "device absent", Gel = Gel.Red },
        new() { Name = "Projector A", Detail = "29.97 · 0 late" },
    ];

    public const string BaySummary = "5 leases · 12 logical · 48 000 Hz";
    public const string BayClock = "clock 01:12:44.318 · epoch 7 · adv";

    // ── screen 03 · lists and groups ──────────────────────────────────────────────────────────

    public static IReadOnlyList<SettingsPane> CueListScopes { get; } =
    [
        new() { Name = "Preshow", Tally = "11" },
        new() { Name = "Act 1", Tally = "84" },
        new() { Name = "Act 2", Tally = "76" },
    ];

    public static IReadOnlyList<SettingsPane> GroupScopes { get; } =
    [
        new() { Name = "Opening sequence", Tally = "4" },
        new() { Name = "Songs", Tally = "412" },
        new() { Name = "   5 · Killer Queen", Tally = "22" },
        new() { Name = "   6 · Somebody to Love", Tally = "19" },
        new() { Name = "   7 · Bohemian Rhapsody", Tally = "28" },
        new() { Name = "   8 · Love of My Life", Tally = "17" },
        new() { Name = "   9 · Under Pressure", Tally = "24" },
        new() { Name = "Interval", Tally = "3" },
    ];

    // ── screen 04 · fade curves ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Thumbnail geometries in a 44 × 26 box, drawn top-left (unity) to bottom-right (silence) so a
    /// fade-out reads as a descent. The same five appear on every curve picker in the app.
    /// </summary>
    public static IReadOnlyList<CurveOption> FadeCurves { get; } =
    [
        new("linear", "M2,2 L42,24"),
        new("eq-power", "M2,2 Q30,4 42,24"),
        new("expo", "M2,2 Q10,22 42,24"),
        new("s-curve", "M2,2 C18,2 26,24 42,24"),
        new("custom ✎", "M2,2 L14,6 L22,18 L32,12 L42,24"),
    ];

    /// <summary>The custom curve open in the editor, drawn in a 100 × 40 box.</summary>
    public static Geometry CustomCurve { get; } = Geometry.Parse("M2,3 L28,8 L48,28 L70,20 L98,38");

    /// <summary>
    /// Its draggable points, as fractions of the editor canvas. Point 3 is the one the numeric fields
    /// beside the canvas are editing, which is why it reads amber — the selected point and the fields
    /// must be unmistakably the same thing.
    /// </summary>
    public static IReadOnlyList<CurvePoint> CustomCurvePoints { get; } =
    [
        new(0.02, 0.075), new(0.28, 0.20), new(0.48, 0.70, IsSelected: true),
        new(0.70, 0.50), new(0.98, 0.95),
    ];

    // ── screen 04 · per-cue sends ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<MatrixColumn> SendColumns { get; } =
    [
        new("Main L", IsGrouped: true), new("Main R", IsGrouped: true),
        new("Fold L"), new("Fold R"), new("Sub"),
    ];

    public static IReadOnlyList<MatrixRow> SendRows { get; } =
    [
        new("Src L", [MatrixCell.Unity, MatrixCell.Empty, MatrixCell.Gain("−6.0"), MatrixCell.Empty, MatrixCell.Empty]),
        new("Src R", [MatrixCell.Empty, MatrixCell.Gain("−3.0", picked: true), MatrixCell.Empty, MatrixCell.Gain("−6.0"), MatrixCell.Empty]),
    ];

    // ── screen 05 · timeline ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> TimelineRuler { get; } =
        ["0:00", "0:15", "0:30", "0:45", "1:00", "1:15", "1:30", "1:45", "2:00"];

    public static IReadOnlyList<TimelineLane> TimelineLanes { get; } =
    [
        new()
        {
            Name = "▾ 13.1 · Storm bed",
            Clips = [new() { Label = "storm-bed.flac · −3.0 dB", Left = 0, Width = 1.0, Kind = "au" }],
        },
        new()
        {
            Name = "fx · volume", IsEffect = true,
            Envelope = Env((0, 20), (8, 6), (55, 6), (70, 14), (88, 14), (100, 20)),
        },
        new()
        {
            Name = "▾ 13.2 · Projection · rain",
            Clips = [new() { Label = "rain-loop.mov", Left = 0.11, Width = 0.37, Kind = "vi" }],
        },
        new()
        {
            Name = "fx · opacity", IsEffect = true,
            Envelope = Env((11, 20), (16, 4), (40, 4), (48, 20)),
        },
        new()
        {
            Name = "13.3 · Thunder crack · disabled",
            Clips = [new() { Label = "thunder", Left = 0.46, Width = 0.05, Kind = "au", IsDisabled = true }],
        },
        new()
        {
            Name = "▸ 13.5 · Lightning sequence · 6 cues", IsGroup = true,
            Clips = [new() { Label = "collapsed group · drags as one · fx apply to all", Left = 0.44, Width = 0.33, Kind = "gr" }],
        },
    ];

    public const double TimelinePlayhead = 0.41;

    /// <summary>
    /// Normalises the mockup's envelope points (a 100 × 22 SVG box) into lane fractions, so the lane
    /// keeps the mockup's exact shape while the control stays pixel-free.
    /// </summary>
    private static IReadOnlyList<CurvePoint> Env(params (double X, double Y)[] points)
        => [.. points.Select(p => new CurvePoint(p.X / 100.0, p.Y / 22.0))];

    // ── screen 06 · logical outputs ───────────────────────────────────────────────────────────

    public static IReadOnlyList<LogicalOutputRow> LogicalOutputs { get; } =
    [
        new()
        {
            Name = "Main L", Group = "Main", FedBy = new("6 cues"),
            PatchedTo = new("18i20·1 + NDI·1 + Rec·1"), MeterBars = 5,
        },
        new()
        {
            Name = "Main R", Group = "Main", FedBy = new("6 cues"),
            PatchedTo = new("18i20·2 + NDI·2 + Rec·2"), MeterBars = 4,
        },
        new()
        {
            Name = "Foldback L", Group = "Fold", FedBy = new("4 cues"),
            PatchedTo = new("18i20·3"), MeterBars = 2,
        },
        new()
        {
            Name = "Foldback R", Group = "Fold", FedBy = new("4 cues"),
            PatchedTo = new("18i20·4"), MeterBars = 2,
        },
        new()
        {
            Name = "Sub", FedBy = new("2 cues"), PatchedTo = new("18i20·7"),
            MeterBars = 7, MeterGel = Gel.Red,
        },
        new()
        {
            Name = "Stage cue L", Group = "Stage", FedBy = new("1 cue"),
            PatchedTo = new("Wedge·1 (absent)", Gel.Amber),
        },
        new()
        {
            Name = "Stage cue R", Group = "Stage", FedBy = new("1 cue"),
            PatchedTo = new("Wedge·2 (absent)", Gel.Amber),
        },
        new() { Name = "FX return", FedBy = new("3 cues"), PatchedTo = new("18i20·5") },
        // Patched to hardware, fed by nothing — a dead channel wasting an output.
        new()
        {
            Name = "Orchestra", NameGel = Gel.Amber, FedBy = new("0 cues", Gel.Amber),
            PatchedTo = new("18i20·8"),
        },
        // Fed by cues, patched to nothing — sound that would silently vanish. HaPlay could not even
        // express this state; here it is first-class, visible and deliberate.
        new()
        {
            Name = "Lobby", NameGel = Gel.Red, Group = "Lobby", FedBy = new("2 cues"),
            PatchedTo = new("nothing — silent", Gel.Red),
        },
    ];

    public static IReadOnlyList<string> LobbySenders { get; } =
    [
        "Q12 Preshow bed · L→L R→R · −10.0 dB",
        "Q41 Interval walk-out · L→L R→R · −10.0 dB",
    ];

    // ── screen 07 · patch matrix ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<MatrixColumn> PatchColumns { get; } =
    [
        new("Main L", IsGrouped: true), new("Main R", IsGrouped: true),
        new("Fold L"), new("Fold R"), new("Sub"), new("Stg L"), new("Stg R"),
        new("FX"), new("Orch"), new("Lby L"), new("Lby R"),
    ];

    public static IReadOnlyList<MatrixRow> PatchRows { get; } =
    [
        PatchRow("18i20 · Out 1", (0, MatrixCell.Unity)),
        PatchRow("18i20 · Out 2", (1, MatrixCell.Unity)),
        PatchRow("18i20 · Out 3", (2, MatrixCell.Gain("−3.0"))),
        PatchRow("18i20 · Out 4", (3, MatrixCell.Gain("−3.0"))),
        PatchRow("18i20 · Out 5", (7, MatrixCell.Gain("−6.0"))),
        PatchRow("18i20 · Out 7", (4, MatrixCell.Gain("+2.0"))),
        PatchRow("18i20 · Out 8", (8, MatrixCell.Unity)),
        PatchRow("NDI Prog · 1", (0, MatrixCell.Gain("−6.0"))),
        PatchRow("NDI Prog · 2", (1, MatrixCell.Gain("−6.0"))),
        PatchRow("Record · 1", (0, MatrixCell.Unity)),
        PatchRow("Record · 2", (1, MatrixCell.Unity)),
        // The absent interface keeps its cells: an absent device is a machine fact, and deleting the
        // patch it carries would lose show data the moment someone unplugs a box.
        PatchRow("Wedge · 1", true, (5, MatrixCell.Mute)),
        PatchRow("Wedge · 2", true, (6, MatrixCell.Mute)),
    ];

    private static MatrixRow PatchRow(string header, params (int Column, MatrixCell Cell)[] set)
        => PatchRow(header, false, set);

    private static MatrixRow PatchRow(string header, bool absent, params (int Column, MatrixCell Cell)[] set)
    {
        var cells = new MatrixCell[PatchColumns.Count];
        Array.Fill(cells, MatrixCell.Empty);
        foreach (var (column, cell) in set)
            cells[column] = cell;
        return new MatrixRow(header, cells, absent);
    }

    public static IReadOnlyList<string> PatchSnapshots { get; } =
        ["▸ Preshow · full patch", "▸ Act 1 · fold + sub only · current", "▸ Interval · lobby only"];

    // ── screen 08 · devices ───────────────────────────────────────────────────────────────────

    public static IReadOnlyList<AudioLineRow> AudioLines { get; } =
    [
        new()
        {
            Name = "18i20", Kind = "PortAudio · Scarlett 18i20 (ALSA)", Channels = "8",
            Rate = new("48 000 native"), State = new("open · clock master", Gel.Green),
            Carries = "7 logical outs",
        },
        new()
        {
            Name = "NDI Prog", Kind = "NDI audio · “HACUE-PROG”", Channels = "2",
            Rate = new("48 000"), State = new("open · 2 receivers", Gel.Green), Carries = "Main pair",
        },
        new()
        {
            Name = "Record", Kind = "File · show-{date}.flac", Channels = "2",
            Rate = new("44 100 · resampled", Gel.Amber), State = new("armed"), Carries = "Main pair",
        },
        new()
        {
            Name = "Stream", Kind = "Live stream · RTMP ×1", Channels = "2",
            Rate = new("48 000"), State = new("idle"), Carries = "—",
        },
        new()
        {
            Name = "Wedge", NameGel = Gel.Red, Kind = "PortAudio · Behringer UCA222", Channels = "2",
            Rate = new("—"), State = new("absent on this machine", Gel.Red), Carries = "Stage pair",
        },
    ];

    public const string RecordPatternTokens = "{date} {time} {project} {list} {n} — preview: show-2026-08-01-3.flac";

    // ── screen 09/10 · video ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<VideoOutputRow> VideoOutputs { get; } =
    [
        new()
        {
            Name = "Projector A", Kind = "local · screen 2 · fullscreen", Shows = "Cyc",
            Map = "warp · 2 sect", State = new("live", Gel.Green),
        },
        new()
        {
            Name = "Lobby TV", Kind = "local · screen 3", Shows = "Cyc", Map = "clean",
            State = new("screen absent", Gel.Red),
        },
        new()
        {
            Name = "NDI Prog", Kind = "NDI · video+audio", Shows = "Cyc", Map = "clean",
            State = new("2 rx", Gel.Green),
        },
    ];

    public static IReadOnlyList<PlacementBox> CycLayers { get; } =
    [
        new() { Label = "Q13.2 rain-loop · L2", Left = 0.06, Top = 0.08, Width = 0.58, Height = 0.78 },
        new()
        {
            Label = "Q15.5 visualizer cue · L1", Left = 0.60, Top = 0.40, Width = 0.36, Height = 0.55,
            IsSecondary = true, IsSelected = true,
        },
    ];

    public static IReadOnlyList<PlacementBox> PortalLayers { get; } =
    [
        new() { Label = "idle image · logo.png", Left = 0.20, Top = 0.12, Width = 0.60, Height = 0.74 },
    ];

    public static IReadOnlyList<PlacementBox> MappingSource { get; } =
    [
        new() { Label = "1 · Left wall", Left = 0.02, Top = 0.06, Width = 0.47, Height = 0.86, IsSelected = true },
        new() { Label = "2 · Right wall", Left = 0.45, Top = 0.06, Width = 0.52, Height = 0.86, IsSecondary = true },
    ];

    public static IReadOnlyList<PlacementBox> MappingOutput { get; } =
    [
        new() { Label = "1 · warp 3×3", Left = 0.03, Top = 0.10, Width = 0.44, Height = 0.80, IsSelected = true },
        new() { Label = "2", Left = 0.52, Top = 0.08, Width = 0.44, Height = 0.84, IsSecondary = true },
    ];

    public static IReadOnlyList<string> MappingSections { get; } =
        ["▸ 1 · Left wall · warp 3×3", "▸ 2 · Right wall"];

    // ── screen 11 · targets ───────────────────────────────────────────────────────────────────

    public static IReadOnlyList<TriggerSourceRow> TriggerSources { get; } =
    [
        new()
        {
            Name = "APC mini", Kind = "MIDI in", Bindings = "9 cues", LastSeen = "note 3 ch 1 · 14:01",
            State = new("open", Gel.Green),
        },
        new()
        {
            Name = "QLab bridge", Kind = "OSC in · :9000", Bindings = "2 cues",
            LastSeen = "/hacue/go · 13:44", State = new("listening", Gel.Green),
        },
        new() { Name = "Hotkeys", Kind = "keyboard", Bindings = "6 cues", State = new("always") },
    ];

    public static IReadOnlyList<BindingRow> ApcBindings { get; } =
    [
        new("note 3 · ch 1", "Q16 Loop to 12 if held", "vel ≥ 1 · no-repeat 250 ms"),
        new("note 4 · ch 1", "Q20 Blackout all", "—"),
        // Register item 24: continuous-controller bindings to parameters are v1, not just note→cue.
        new("cc 48 · ch 1", "master trim (ride)", "0–127 → −60..0 dB"),
    ];

    public static IReadOnlyList<LogLine> TriggerMonitor { get; } =
    [
        new("14:01:22", "MIDI in", "", "APC mini · note-on 3 ch 1 vel 127 → Q16", Gel.Congo),
        new("13:58:41", "OSC in", "", ":9000 · /hacue/go → GO", Gel.Steel),
        new("13:58:12", "key", "", "Space → GO"),
    ];

    public static IReadOnlyList<TriggerSourceRow> ActionEndpoints { get; } =
    [
        new()
        {
            Name = "Eos", Kind = "OSC out · 10.0.1.20:8000", Bindings = "31 cues",
            LastSeen = "/eos/cue/7.2 · 14:01", State = new("reachable", Gel.Green),
        },
        new()
        {
            Name = "X32", Kind = "OSC out · 10.0.1.30:10023", Bindings = "6 cues",
            LastSeen = "/ch/01/mix/fader · 13:52", State = new("reachable", Gel.Green),
        },
        new()
        {
            Name = "Hog wing", Kind = "MIDI out · port 1", Bindings = "0 cues",
            LastSeen = "—", State = new("unused", Gel.Amber),
        },
    ];

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

    // ── screens 12/13 · settings ──────────────────────────────────────────────────────────────

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

    public static IReadOnlyList<SettingsPane> ProjectPanes { get; } =
    [
        new() { Name = "Show behaviour" },
        new() { Name = "Authoring defaults" },
        new() { Name = "Overrides", Tally = "2 active" },
        new() { Name = "Save, autosave & recovery" },
        new() { Name = "Project status", Tally = "2 errors", TallyGel = Gel.Red },
    ];

    public static IReadOnlyList<OverrideRow> Overrides { get; } =
    [
        new("Panic fade", "0.15 s", "0.25 s"),
        new("Remote API", "off", "on · port 8420"),
    ];

    // ── screen 14 · project status ────────────────────────────────────────────────────────────

    public static IReadOnlyList<CheckRow> Checks { get; } =
    [
        new()
        {
            Check = "Media files", Result = new("1 missing", Gel.Red),
            Detail = "Q15 interval.wav — last seen /media/usb0", Fix = "Relink ›",
        },
        new()
        {
            Check = "Logical outputs patched", Result = new("1 unpatched", Gel.Red),
            Detail = "Lobby fed by 2 cues, no device receives it", Fix = "Patch ›",
        },
        new()
        {
            Check = "Logical outputs fed", Result = new("1 unfed", Gel.Amber),
            Detail = "Orchestra patched but no cue sends to it", Fix = "Show ›",
        },
        new()
        {
            Check = "Audio devices", Result = new("1 absent", Gel.Amber),
            Detail = "Wedge (UCA222) — Stage pair silent", Fix = "Relink ›",
        },
        new() { Check = "Clock master", Result = new("ok", Gel.Green), Detail = "18i20 native 48 000" },
        new()
        {
            Check = "Jump / fade targets", Result = new("ok", Gel.Green),
            Detail = "31 references, all resolve",
        },
        new()
        {
            Check = "Action endpoints", Result = new("ok", Gel.Green),
            Detail = "Eos + X32 reachable · Hog wing unused by any enabled cue",
        },
        new() { Check = "Schedules", Result = new("ok", Gel.Green), Detail = "none in the past" },
        new()
        {
            Check = "Video outputs", Result = new("ok", Gel.Green),
            Detail = "screens present · Lobby TV absent but not marked required",
        },
    ];

    // ── screen 15 · diagnostics ───────────────────────────────────────────────────────────────

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
            Name = "lease · Q9 walk-in (fading)", State = new("releasing", Gel.Amber), InFlight = "2",
            Capacity = "8", Enqueued = "412 800", Processed = "412 798", Dropped = new("0"),
            Latency = new("40.0 ms"), Epoch = "7", IsLease = true,
        },
        new()
        {
            Name = "monitor · audition", State = new("idle"), Dropped = new("—"), Latency = new("—"),
            IsLease = true,
        },
    ];

    public static IReadOnlyList<CompositionStatsRow> CompositionStats { get; } =
    [
        new()
        {
            Name = "Cyc · 1920×1080", Fps = new("29.97", Gel.Green), Layers = "2",
            Late = new("0"), Dropped = "0",
        },
        new()
        {
            Name = "Portal · 1280×720", Fps = new("28.4", Gel.Amber), Layers = "1",
            Late = new("6", Gel.Amber), Dropped = "0",
        },
    ];

    public static IReadOnlyList<LogLine> LogTail { get; } =
    [
        new("14:02:11", "WARN", "S.Media.Routing", "OutputPump Record: Submit took 118 ms", Gel.Amber),
        new("13:58:40", "ERROR", "HaCue2.Audio", "logical output Lobby unpatched — 2 senders routed silent", Gel.Red),
        new("13:58:40", "WARN", "HaCue2.Status", "project status: 2 errors, 2 warnings", Gel.Amber),
        new("13:44:02", "INFO", "S.Media.Session", "clock master 18i20 epoch 7 · 48 000 native"),
    ];
}
