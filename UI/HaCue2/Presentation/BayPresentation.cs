using System.Globalization;
using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;
using S.Media.Routing;

namespace HaCue2.Presentation;

/// <summary>
/// Turns the audio bay's own counters into the Diagnostics table and the Output info drawer.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is measured. Before this existed the same rows came from <c>SampleShow</c>, which
/// meant the one screen an operator opens to answer "why is there no sound" was describing a show that
/// did not exist - the most expensive possible place for invented data.
/// </para>
/// <para>
/// Terminal ids are line GUIDs, so the project is needed to name them. A terminal whose line has since
/// been deleted keeps its id rather than disappearing: it is still open and still making noise, and a
/// row that vanished would hide that.
/// </para>
/// </remarks>
public static class BayPresentation
{
    /// <summary>Peak dB at which the meter is called hot. Not clipping yet - the last warning before it.</summary>
    private const double HotDb = -3;

    /// <summary>One row per terminal, then one per producer lease.</summary>
    public static IReadOnlyList<BayRow> Rows(HaCueProject project, AudioPatchBayDiagnostics bay)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bay);

        var rows = new List<BayRow>();

        foreach (var terminal in bay.Terminals)
        {
            var stats = terminal.Stats;

            rows.Add(new BayRow
            {
                Name = LineName(project, terminal.TerminalId),
                State = StateOf(terminal),
                InFlight = terminal.InFlight.ToString(CultureInfo.InvariantCulture),
                Capacity = stats.PumpCapacityChunks.ToString(CultureInfo.InvariantCulture),
                Enqueued = stats.Enqueued.ToString(CultureInfo.InvariantCulture),
                Processed = stats.Processed.ToString(CultureInfo.InvariantCulture),
                // A drop is amber the moment it is non-zero. There is no acceptable number of dropped
                // chunks on a show output - one is a click somebody heard.
                Dropped = stats.Dropped == 0
                    ? new Status("0")
                    : new Status(stats.Dropped.ToString(CultureInfo.InvariantCulture), Gel.Amber),
                // A terminal has no latency or epoch of its own to report: both are properties of the
                // producer feeding the bus, and inventing a dash-shaped one here would suggest the
                // number exists and is simply unavailable.
                Latency = new Status("-"),
                Epoch = "-",
                Rate = terminal.NativeSampleRate == bay.MixSampleRate
                    ? $"{terminal.NativeSampleRate / 1000d:0.#}k"
                    : $"{terminal.NativeSampleRate / 1000d:0.#}k · resampled",
            });
        }

        foreach (var producer in bay.Producers)
        {
            rows.Add(new BayRow
            {
                Name = producer.Label ?? "(unnamed lease)",
                State = producer.IsAdvancing
                    ? new Status("advancing", Gel.Green)
                    : new Status("idle", Gel.Steel),
                InFlight = producer.BufferedFrames.ToString(CultureInfo.InvariantCulture),
                // An underrun is the producer's own row of the same story a terminal's drop tells: the
                // bus asked for audio this lease did not have, and silence went out instead.
                Dropped = producer.OverflowFloats + producer.UnderrunFloats == 0
                    ? new Status("0")
                    : new Status(
                        $"{producer.OverflowFloats} over · {producer.UnderrunFloats} under", Gel.Amber),
                Latency = new Status($"{producer.SubmitToOutputLatency.TotalMilliseconds:0} ms"),
                Epoch = producer.EpochId.ToString(CultureInfo.InvariantCulture),
                IsLease = true,
            });
        }

        return rows;
    }

    /// <summary>The program meters, one per logical output, in bus order.</summary>
    public static IReadOnlyList<ProgramMeter> Meters(HaCueProject project, AudioPatchBayDiagnostics bay)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bay);

        var channels = project.AudioPatch.LogicalChannels
            .OrderBy(channel => channel.SortOrder)
            .ToList();

        return
        [
            .. bay.ChannelLevels.Select((level, index) => new ProgramMeter(
                Caption(index < channels.Count ? channels[index].Name : $"CH{index + 1}"),
                Normalize(level.RmsDb),
                Normalize(level.PeakDb),
                level.Clipped)),
        ];
    }

    /// <summary>The summary column's coarse bars, by logical output id.</summary>
    /// <remarks>
    /// Absent means NO TELEMETRY, which is why an unmetered output reads "-" rather than showing an
    /// empty bar: silence and "nobody is measuring" must not look the same in a table.
    /// </remarks>
    public static Dictionary<Guid, OutputLevel> Levels(
        HaCueProject project, AudioPatchBayDiagnostics bay)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bay);

        var channels = project.AudioPatch.LogicalChannels
            .OrderBy(channel => channel.SortOrder)
            .ToList();

        var levels = new Dictionary<Guid, OutputLevel>();

        for (var index = 0; index < bay.ChannelLevels.Count && index < channels.Count; index++)
        {
            var level = bay.ChannelLevels[index];

            levels[channels[index].Id] = new OutputLevel(
                (int)Math.Round(Normalize(level.PeakDb) * 7),
                level.Clipped || level.PeakDb >= HotDb);
        }

        return levels;
    }

    /// <summary>The drawer's one-line summary of the whole bay.</summary>
    public static string Summary(AudioPatchBayDiagnostics bay)
    {
        ArgumentNullException.ThrowIfNull(bay);

        return $"{bay.Producers.Count} lease{(bay.Producers.Count == 1 ? "" : "s")} · "
               + $"{bay.LogicalChannels} logical · {bay.MixSampleRate:N0} Hz";
    }

    /// <summary>What the clock is doing, or that there is no master pacing it.</summary>
    public static string Clock(HaCueProject project, AudioPatchBayDiagnostics bay)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bay);

        if (bay.ClockMasterTerminalId is not { } master)
            return "no clock master · wall-clock fallback";

        var advancing = bay.Producers.Any(producer => producer.IsAdvancing);
        return $"master {LineName(project, master)} · {(advancing ? "adv" : "idle")}";
    }

    private static Status StateOf(TerminalDiagnostics terminal) => terminal.State switch
    {
        TerminalState.AdvancingMaster => new Status("master", Gel.Green),
        TerminalState.Open => new Status("open", Gel.Green),
        TerminalState.Behind => new Status("behind", Gel.Amber),
        _ => new Status("quarantined", Gel.Red),
    };

    private static string LineName(HaCueProject project, string terminalId) =>
        Guid.TryParse(terminalId, out var id) && project.FindLine(id) is { } line
            ? line.Name
            : terminalId;

    /// <summary>
    /// A decibel reading as the 0..1 a meter draws.
    /// </summary>
    /// <remarks>
    /// Over 60 dB, so the bottom of the meter is −60 rather than −∞: a linear-in-dB scale with no floor
    /// has nowhere to put silence, and every real console picks a floor for the same reason.
    /// </remarks>
    private static double Normalize(double decibels) =>
        double.IsNegativeInfinity(decibels) ? 0 : Math.Clamp((decibels + 60) / 60, 0, 1);

    /// <summary>The meter's short caption: "Main L" becomes "ML", "Sub" stays "SUB".</summary>
    private static string Caption(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length switch
        {
            0 => "-",
            1 => words[0][..Math.Min(3, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Select(word => word[0])).ToUpperInvariant(),
        };
    }
}
