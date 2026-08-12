using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using S.Media.Session;

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
    Text,
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

    /// <summary>The colour band's palette index; 0 is untagged.</summary>
    public int ColorTag { get; init; }

    /// <summary>The band itself, resolved once here rather than by a converter per row per repaint.</summary>
    public IBrush ColorBrush => HaCue2.Presentation.CueColors.Brush(ColorTag);

    public bool HasColorTag => ColorTag > 0;
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
        CueKind.Text => "T",
        _ => "·",
    };

    /// <summary>Left padding of the number column, in the mockup's 20 px-per-level steps.</summary>
    public Thickness NumberIndent => new(6 + (Depth * 9), 0, 0, 0);
}

/// <summary>One row of the Active panel — everything sounding, in or out of the current scope.</summary>
/// <remarks>
/// Fully observable, because the row object PERSISTS across engine polls: the panel reconciles rows
/// in place rather than replacing them, so the seek bar an operator is dragging and the button under
/// their pointer stay the same controls from poll to poll. The engine is polled at 4 Hz; a
/// smooth-clock timer extrapolates the millisecond digits between polls from <see cref="Position"/>
/// and <see cref="PolledAtTicks"/>, and each poll's <see cref="UpdateFrom"/> re-corrects.
/// </remarks>
public sealed partial class ActiveCueRow : ObservableObject
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
    [ObservableProperty]
    private string _clock = "";

    /// <summary>Counting DOWN, with its minus sign. Empty when nothing knows how long the clip runs.</summary>
    [ObservableProperty]
    private string _remaining = "";

    /// <summary>The clip's whole length, for the readout beside the bar.</summary>
    [ObservableProperty]
    private string _length = "";

    /// <summary>The playhead and the length as VALUES, which is what a seek is computed against.</summary>
    public TimeSpan Position { get; set; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> when <see cref="Position"/> was read, so the
    /// smooth-clock timer can extrapolate the playhead between engine polls.</summary>
    public long PolledAtTicks { get; set; }

    /// <summary>Whether the bar can be dragged: a cue whose length nobody knows cannot be seeked.</summary>
    public bool CanSeek => Duration is { TotalMilliseconds: > 0 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    private TimeSpan? _duration;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _destination = "—";

    public bool IsGroup { get; init; }
    public bool IsChild { get; init; }

    [ObservableProperty]
    private bool _isFading;

    /// <summary>Within seconds of the end — the clock turns red before it becomes a problem.</summary>
    [ObservableProperty]
    private bool _isNearEnd;

    public bool HasQualifier => Qualifier.Length > 0;
    public Thickness NumberIndent => new(IsChild ? 18 : 8, 0, 0, 0);

    /// <summary>Whether <paramref name="fresh"/> describes the same row shape, so an in-place update
    /// is enough. A changed identity or indentation replaces the row instead.</summary>
    public bool StructurallySame(ActiveCueRow fresh) =>
        CueId == fresh.CueId && IsChild == fresh.IsChild && IsGroup == fresh.IsGroup
        && Number == fresh.Number && Label == fresh.Label && Qualifier == fresh.Qualifier;

    /// <summary>Adopts one poll's measurements without replacing the row object.</summary>
    public void UpdateFrom(ActiveCueRow fresh)
    {
        Clock = fresh.Clock;
        Remaining = fresh.Remaining;
        Length = fresh.Length;
        Position = fresh.Position;
        PolledAtTicks = fresh.PolledAtTicks;
        Duration = fresh.Duration;
        Progress = fresh.Progress;
        Destination = fresh.Destination;
        IsFading = fresh.IsFading;
        IsNearEnd = fresh.IsNearEnd;
    }
}

/// <summary>
/// A group with something sounding inside it, as the Active panel's header row.
/// </summary>
/// <remarks>
/// Mutable and observable, unlike the flat rows: it is built in two passes — children collected, then
/// the aggregate computed over them — and its expander is operator state that must survive the 4 Hz
/// rebuild. <see cref="IsExpanded"/> starts TRUE, because a group that hid its children by default
/// would be a panel that shows less than the flat list it replaced.
/// </remarks>
public sealed partial class ActiveGroupRow : ObservableObject
{
    public Guid GroupId { get; init; }

    public required string Number { get; init; }
    public required string Label { get; init; }

    /// <summary>"playlist", "timeline" or "together" — what firing it did.</summary>
    public required string Mode { get; init; }

    /// <summary>The sounding children, in the order the flat panel would have listed them.
    /// Observable, because the header persists across polls and children join/leave under it.</summary>
    public ObservableCollection<ActiveCueRow> Children { get; } = [];

    /// <summary>The rest of the chain, each with how long until it starts.</summary>
    public ObservableCollection<UpcomingCueRow> Upcoming { get; } = [];

    /// <summary>The WHOLE group's remaining and total — what somebody waiting for the list wants.</summary>
    [ObservableProperty]
    private string _clock = "";

    /// <summary>"item 3/12", the position called out over talkback.</summary>
    [ObservableProperty]
    private string _position = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isNearEnd;

    /// <summary>Everything the group holds is ramping down — the header's feedback that its × took.</summary>
    [ObservableProperty]
    private bool _isFading;

    /// <summary>Whether the header's bar seeks. Only a together-group has one absolute time all its
    /// children can move to; a playlist's children play in sequence, so its header stays read-only.</summary>
    [ObservableProperty]
    private bool _canSeek;

    /// <summary>The aggregate as VALUES plus their poll stamp, so the smooth-clock timer can tick the
    /// header's milliseconds between engine polls. Zero total = unknown, and the timer leaves it alone.</summary>
    public TimeSpan TotalValue { get; set; }

    public TimeSpan RemainingAtPoll { get; set; }
    public long PolledAtTicks { get; set; }

    [ObservableProperty]
    private bool _hasUpcoming;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Adopts one poll's aggregates without replacing the header (the expander and every
    /// control inside the row survive). Children/Upcoming are reconciled separately by the caller.</summary>
    public void UpdateAggregatesFrom(ActiveGroupRow fresh)
    {
        Clock = fresh.Clock;
        Position = fresh.Position;
        Progress = fresh.Progress;
        IsNearEnd = fresh.IsNearEnd;
        IsFading = fresh.IsFading;
        CanSeek = fresh.CanSeek;
        TotalValue = fresh.TotalValue;
        RemainingAtPoll = fresh.RemainingAtPoll;
        PolledAtTicks = fresh.PolledAtTicks;
    }
}

/// <summary>One cue the group has not reached yet, and the countdown to it.</summary>
/// <remarks>
/// The countdown text is observable and STAGED: far from its start it reads (and updates) in whole
/// seconds on the 4 Hz poll, and inside the last ten seconds the smooth-clock timer takes over and
/// ticks its milliseconds — the closer the start, the more precision the readout carries.
/// </remarks>
public sealed partial class UpcomingCueRow : ObservableObject
{
    public required string Number { get; init; }
    public required string Label { get; init; }

    [ObservableProperty]
    private string _length = "";

    [ObservableProperty]
    private string _countdown = "";

    /// <summary>How far away the start was at poll time, plus the stamp to extrapolate from.</summary>
    public TimeSpan StartsInAtPoll { get; set; }

    public long PolledAtTicks { get; set; }

    /// <summary>Adopts one poll's countdown without replacing the row.</summary>
    public void UpdateFrom(UpcomingCueRow fresh)
    {
        Length = fresh.Length;
        Countdown = fresh.Countdown;
        StartsInAtPoll = fresh.StartsInAtPoll;
        PolledAtTicks = fresh.PolledAtTicks;
    }
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

    /// <summary>
    /// The document rectangle this rendered box edits when it is not the same rectangle.
    /// </summary>
    /// <remarks>
    /// A contained square image occupies only the square part of a wide destination, and a 1280×720
    /// output has a smaller physical footprint than a 1920×1080 composition even when it samples the
    /// whole composition. The canvas draws the first rectangle while gestures remain edits of this
    /// one. Null means the displayed and authored rectangles are identical.
    /// </remarks>
    public NormalizedRect? AuthoredRect { get; init; }

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

    /// <summary>
    /// The main-window screen this check's FIX button takes the operator to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the check NAME rather than from the button's label: "Show ›" is the label on
    /// cues, cue lists, compositions, snapshots and trigger inputs, which live on three different
    /// screens, so the label alone cannot say where to go.
    /// </para>
    /// <para>
    /// Empty for a check with nowhere to send anybody. The button reads its own destination, so a
    /// fix that cannot navigate is simply not offered rather than offered and inert — which is what
    /// every one of them was: the template bound the label and never wired a click, so "Patch ›"
    /// looked like the way to patch an unpatched output and did nothing at all.
    /// </para>
    /// </remarks>
    public string Destination => Check switch
    {
        "Logical outputs" or "Patch snapshots" or "Audio devices"
            or "Clock master and mix" or "Audition rig" => "AUDIO",
        "Compositions" or "Video outputs" or "Recordings" => "VIDEO",
        "Trigger inputs" => "TARGETS",
        "Cues" or "Cue lists" or "Media files" or "Compiles" => "CUES",
        "YouTube cache" => "YOUTUBE_CACHE",
        _ => "",
    };

    /// <summary>A fix is offered only when it has both a label and somewhere to go.</summary>
    public bool CanFix => HasFix && Destination.Length > 0;
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
    /// <summary>The compositor actually selected by the production session, never an assumed GPU.</summary>
    public string Gpu { get; init; } = "Unknown";
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

/// <summary>One editable machine hotkey. Safety Esc/Esc×2 is intentionally a fixed convention.</summary>
public sealed class HotkeyRow : ObservableObject
{
    private readonly Action<string> _write;
    private string _gesture;

    public HotkeyRow(string id, string command, string gesture, string group, Action<string> write)
    {
        Id = id;
        Command = command;
        _gesture = gesture;
        Group = group;
        _write = write;
    }

    public string Id { get; }
    public string Command { get; }
    public string Group { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            var normalized = value.Trim().Replace(" ", "", StringComparison.Ordinal);
            if (SetProperty(ref _gesture, normalized))
                _write(normalized);
        }
    }
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
/// <remarks>
/// An observable CLASS rather than a record on purpose: mid-drag the sheet updates a lane's clips IN
/// PLACE. Replacing the Lanes list makes the ItemsControl unrealize every ClipLane, and Avalonia
/// releases pointer capture on detach — which is how a clip drag used to die after its first motion
/// event.
/// </remarks>
public sealed partial class TimelineLane : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>The cue this lane belongs to — an effect lane carries its OWNER cue's id, so clicking
    /// either row's label selects the same cue.</summary>
    public Guid SubjectId { get; init; }

    /// <summary>Whether this lane's cue is the tree's selected cue — the sheet's label highlight.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private IReadOnlyList<TimelineClip> _clips = [];

    /// <summary>Only clip lanes are draggable; an effect lane's points are edited in the curve editor.</summary>
    public bool IsEditable => !IsEffect;

    /// <summary>An effect lane (volume / opacity / OSC ramp), drawn shorter and indented.</summary>
    public bool IsEffect { get; init; }

    /// <summary>The document lane this row edits. Null on ordinary clip rows.</summary>
    public Guid? EffectLaneId { get; init; }

    public EffectLaneKind? EffectKind { get; init; }

    /// <summary>
    /// The cue-relative editor's position inside the currently visible group window.
    /// </summary>
    /// <remarks>
    /// Observable, not <c>init</c>: a clip drag refreshes the lanes IN PLACE (see
    /// <c>TimelineViewModel.Refresh</c>), so an expanded lane whose span could not be written would
    /// sit at the cue's OLD position while its clip moved, until the gesture ended.
    /// </remarks>
    [ObservableProperty]
    private double _effectLeft;

    [ObservableProperty]
    private double _effectWidth = 1;

    public bool IsGroup { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Height))]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    private bool _isExpanded;

    public double Height => IsEffect ? IsExpanded ? 152 : 58 : 34;
    public string ExpandLabel => IsExpanded ? "COLLAPSE" : "EDIT";

    /// <summary>The authored handles, local to the cue rather than the whole visible group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPoints))]
    private IReadOnlyList<CurvePoint> _points = [];

    public bool HasSelectedPoints => Points.Any(point => point.IsSelected);

    /// <summary>Bézier tangent arms for the selected keyframes.</summary>
    [ObservableProperty]
    private IReadOnlyList<CurveTangent> _tangents = [];

    /// <summary>The sampled shaped path, local to the cue.</summary>
    [ObservableProperty]
    private IReadOnlyList<CurvePoint> _shape = [];

    /// <summary>Audio peaks behind an expanded volume lane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaveform))]
    private IReadOnlyList<float>? _peaks;

    public bool HasWaveform => EffectKind == EffectLaneKind.Volume && Peaks is { Count: > 0 };

    /// <summary>
    /// The lane's envelope as fractions of the lane; empty on a clip lane.
    /// </summary>
    /// <remarks>
    /// Register item 18 makes this ONE concept with two editors: the same points are edited here and
    /// in the inspector, replacing the media cue's separate VolumeEnvelope. Fractions rather than a
    /// <see cref="Geometry"/> because a stretched geometry is scaled by its own bounding box — see
    /// <see cref="Controls.EnvelopeGraph"/> for why that renders a plausible lie.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEnvelope))]
    private IReadOnlyList<CurvePoint> _envelope = [];

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
/// <para>
/// The thumbnail is a <see cref="Media.Geometry"/> rather than the path string it is written as:
/// Avalonia will not convert a BOUND <see cref="string"/> to a geometry the way it converts a literal
/// XAML attribute, so a string-typed property would silently render nothing.
/// </para>
/// <para>
/// <b>Parsed on access, not in the constructor.</b> <see cref="Media.Geometry.Parse"/> resolves
/// <c>IPlatformRenderInterface</c>, so a constructor-parsed geometry made this record — and therefore
/// <see cref="Presentation.CurveLibrary"/>, and therefore every view-model that offers a curve picker —
/// unconstructible without a running renderer. That is the wrong dependency for a row of data, and it
/// broke as badly as it sounds: <c>CurveLibrary</c>'s static initializer threw the first time a
/// view-model was built outside Avalonia's headless session scope, .NET cached the
/// <see cref="TypeInitializationException"/> for the life of the process, and every later test in that
/// assembly failed with it — 227 of them, on whichever runs happened to order a plain fact first.
/// Re-parsing per access costs nothing at five curves read once per bind.
/// </para>
/// </remarks>
public sealed record CurveOption(
    string Name,
    string PathData,
    FadeCurve? Law = null,
    Guid? PresetId = null,
    bool IsCustom = false)
{
    public Geometry Shape => Geometry.Parse(PathData);
}

/// <summary>A draggable point on the custom-curve editor, in fractions of the canvas.</summary>
/// <param name="IsHold">Flat until the next point. Drawn as a square rather than a circle.</param>
public sealed record CurvePoint(double X, double Y, bool IsSelected = false, bool IsHold = false);

/// <summary>One visible cubic Bézier tangent, in canvas fractions. Incoming identifies which side of
/// the owning keyframe is edited.</summary>
public sealed record CurveTangent(
    int Index,
    bool Incoming,
    double AnchorX,
    double AnchorY,
    double X,
    double Y);

/// <summary>One row in the project curve-preset library.</summary>
public sealed record CurvePresetRow(Guid Id, string Name, int References)
{
    public string ReferenceLabel => References == 1 ? "1 use" : $"{References} uses";
}

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

/// <summary>
/// One placement's collapsed row in the inspector.
/// </summary>
/// <remarks>
/// A placement carries the better part of a screen of settings, so a cue on three canvases used to be
/// three screens deep behind a picker. This is what a closed row says about the one it stands for:
/// where the picture goes, and which of the optional stages are in force.
/// </remarks>
/// <param name="Index">Its position in the cue's placement list — what selecting it sets.</param>
/// <param name="Composition">The canvas name, or a plain statement that it has none.</param>
/// <param name="Layer">"L0", "L1" — short enough to sit beside the name.</param>
/// <param name="Summary">Geometry, and whichever of crop, key, grade and mapping are on.</param>
/// <param name="IsOpen">Whether this is the placement the editor below is currently showing.</param>
public readonly record struct PlacementHeader(
    int Index,
    string Composition,
    string Layer,
    string Summary,
    bool IsOpen);
