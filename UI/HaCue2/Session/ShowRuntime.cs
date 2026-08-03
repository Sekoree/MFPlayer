using HaCue2.ViewModels;

namespace HaCue2.Session;

/// <summary>
/// Everything on screen that is a fact about the RUNNING show rather than about the document.
/// </summary>
/// <remarks>
/// <para>
/// This is a stand-in, and saying so is the point. Which cues are sounding, how far through they are,
/// what the meters read and what the audio bay's counters say cannot be derived from a
/// <c>HaCueProject</c> — they come from <c>ShowSession</c>, which the shell does not yet own. Phase 5
/// replaces the contents of this class with real telemetry and nothing above it changes.
/// </para>
/// <para>
/// It exists as a separate seam because the alternative is worse: with document and runtime facts
/// mixed into one bag of sample data, nobody reading the shell can tell which values are real and
/// which are invented.
/// </para>
/// <para>
/// <b>Four members are now REAL:</b> <see cref="Sounding"/> comes from the live session, and <see cref="MediaDurations"/> and <see cref="Broken"/> are filled
/// by <c>MediaFactsCache</c> from an actual probe, and <see cref="AbsentLines"/> by real device
/// enumeration. Everything else here is still invented, and each
/// one stops being so as Phase 5 lands. They are settable rather than init-only for exactly that
/// reason — an answer that arrives after the views were built has to reach the instance they already
/// hold, and replacing the object would leave every view-model looking at the old one.
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

    /// <summary>Video outputs this machine does not have.</summary>
    public HashSet<Guid> AbsentVideoOutputs { get; init; } = [];

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

    /// <summary>The MTC readout in the transport row.</summary>
    public string ChaseReadout { get; init; } = "";

    /// <summary>Audio-bay telemetry for the Diagnostics window.</summary>
    public IReadOnlyList<BayRow> BayRows { get; set; } = [];

    public IReadOnlyList<CompositionStatsRow> CompositionStats { get; init; } = [];

    /// <summary>The log tail — a live read of the logging pipeline once one exists.</summary>
    public IReadOnlyList<LogLine> Log { get; init; } = [];

    /// <summary>When each trigger input last spoke, by document id.</summary>
    public Dictionary<Guid, string> LastSeen { get; init; } = [];

    /// <summary>When each action endpoint was last sent to, by document id.</summary>
    public Dictionary<Guid, string> LastSent { get; init; } = [];

    /// <summary>Inbound wire traffic for the Targets monitor.</summary>
    public IReadOnlyList<LogLine> TriggerMonitor { get; init; } = [];

    /// <summary>Nothing running: what the shell shows before a show starts.</summary>
    public static ShowRuntime Idle { get; } = new();
}

/// <summary>One logical output's level, as the summary column shows it.</summary>
/// <param name="Bars">0–7 bar glyphs; zero renders as "—", not as an empty meter.</param>
/// <param name="IsHot">Clipping, latched — the sticky red the operator has to acknowledge.</param>
public readonly record struct OutputLevel(int Bars, bool IsHot);
