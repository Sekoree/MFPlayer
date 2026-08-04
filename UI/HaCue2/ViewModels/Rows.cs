using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaCue2.ViewModels;

/// <summary>
/// The presentation shapes every repeating surface in HaCue2 binds to.
/// </summary>
/// <remarks>
/// They are immutable records rather than observable objects on purpose. A row in this shell never
/// changes after it is created — a row is built from the document or the runtime and replaced, never mutated — and making them
/// observable now would invent a change-notification design before there is a document to notify about.
/// When the engine lands, the rows that genuinely tick (active cues, meters, diagnostics counters)
/// become observables and the rest stay exactly as they are.
/// </remarks>
public enum CueKind
{
    Media,
    Video,
    Group,
    Action,
    Fade,
    Jump,
    Visualizer,
    Patch,
    Comment,
}

/// <summary>Colour role for a badge or a status word, named after the mockup's gels.</summary>
public enum Gel
{
    Neutral,
    Congo,
    Amber,
    Green,
    Red,
    Steel,
}

/// <summary>
/// One tab in a view's section strip: a stable key, and a label that may carry a live count.
/// </summary>
/// <remarks>
/// <para>
/// The key exists so the label can change. The strips used to be lists of STRINGS which doubled as the
/// tabs' identity — "DEVICES · 3" was both what the tab said and how the view-model recognised which
/// pane was open — so the counts had to be frozen at construction or selecting a tab whose label had
/// been rewritten would blank the pane. They were frozen, and a project with eight devices spent the
/// evening insisting it had three.
/// </para>
/// <para>
/// Observable, unlike the other rows in this file, because it is a row that genuinely ticks: adding a
/// composition has to be visible in the tab strip without rebuilding the strip under the operator's
/// selection.
/// </para>
/// </remarks>
public sealed partial class SectionTab(string key, string label) : ObservableObject
{
    /// <summary>What the view-model matches on. Never shown, never changes.</summary>
    public string Key { get; } = key;

    [ObservableProperty]
    private string _label = label;

    /// <summary>Re-labels the tab with its count, e.g. <c>DEVICES · 8</c>.</summary>
    public void Count(string caption, int count) => Label = $"{caption} · {count}";
}

/// <summary>A tag on a cue row: "loop", "OSC", "offline", "timeline".</summary>
public sealed record Badge(string Text, Gel Gel = Gel.Neutral)
{
    public bool IsMidi => Gel == Gel.Congo;
    public bool IsOsc => Gel == Gel.Steel;
    public bool IsClock => Gel == Gel.Amber;
    public bool IsBad => Gel == Gel.Red;
    public bool IsGood => Gel == Gel.Green;
}

/// <summary>One row of the cue tree (screens 02 and 03).</summary>
public sealed record CueRow
{
    /// <summary>The document id this row stands for; how a selection names an editable thing.</summary>
    public Guid Id { get; init; }

    public required string Number { get; init; }
    public required string Label { get; init; }
    public CueKind Kind { get; init; } = CueKind.Media;
    public string Source { get; init; } = "";
    public string Fade { get; init; } = "—";
    public string Length { get; init; } = "—";
    public string Level { get; init; } = "—";
    public IReadOnlyList<Badge> Badges { get; init; } = [];

    /// <summary>The Note tab's content — one tab on every kind, and the whole of a comment cue.</summary>
    public string Note { get; init; } = "";

    /// <summary>Indent level. The TreeDataGrid indents from the hierarchy itself; this is kept for
    /// anything that still reads a row out of tree context.</summary>
    public int Depth { get; init; }

    /// <summary>A group's cues. Empty for everything else — this is what makes the tree a tree.</summary>
    public IReadOnlyList<CueRow> Children { get; init; } = [];

    /// <summary>
    /// Whether this row gets an expander.
    /// </summary>
    /// <remarks>
    /// A GROUP with no cues in it still gets one, deliberately: an empty group is a thing an operator
    /// made and needs to see is empty, and a row with no chevron reads as an ordinary cue — which is
    /// exactly the confusion the tree was adopted to end.
    /// </remarks>
    public bool HasChildren => IsGroup;

    /// <summary>Groups open by default: a collapsed show hides the cues somebody came here to read.</summary>
    public bool IsExpanded { get; set; } = true;

    public bool IsRunning { get; init; }
    public bool IsStandby { get; init; }
    public bool IsBroken { get; init; }

    /// <summary>Register item: a cue skipped for this performance, still visible and struck through.</summary>
    public bool IsDisabled { get; init; }

    public bool IsGroup => Kind == CueKind.Group;

    /// <summary>
    /// The wash behind the row. Most urgent state wins, exactly as the stripe does.
    /// </summary>
    /// <remarks>
    /// Group last: a running cue inside a group must read as running, not as a group member.
    /// </remarks>
    public RowWash Wash => IsRunning
        ? RowWash.Running
        : IsStandby
            ? RowWash.Standby
            : IsGroup
                ? RowWash.Group
                : RowWash.None;

    /// <summary>
    /// The kind marker in the tree's glyph column. The mockup uses ◈ for both patch cues and OSC
    /// action cues on different screens; they are disambiguated here (◈ action, ◇ patch) because a
    /// glyph column earns its width only if each glyph means one thing.
    /// </summary>
    public string Glyph => Kind switch
    {
        CueKind.Media => "▶",
        CueKind.Video => "▦",
        CueKind.Group => "▤",
        CueKind.Action => "◈",
        CueKind.Fade => "◆",
        CueKind.Jump => "⤳",
        CueKind.Visualizer => "✷",
        CueKind.Patch => "◇",
        CueKind.Comment => "※",
        _ => "·",
    };

    /// <summary>Left padding of the number column, in the mockup's 20 px-per-level steps.</summary>
    public Thickness NumberIndent => new(6 + (Depth * 9), 0, 0, 0);
}

/// <summary>One row of the Active panel — everything sounding, in or out of the current scope.</summary>
public sealed record ActiveCueRow
{
    /// <summary>
    /// Which cue this row is. Needed because the row is a TARGET, not just a readout — bare STOP acts
    /// on it, and a row that only carried its number could not be turned back into a cue to stop.
    /// </summary>
    public Guid CueId { get; init; }

    public required string Number { get; init; }
    public required string Label { get; init; }

    /// <summary>List name or group progress, shown after the label in ink-3 ("Preshow", "3 of 28").</summary>
    public string Qualifier { get; init; } = "";

    /// <summary>Where the playhead is, as the transport reports it — not wall time since the fire.</summary>
    public string Clock { get; init; } = "";

    /// <summary>Counting DOWN, with its minus sign. Empty when nothing knows how long the clip runs.</summary>
    public string Remaining { get; init; } = "";

    /// <summary>The clip's whole length, for the readout beside the bar.</summary>
    public string Length { get; init; } = "";

    /// <summary>The playhead and the length as VALUES, which is what a seek is computed against.</summary>
    public TimeSpan Position { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Whether the bar can be dragged: a cue whose length nobody knows cannot be seeked.</summary>
    public bool CanSeek => Duration is { TotalMilliseconds: > 0 };

    public double Progress { get; init; }
    public string Destination { get; init; } = "—";
    public bool IsGroup { get; init; }
    public bool IsChild { get; init; }
    public bool IsFading { get; init; }

    /// <summary>Within seconds of the end — the clock turns red before it becomes a problem.</summary>
    public bool IsNearEnd { get; init; }

    public bool HasQualifier => Qualifier.Length > 0;
    public Thickness NumberIndent => new(IsChild ? 18 : 8, 0, 0, 0);
}

/// <summary>A generic status word plus its gel — used across every table's result column.</summary>
public sealed record Status(string Text, Gel Gel = Gel.Neutral)
{
    public bool IsGood => Gel == Gel.Green;
    public bool IsWarn => Gel == Gel.Amber;
    public bool IsBad => Gel == Gel.Red;
    public bool IsInfo => Gel == Gel.Steel;
}

/// <summary>Screen 06 — a project-owned audio channel; the only destination a cue can name.</summary>
public sealed record LogicalOutputRow
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Named Output Group (register item 9); a stereo pair is a two-member group.</summary>
    public string Group { get; init; } = "—";

    public required Status FedBy { get; init; }
    public required Status PatchedTo { get; init; }

    /// <summary>Bar glyphs, 0–7. Zero means "no signal", which is not the same as "no telemetry".</summary>
    public int MeterBars { get; init; }

    public Gel MeterGel { get; init; } = Gel.Green;
    public Gel NameGel { get; init; } = Gel.Neutral;
    public bool HasGroup => Group != "—";
    public bool IsMeterHot => MeterGel == Gel.Red;
    public string MeterGlyphs => MeterBars == 0 ? "—" : new string('▮', MeterBars);
}

/// <summary>One cell of the patch or per-cue send matrix.</summary>
/// <remarks>
/// Every cell carries its own <see cref="Row"/> and <see cref="Column"/>. Without them a pointer
/// gesture would have to find the cell in the bound lists, and since this is a record with structural
/// equality, two empty cells in a row are indistinguishable — a click on the last column would edit
/// the first. The coordinates make the lookup unnecessary as well as unambiguous.
/// </remarks>
public sealed record MatrixCell
{
    public int Row { get; init; }
    public int Column { get; init; }

    public string Text { get; init; } = "";
    public bool IsOn { get; init; }
    public bool IsUnity { get; init; }
    public bool IsMuted { get; init; }

    /// <summary>The cell the effective-route strip underneath is currently explaining.</summary>
    public bool IsPicked { get; init; }

    /// <summary>Not routed — which is not the same as muted, and is why this is a distinct state.</summary>
    public bool IsEmpty => !IsOn && !IsMuted;

    public static MatrixCell Empty(int row, int column) => new() { Row = row, Column = column };

    public static MatrixCell Unity(int row, int column) =>
        new() { Row = row, Column = column, Text = "0.0", IsOn = true, IsUnity = true };

    public static MatrixCell Gain(int row, int column, string db, bool picked = false) =>
        new() { Row = row, Column = column, Text = db, IsOn = true, IsPicked = picked };

    public static MatrixCell Mute(int row, int column) =>
        new() { Row = row, Column = column, Text = "off", IsMuted = true };
}

/// <summary>A matrix row: a label plus its cells, one per column.</summary>
/// <param name="LineId">The device line this row is a channel of; empty for a cue's send rows.</param>
/// <param name="LineChannel">That line's channel, or the cue's source channel.</param>
public sealed record MatrixRow(
    string Header,
    IReadOnlyList<MatrixCell> Cells,
    bool IsAbsent = false,
    Guid LineId = default,
    int LineChannel = 0);

/// <summary>A matrix column head; <paramref name="IsGrouped"/> marks Output-Group membership.</summary>
/// <param name="ChannelId">The logical output this column is, so a gesture can name it.</param>
public sealed record MatrixColumn(string Header, bool IsGrouped = false, Guid ChannelId = default);

/// <summary>Screen 08 — a machine-side audio line, project-owned and possibly absent here.</summary>
public sealed record AudioLineRow
{
    public Guid Id { get; init; }

    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string Channels { get; init; } = "—";
    public required Status Rate { get; init; }
    public required Status State { get; init; }
    public string Carries { get; init; } = "—";
    public Gel NameGel { get; init; } = Gel.Neutral;
}

/// <summary>Screen 09 — a video output and what it currently shows.</summary>
public sealed record VideoOutputRow
{
    public Guid Id { get; init; }

    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string Shows { get; init; } = "—";
    public string Map { get; init; } = "clean";
    public required Status State { get; init; }
}

/// <summary>A layer or mapping section drawn on a canvas, positioned in fractions of the frame.</summary>
public sealed record PlacementBox
{
    /// <summary>The cue or mapping section this box is — how a drag names what it moved.</summary>
    public Guid SubjectId { get; init; }

    /// <summary>Stacking order on the canvas. Mapping sections all sit at 0; only layers stack.</summary>
    public int LayerIndex { get; init; }

    public required string Label { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>Steel (a) or congo (b) — the mockup alternates so overlapping boxes stay separable.</summary>
    public bool IsSecondary { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>
    /// A mapping section that is switched off: drawn, but as an outline.
    /// </summary>
    /// <remarks>
    /// Drawn rather than hidden, because it still HAS geometry the operator is arranging. A disabled
    /// section that vanished from the canvas would be a section they could no longer drag back into
    /// place, and the only way to find it again would be to switch it on in front of the audience.
    /// </remarks>
    public bool IsDisabled { get; init; }
}

/// <summary>Screen 11 — an inbound trigger source.</summary>
public sealed record TriggerSourceRow
{
    public Guid Id { get; init; }

    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string Bindings { get; init; } = "—";
    public string LastSeen { get; init; } = "—";
    public required Status State { get; init; }
}

/// <summary>Screen 11 — one input-to-cue binding on the selected device.</summary>
public sealed record BindingRow(string Input, string Fires, string Filter);

/// <summary>Screen 11b — a remote API route with its live call count.</summary>
public sealed record EndpointRow(string Method, string Path, string Does, string Calls);

/// <summary>Screen 14 — one project-status check.</summary>
public sealed record CheckRow
{
    public required string Check { get; init; }
    public required Status Result { get; init; }
    public string Detail { get; init; } = "";
    public string Fix { get; init; } = "";
    public bool HasFix => Fix.Length > 0;
}

/// <summary>Screen 15 — one audio-bay terminal or lease.</summary>
public sealed record BayRow
{
    public required string Name { get; init; }
    public required Status State { get; init; }
    public string InFlight { get; init; } = "—";
    public string Capacity { get; init; } = "—";
    public string Enqueued { get; init; } = "—";
    public string Processed { get; init; } = "—";
    public required Status Dropped { get; init; }
    public required Status Latency { get; init; }
    public string Epoch { get; init; } = "—";
    public string Rate { get; init; } = "—";

    /// <summary>
    /// A lease row, indented under the terminals. Leases are listed BESIDE terminals rather than
    /// nested under one because in this topology every lease feeds the single program bus, which then
    /// fans out to all terminals — nesting would draw a parent relationship that does not exist.
    /// </summary>
    public bool IsLease { get; init; }

    public Thickness NameIndent => new(IsLease ? 14 : 0, 0, 0, 0);
}

/// <summary>Screen 15 — one composition's render telemetry.</summary>
public sealed record CompositionStatsRow
{
    public required string Name { get; init; }
    public required Status Fps { get; init; }
    public string Layers { get; init; } = "0";
    public required Status Late { get; init; }
    public string Dropped { get; init; } = "0";
    public string Gpu { get; init; } = "GL";
}

/// <summary>One line of the log tail (screen 15) or a wire monitor (screen 11).</summary>
public sealed record LogLine(string Time, string Level, string Category, string Message, Gel Gel = Gel.Neutral)
{
    public bool IsWarn => Gel == Gel.Amber;
    public bool IsError => Gel == Gel.Red;
    public bool IsInfo => Gel == Gel.Steel;
    public bool IsAccent => Gel == Gel.Congo;
}

/// <summary>Screen 01 — a recent project, with the reasons it may not open.</summary>
public sealed record RecentProjectRow
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Contents { get; init; } = "—";
    public string Opened { get; init; } = "";
    public bool IsMissing { get; init; }
    public bool IsCurrent { get; init; }
}

/// <summary>Screen 13 — one app setting this project defeats.</summary>
public sealed record OverrideRow(string Setting, string AppValue, string ProjectValue);

/// <summary>Screen 12 — one row of the hotkey grid: a command, its gesture, and any project override.</summary>
public sealed record HotkeyRow(string Command, string Gesture, string Group, string ProjectOverride)
{
    public bool HasOverride => ProjectOverride.Length > 0;
    public string OverrideText => HasOverride ? ProjectOverride : "—";
}

/// <summary>Screen 02b — a line chip in the Output info drawer.</summary>
public sealed record OutputLineChip
{
    public required string Name { get; init; }
    public string Suffix { get; init; } = "";
    public required string Detail { get; init; }
    public Gel Gel { get; init; } = Gel.Green;
    public bool IsOk => Gel == Gel.Green;
    public bool IsWarn => Gel == Gel.Amber;
    public bool IsError => Gel == Gel.Red;
    public bool IsIdle => Gel == Gel.Neutral;
    public bool HasSuffix => Suffix.Length > 0;
}

/// <summary>Screen 02b — one program meter in the drawer.</summary>
public sealed record ProgramMeter(string Caption, double Level, double Peak, bool IsClipping = false);

/// <summary>A timeline lane and the clips on it (screen 05).</summary>
public sealed record TimelineLane
{
    public required string Name { get; init; }
    public IReadOnlyList<TimelineClip> Clips { get; init; } = [];

    /// <summary>Only clip lanes are draggable; an effect lane's points are edited in the curve editor.</summary>
    public bool IsEditable => !IsEffect;

    /// <summary>An effect lane (volume / opacity / OSC ramp), drawn shorter and indented.</summary>
    public bool IsEffect { get; init; }

    public bool IsGroup { get; init; }

    /// <summary>
    /// The lane's envelope as fractions of the lane; empty on a clip lane.
    /// </summary>
    /// <remarks>
    /// Register item 18 makes this ONE concept with two editors: the same points are edited here and
    /// in the inspector, replacing the media cue's separate VolumeEnvelope. Fractions rather than a
    /// <see cref="Geometry"/> because a stretched geometry is scaled by its own bounding box — see
    /// <see cref="Controls.EnvelopeGraph"/> for why that renders a plausible lie.
    /// </remarks>
    public IReadOnlyList<CurvePoint> Envelope { get; init; } = [];

    public bool HasEnvelope => Envelope.Count > 1;
}

/// <summary>A clip drawn on a timeline lane, positioned in fractions of the visible range.</summary>
public sealed record TimelineClip
{
    /// <summary>The cue this clip draws — how a drag names what it moved.</summary>
    public Guid SubjectId { get; init; }

    public required string Label { get; init; }
    public double Left { get; init; }
    public double Width { get; init; }

    /// <summary>au / vi / ac / gr — the mockup's clip colour classes.</summary>
    public string Kind { get; init; } = "au";

    public bool IsDisabled { get; init; }
    public bool IsAudio => Kind == "au" && !IsDisabled;
    public bool IsVideo => Kind == "vi" && !IsDisabled;
    public bool IsAction => Kind == "ac" && !IsDisabled;
    public bool IsGroup => Kind == "gr" && !IsDisabled;
}

/// <summary>A fade curve option in the picker (screen 04), including the drawn thumbnail.</summary>
/// <remarks>
/// The geometry is parsed once here rather than bound as a string: Avalonia will not convert a bound
/// <see cref="string"/> to a <see cref="Media.Geometry"/> the way it converts a literal XAML attribute,
/// so a string-typed property would silently render nothing.
/// </remarks>
public sealed record CurveOption(string Name, string PathData)
{
    public Geometry Shape { get; } = Geometry.Parse(PathData);
}

/// <summary>A draggable point on the custom-curve editor, in fractions of the canvas.</summary>
/// <param name="IsHold">Flat until the next point. Drawn as a square rather than a circle.</param>
public sealed record CurvePoint(double X, double Y, bool IsSelected = false, bool IsHold = false);

/// <summary>
/// One source-to-hardware route, as the inspector's chain draws it.
/// </summary>
/// <param name="Gain">The COMPOSED gain — the cue's send plus the patch cell, which is what is heard.</param>
public sealed record RouteHop(string Source, string Logical, string Line, string Gain, bool IsMuted);

/// <summary>A named settings pane in the nav (screens 12/13).</summary>
public sealed record SettingsPane : INavRow
{
    public required string Name { get; init; }
    public string Tally { get; init; } = "";
    public Gel TallyGel { get; init; } = Gel.Neutral;
    public bool HasTally => Tally.Length > 0;
    public bool TallyIsBad => TallyGel == Gel.Red;
    public bool TallyIsOverride => TallyGel == Gel.Amber;

    /// <summary>Settings panes are a flat list; only the scope picker nests.</summary>
    public Thickness Indent => default;
}

/// <summary>
/// A row in a navigation list: a destination, with a count beside it.
/// </summary>
/// <remarks>
/// The scope picker (screen 03) and both settings navs (screens 12/13) share one DataTemplate because
/// they are the same control. Sharing it needs a shared TYPE: a compiled binding whose x:DataType does
/// not match the item silently resolves to nothing, and the row falls back to ToString() — which is
/// exactly what the scope picker was doing, printing "ScopeEntry { Id = … }" down the side of the app.
/// </remarks>
public interface INavRow
{
    string Name { get; }
    string Tally { get; }
    bool HasTally { get; }
    bool TallyIsBad { get; }
    bool TallyIsOverride { get; }

    /// <summary>Nesting, so a group inside a group sits under it.</summary>
    Thickness Indent { get; }
}

/// <summary>Which wash sits behind a cue row. Mapped to a token by RowWashBrushConverter.</summary>
public enum RowWash
{
    None,
    Running,
    Standby,
    Group,
}
