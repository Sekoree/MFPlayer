using HaCue2.Engine;
using HaCue2.ViewModels;

namespace HaCue2.Session;

/// <summary>
/// Everything on screen that is a fact about the RUNNING show rather than about the document.
/// </summary>
/// <remarks>
/// <para>
/// Which cues are sounding, how far through they are, what the meters read and what the audio bay's
/// counters say cannot be derived from a <c>HaCueProject</c> — they come from the running session,
/// which the views deliberately cannot see. They read this instead, and <c>EngineRuntime</c> fills it.
/// </para>
/// <para>
/// It is a separate seam because the alternative is worse: with document and runtime facts mixed into
/// one bag, nobody reading the shell can tell which values are measured and which are assumed — and
/// with no session at all (a laptop, a preview, a test) the app is still a working editor, which only
/// holds while the runtime half is one object that can simply be empty.
/// </para>
/// <para>
/// <b>Every member is now real.</b> The live session fills <see cref="Sounding"/>,
/// <see cref="ActiveCues"/>, <see cref="IsPaused"/>, <see cref="Levels"/>, <see cref="Meters"/>,
/// <see cref="LineChips"/>, <see cref="BayRows"/>, <see cref="BaySummary"/>, <see cref="BayClock"/>,
/// <see cref="CompositionStats"/>, <see cref="ChaseReadout"/>, <see cref="Recorders"/>,
/// <see cref="TriggerMonitor"/>, <see cref="LastSignal"/>, <see cref="LastSeen"/> and
/// <see cref="LastSent"/>; <c>MediaFactsCache</c> fills <see cref="MediaDurations"/> and
/// <see cref="Broken"/> from a probe; device enumeration fills <see cref="AbsentLines"/>, and opening
/// the show's windows fills <see cref="AbsentVideoOutputs"/>.
/// <para>
/// <b>Nothing here is invented any more.</b> Keep this paragraph current if that changes — it is the
/// one place a reader can tell which values on screen are facts. A member that has to be guessed at
/// belongs in a named list here, not quietly among the measured ones.
/// </para>
/// They are settable rather than init-only because an answer that arrives after the views were built
/// has to reach the instance they already hold; replacing the object would leave every view-model
/// looking at the old one.
/// </para>
/// </remarks>
public sealed class ShowRuntime
{
    /// <summary>Cues currently sounding, by document id.</summary>
    public HashSet<Guid> Sounding { get; set; } = [];

    /// <summary>Cues whose media the document names but which cannot be resolved on this machine.</summary>
    public HashSet<Guid> Broken { get; set; } = [];

    /// <summary>
    /// How long each cue's media runs, by document id.
    /// </summary>
    /// <remarks>
    /// A duration is a MACHINE fact — it comes from probing the file — so it belongs here rather than
    /// in the document. A cue whose media nobody has looked at shows "—" in the Len column, which is
    /// the truthful answer and not a rendering gap.
    /// </remarks>
    public Dictionary<Guid, TimeSpan> MediaDurations { get; set; } = [];

    /// <summary>
    /// Per-logical-output levels, by document id. Absent means NO TELEMETRY, which is why the meter
    /// column reads "—" rather than showing an empty bar: silence and "nobody is measuring" look the
    /// same on a meter and must not read the same in a table.
    /// </summary>
    public Dictionary<Guid, OutputLevel> Levels { get; set; } = [];

    /// <summary>Audio lines this machine does not have. A machine fact, never a document one.</summary>
    public HashSet<Guid> AbsentLines { get; set; } = [];

    /// <summary>Video outputs that are not showing anything on this machine.</summary>
    public HashSet<Guid> AbsentVideoOutputs { get; set; } = [];

    /// <summary>
    /// The Active panel — a runtime list in its entirety.
    /// </summary>
    /// <remarks>
    /// Settable, like the other members the engine fills. It was init-only, which meant
    /// <c>EngineRuntime</c> could not write it and the panel showed the sample's five invented rows
    /// forever — empty on any real project, however much was sounding.
    /// </remarks>
    public IReadOnlyList<ActiveCueRow> ActiveCues { get; set; } = [];

    /// <summary>Whether the transport is paused. A runtime fact; false with no session.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Program meters for the Output info drawer.</summary>
    public IReadOnlyList<ProgramMeter> Meters { get; set; } = [];

    /// <summary>Line chips for the Output info drawer.</summary>
    public IReadOnlyList<OutputLineChip> LineChips { get; set; } = [];

    public string BaySummary { get; set; } = "";
    public string BayClock { get; set; } = "";

    /// <summary>
    /// The MTC readout in the transport row.
    /// </summary>
    /// <remarks>
    /// Where the incoming timecode says the SENDER is. Nothing chases it yet, so this is a report on a
    /// cable rather than on the show — which is why the chip distinguishes "input off" from "no signal"
    /// from "undecodable" rather than showing a blank when any of the three is true.
    /// </remarks>
    public string ChaseReadout { get; set; } = "";

    /// <summary>Audio-bay telemetry for the Diagnostics window.</summary>
    public IReadOnlyList<BayRow> BayRows { get; set; } = [];

    /// <summary>
    /// Every record and stream target: armed or not, where it is writing, and how it fares.
    /// </summary>
    /// <remarks>
    /// Read on the tick rather than on a change, for the same reason the meters are: a recording that
    /// starts dropping frames does so quietly, and a status that only refreshed when somebody armed or
    /// disarmed would show "armed, fine" over a file that had been gapping for ten minutes.
    /// </remarks>
    public IReadOnlyList<RecorderStatus> Recorders { get; set; } = [];

    /// <summary>Per-composition render telemetry for the Diagnostics window.</summary>
    /// <remarks>
    /// There is no <c>Log</c> beside this, deliberately. The Diagnostics window reads the app's log
    /// ring directly: the ring already IS the bounded window that panel wants, and a copy of it behind
    /// this seam would be a second buffer to keep in step with the archive the file log writes.
    /// </remarks>
    public IReadOnlyList<CompositionStatsRow> CompositionStats { get; set; } = [];

    /// <summary>When each trigger input last spoke, by document id.</summary>
    public Dictionary<Guid, string> LastSeen { get; set; } = [];

    /// <summary>
    /// The most recent thing that arrived on any enabled source, as a binding pattern.
    /// </summary>
    /// <remarks>
    /// What Learn captures. Stored as the PATTERN rather than the raw message because that is what a
    /// binding holds — and because the wire monitor prints the same text, so what the operator sees is
    /// literally what gets bound.
    /// </remarks>
    public string LastSignal { get; set; } = "";

    /// <summary>What each action endpoint was last sent, and when, by document id.</summary>
    public IReadOnlyDictionary<Guid, string> LastSent { get; set; } =
        new Dictionary<Guid, string>();

    /// <summary>Inbound wire traffic for the Targets monitor.</summary>
    public IReadOnlyList<LogLine> TriggerMonitor { get; set; } = [];

    /// <summary>Nothing running: what the shell shows before a show starts.</summary>
    public static ShowRuntime Idle { get; } = new();
}

/// <summary>One logical output's level, as the summary column shows it.</summary>
/// <param name="Bars">0–7 bar glyphs; zero renders as "—", not as an empty meter.</param>
/// <param name="IsHot">Clipping, latched — the sticky red the operator has to acknowledge.</param>
public readonly record struct OutputLevel(int Bars, bool IsHot);
