namespace S.Media.Routing;

/// <summary>What a terminal is doing, as far as the bay can actually tell.</summary>
/// <remarks>
/// Deliberately does NOT include "absent". Device presence is a host-level fact - the bay only ever sees
/// an <c>IAudioOutput</c> it was handed, so it cannot distinguish "this device was unplugged" from "this
/// device is quiet". Reporting absence is the output catalog's job; inventing it here would produce a
/// state that is wrong exactly when it matters.
/// </remarks>
public enum TerminalState
{
    /// <summary>Attached and draining, and it is the line pacing the bay.</summary>
    AdvancingMaster,

    /// <summary>Attached and draining normally.</summary>
    Open,

    /// <summary>Attached, but its queue is at capacity and it is not draining - the early warning that
    /// precedes a drop.</summary>
    Behind,

    /// <summary>Its pump wedged in a native Submit and was leaked. The bay keeps running; this line does
    /// not, until it is replaced.</summary>
    Quarantined,
}

/// <summary>One real output line's diagnostics row.</summary>
/// <param name="TerminalId">The id the host attached it under.</param>
/// <param name="State">See <see cref="TerminalState"/>.</param>
/// <param name="Channels">Device channel count.</param>
/// <param name="NativeSampleRate">The device's own rate. When it differs from the bay's mix rate the line
/// is running through a resampling wrapper, which is also why it can never be the clock master.</param>
/// <param name="IsClockMaster">Whether this line paces the bay.</param>
/// <param name="Stats">The router's pump counters for this line, in full - the session-level query
/// historically discarded all but two of these.</param>
/// <param name="InFlight">Chunks enqueued but not yet processed, dropped or abandoned.</param>
public readonly record struct TerminalDiagnostics(
    string TerminalId,
    TerminalState State,
    int Channels,
    int NativeSampleRate,
    bool IsClockMaster,
    AudioRouter.OutputPumpStats Stats,
    long InFlight);

/// <summary>One producer lease's diagnostics row - the input side, which had no counters at all before.</summary>
/// <param name="Label">The host's name for this lease, when it supplied one.</param>
/// <param name="BufferedFrames">Frames waiting in this producer's ring.</param>
/// <param name="OverflowFloats">Samples dropped because the host submitted faster than the bus consumed
/// (oldest-first, live policy).</param>
/// <param name="UnderrunFloats">Samples the bus had to fill with silence because this producer was late.</param>
/// <param name="SubmitToOutputLatency">Submit-to-audible latency for this lease.</param>
/// <param name="EpochId">Clock epoch; a change means a flush/seek re-anchored this producer.</param>
/// <param name="IsAdvancing">Whether this lease's clock is moving.</param>
public readonly record struct ProducerDiagnostics(
    string? Label,
    int BufferedFrames,
    long OverflowFloats,
    long UnderrunFloats,
    TimeSpan SubmitToOutputLatency,
    long EpochId,
    bool IsAdvancing);

/// <summary>A whole-bay diagnostics snapshot: the numbers behind the operator's output view.</summary>
/// <param name="MixSampleRate">The project mix rate every producer submits at.</param>
/// <param name="LogicalChannels">Width of the program bus.</param>
/// <param name="ClockMasterTerminalId">The pacing line, or null when the bay has no master (producer
/// clocks then run in the wall-clock fallback domain).</param>
/// <param name="Terminals">One row per attached output line.</param>
/// <param name="Producers">One row per live producer lease.</param>
/// <param name="ChannelLevels">Per-logical-output levels when metering is enabled, otherwise empty.</param>
public sealed record AudioPatchBayDiagnostics(
    int MixSampleRate,
    int LogicalChannels,
    string? ClockMasterTerminalId,
    IReadOnlyList<TerminalDiagnostics> Terminals,
    IReadOnlyList<ProducerDiagnostics> Producers,
    IReadOnlyList<ProgramChannelLevel> ChannelLevels);
