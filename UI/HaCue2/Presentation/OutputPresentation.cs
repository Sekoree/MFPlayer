using System.Globalization;
using HaCue2.Core.Model;
using HaCue2.Session;
using HaCue2.ViewModels;
using S.Control;
using S.Media.Routing;
using S.Media.Session;

namespace HaCue2.Presentation;

/// <summary>
/// Turns what the outputs are actually doing into the drawer's chips, the composition table and the
/// transport's chase readout.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="BayPresentation"/> for everything an output can be that a bay
/// terminal cannot: a screen that never opened, a canvas rendering behind its clock, a timecode sender
/// on the other end of a cable. All three used to be invented, and all three are read on the same
/// sweep as the meters — a chip that only refreshed when something was armed would sit on "fine" over
/// a projector that had dropped out ten minutes ago.
/// </remarks>
public static class OutputPresentation
{
    /// <summary>
    /// The Output info drawer's chips: one per audio line, then one per video output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audio first because that is the order the question gets asked in — "why is there no sound"
    /// before "why is there no picture" — and because the audio side is the one with a clock master to
    /// point at.
    /// </para>
    /// <para>
    /// A line the document defines but the patch never sends to gets a chip too, reading "not patched".
    /// It is the single most common reason a rig is silent, and a line that simply vanished from the
    /// drawer would leave the operator looking for a device fault that is not there.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<OutputLineChip> Chips(
        HaCueProject project,
        AudioPatchBayDiagnostics bay,
        ShowRuntime runtime,
        IReadOnlyList<ClipCompositionRuntimeStats> compositions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bay);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(compositions);

        var terminals = bay.Terminals.ToDictionary(terminal => terminal.TerminalId);
        var chips = new List<OutputLineChip>();

        foreach (var line in project.AudioLines)
        {
            var key = line.Id.ToString();

            if (runtime.AbsentLines.Contains(line.Id))
            {
                chips.Add(new OutputLineChip
                {
                    Name = line.Name, Detail = "device absent", Gel = Gel.Red,
                });

                continue;
            }

            if (!terminals.TryGetValue(key, out var terminal))
            {
                chips.Add(new OutputLineChip
                {
                    Name = line.Name, Detail = "not patched", Gel = Gel.Neutral,
                });

                continue;
            }

            var stats = terminal.Stats;

            chips.Add(new OutputLineChip
            {
                Name = line.Name,
                Suffix = bay.ClockMasterTerminalId == key ? "master" : "",
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{terminal.NativeSampleRate / 1000d:0.#}k · {stats.Dropped} drop · "
                    + $"{terminal.InFlight}/{stats.PumpCapacityChunks}"),
                // A drop is amber at one. There is no acceptable number of dropped chunks on a show
                // output — one of them is a click somebody in the room heard.
                Gel = terminal.State switch
                {
                    TerminalState.Quarantined => Gel.Red,
                    TerminalState.Behind => Gel.Amber,
                    _ => stats.Dropped > 0 ? Gel.Amber : Gel.Green,
                },
            });
        }

        var byComposition = compositions.ToDictionary(stats => stats.CompositionId);

        foreach (var output in project.VideoOutputs)
        {
            if (runtime.AbsentVideoOutputs.Contains(output.Id))
            {
                chips.Add(new OutputLineChip
                {
                    Name = output.Name, Detail = "screen absent", Gel = Gel.Red,
                });

                continue;
            }

            if (output.CompositionId is not { } compositionId
                || !byComposition.TryGetValue(compositionId.ToString(), out var stats))
            {
                chips.Add(new OutputLineChip
                {
                    Name = output.Name, Detail = "no frames yet", Gel = Gel.Neutral,
                });

                continue;
            }

            chips.Add(new OutputLineChip
            {
                Name = output.Name,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{stats.TargetFramesPerSecond:0.##} · {stats.MissedCompositionDeadlines} missed"),
                Gel = stats.MissedCompositionDeadlines > 0 ? Gel.Amber : Gel.Green,
            });
        }

        return chips;
    }

    /// <summary>
    /// The composition table (screen 15), given an achieved frame rate per composition.
    /// </summary>
    /// <remarks>
    /// The rate is passed in rather than read here because it is a DELTA — only something that has
    /// been watching across ticks can compute it. See <see cref="CompositionRates"/>, which is the
    /// thing that has been.
    /// </remarks>
    public static IReadOnlyList<CompositionStatsRow> Compositions(
        HaCueProject project,
        IReadOnlyList<ClipCompositionRuntimeStats> stats,
        IReadOnlyDictionary<string, double> achieved)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(achieved);

        return
        [
            .. stats.Select(item => new CompositionStatsRow
            {
                Name = Name(project, item),
                Fps = Rate(item, achieved.GetValueOrDefault(item.CompositionId, -1)),
                Layers = item.LayerCount.ToString(CultureInfo.InvariantCulture),
                // A coalesced canvas deadline is an actual frame the renderer did not produce. The old
                // master-drift counter was a sampled duration estimate, not an observed frame loss.
                Late = item.MissedCompositionDeadlines == 0
                    ? new Status("0")
                    : new Status(item.MissedCompositionDeadlines.ToString(CultureInfo.InvariantCulture), Gel.Amber),
                Dropped = Dropped(item),
                Gpu = item.CompositorBackend,
            }),
        ];
    }

    /// <summary>
    /// The transport's timecode chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four states, and they are worth keeping apart. "input off" is the operator's own switch and must
    /// never be mistaken for a fault; "no signal" is a cable or a stopped sender; "undecodable" is the
    /// one the framework's chase clock can see and nobody else can — timecode that ARRIVES and never
    /// assembles, which without a name looks exactly like an unplugged cable and sends people to check
    /// the wrong end of it; "held" is a sender that has stalled or parked, where the position on screen
    /// is the last one it actually reached rather than one predicted past the evidence.
    /// </para>
    /// <para>
    /// Nothing chases this yet. The chip says where the sender is, not where the show is.
    /// </para>
    /// </remarks>
    public static string Chase(MidiTimecodeChaseState state, bool inputEnabled)
    {
        if (!inputEnabled)
            return "MTC · input off";

        if (!state.HasSignal)
            return state.UndecodedQuarterFrames > 0 ? "MTC · undecodable" : "MTC · no signal";

        var label = $"MTC {state.Position}";

        return state.IsChasing ? label : $"{label} · held";
    }

    private static string Name(HaCueProject project, ClipCompositionRuntimeStats stats)
    {
        // The audition canvas is a real composition with real telemetry and no document behind it. It
        // is worth a row — a monitor rig that is dropping frames is a thing to know — but it must not
        // read as one of the show's own.
        if (stats.CompositionId == ShowSession.AuditionCompositionId)
            return "Audition monitor";

        return Guid.TryParse(stats.CompositionId, out var id)
               && project.Compositions.FirstOrDefault(item => item.Id == id) is { } composition
            ? $"{composition.Name} · {composition.Width}×{composition.Height}"
            : stats.CompositionId;
    }

    /// <summary>
    /// Achieved over target, when there has been long enough to measure one.
    /// </summary>
    /// <remarks>
    /// A negative achieved rate is the "not yet" marker rather than a zero: a composition that has
    /// genuinely stopped compositing reads 0 and must not be confused with one nobody has timed.
    /// </remarks>
    private static Status Rate(ClipCompositionRuntimeStats stats, double achieved)
    {
        var target = stats.TargetFramesPerSecond;

        if (achieved < 0)
            return new Status(string.Create(CultureInfo.InvariantCulture, $"— / {target:0.##}"));

        var text = string.Create(CultureInfo.InvariantCulture, $"{achieved:0.#} / {target:0.##}");

        // Within a twentieth of target is what a compositor pacing itself against a display looks like;
        // below that somebody is losing frames.
        return target > 0 && achieved < target * 0.95
            ? new Status(text, Gel.Amber)
            : new Status(text, Gel.Green);
    }

    /// <summary>
    /// Keeps source sampling, missed canvas work, output pressure and normal cadence conversion distinct.
    /// </summary>
    /// <remarks>
    /// Device skip/repeat is expected when canvas and panel rates differ, so it must not be added to
    /// pressure or source-unsampled counts and coloured as one generic failure.
    /// </remarks>
    private static string Dropped(ClipCompositionRuntimeStats stats)
    {
        long pressure = 0;
        long cadenceDrops = 0;
        long cadenceRepeats = 0;
        foreach (var output in stats.OutputStats)
        {
            pressure += output.BackpressureDropped;
            cadenceDrops += output.PresentDropped;
            cadenceRepeats += output.PresentRepeated;
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"overflow {stats.SlotOverflowFrames} · sample {stats.SourceSamplesSkipped} · canvas {stats.MissedCompositionDeadlines} · pressure {pressure} · cadence {cadenceDrops}/{cadenceRepeats}");
    }
}

/// <summary>
/// Measures each composition's ACHIEVED frame rate across ticks.
/// </summary>
/// <remarks>
/// The runtime reports a frame COUNT, which is the only thing it can report — a rate is a count over an
/// interval and the runtime does not know when anyone last looked. This is the thing that knows. It is
/// stateful for exactly that reason and belongs to the sweep that owns the interval.
/// </remarks>
public sealed class CompositionRates
{
    /// <summary>Frames and the tick they were read at, per composition.</summary>
    private readonly Dictionary<string, (long Frames, long Ticks)> _last = [];

    /// <summary>
    /// Takes one sample and returns the achieved rate per composition.
    /// </summary>
    /// <remarks>
    /// A composition seen for the first time is absent from the result rather than reported as zero:
    /// one sample is a count, not a rate, and a table that showed 0 fps for the first quarter-second of
    /// every show would train an operator to ignore the column.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Sample(IReadOnlyList<ClipCompositionRuntimeStats> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var rates = new Dictionary<string, double>(stats.Count);
        var seen = new HashSet<string>(stats.Count);

        foreach (var item in stats)
        {
            seen.Add(item.CompositionId);

            if (_last.TryGetValue(item.CompositionId, out var previous))
            {
                var seconds = System.Diagnostics.Stopwatch
                    .GetElapsedTime(previous.Ticks, now).TotalSeconds;

                // Two reads inside a millisecond of each other divide by nearly nothing and produce a
                // rate in the thousands. Skipped rather than clamped: the previous sample is still
                // valid and the next tick will be a real interval.
                if (seconds >= 0.05)
                    rates[item.CompositionId] =
                        Math.Max(0, (item.FramesComposited - previous.Frames) / seconds);
                else
                    continue;
            }

            _last[item.CompositionId] = (item.FramesComposited, now);
        }

        // A composition that went away takes its sample with it, so one that comes back under the same
        // id is timed from its return rather than across the gap.
        foreach (var stale in _last.Keys.Where(id => !seen.Contains(id)).ToList())
            _last.Remove(stale);

        return rates;
    }
}
