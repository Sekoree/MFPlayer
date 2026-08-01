using System.Globalization;
using System.Text;

namespace S.Media.Routing;

/// <summary>
/// Renders an <see cref="AudioPatchBayDiagnostics"/> snapshot as plain text.
/// </summary>
/// <remarks>
/// This is the "Copy report" path: the fastest route from "it glitched in the second half" to something
/// a person can paste into an issue afterwards. Text rather than a screenshot because the numbers are
/// the point, and because a report that can be diffed against a later one is worth far more than an
/// image - a support conversation is usually "what changed", not "what is it now".
/// <para>Columns are padded to fixed widths so the output stays readable in a monospaced paste, and
/// every number is formatted invariantly so a report from a comma-decimal machine reads the same.</para>
/// </remarks>
public static class AudioPatchBayReport
{
    /// <summary>Renders the snapshot. <paramref name="header"/> is an optional first line - a project
    /// name, a timestamp, a build id - supplied by the caller so this stays free of ambient state.</summary>
    public static string Render(AudioPatchBayDiagnostics diagnostics, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var text = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(header))
            text.AppendLine(header).AppendLine();

        var master = diagnostics.ClockMasterTerminalId ?? "(none - wall-clock fallback)";
        text.AppendLine(Invariant(
            $"Audio bay: {diagnostics.MixSampleRate} Hz mix, {diagnostics.LogicalChannels} logical channels, clock master {master}"));
        text.AppendLine();

        AppendTerminals(text, diagnostics.Terminals);
        AppendProducers(text, diagnostics.Producers);
        AppendLevels(text, diagnostics.ChannelLevels);

        return text.ToString();
    }

    private static void AppendTerminals(StringBuilder text, IReadOnlyList<TerminalDiagnostics> terminals)
    {
        text.AppendLine("TERMINALS");
        if (terminals.Count == 0)
        {
            text.AppendLine("  (none attached)").AppendLine();
            return;
        }

        text.AppendLine(
            "  " + "line".PadRight(20) + "state".PadRight(17) + "fmt".PadRight(14) +
            "in/cap".PadRight(10) + "enqueued".PadRight(12) + "processed".PadRight(12) +
            "dropped".PadRight(10) + "abandoned");

        foreach (var t in terminals)
        {
            var format = Invariant($"{t.Channels}ch {t.NativeSampleRate}");
            var inFlight = Invariant($"{t.InFlight}/{t.Stats.PumpCapacityChunks}");
            text.AppendLine(
                "  " + Trim(t.TerminalId, 20).PadRight(20) +
                t.State.ToString().PadRight(17) +
                format.PadRight(14) +
                inFlight.PadRight(10) +
                Invariant($"{t.Stats.Enqueued}").PadRight(12) +
                Invariant($"{t.Stats.Processed}").PadRight(12) +
                Invariant($"{t.Stats.Dropped}").PadRight(10) +
                Invariant($"{t.Stats.Abandoned}"));
        }
        text.AppendLine();
    }

    private static void AppendProducers(StringBuilder text, IReadOnlyList<ProducerDiagnostics> producers)
    {
        text.AppendLine("PRODUCER LEASES");
        if (producers.Count == 0)
        {
            text.AppendLine("  (none sounding)").AppendLine();
            return;
        }

        text.AppendLine(
            "  " + "lease".PadRight(28) + "buffered".PadRight(11) + "overflow".PadRight(11) +
            "underrun".PadRight(11) + "latency".PadRight(11) + "epoch".PadRight(8) + "advancing");

        foreach (var p in producers)
        {
            text.AppendLine(
                "  " + Trim(p.Label ?? "(unlabelled)", 28).PadRight(28) +
                Invariant($"{p.BufferedFrames}").PadRight(11) +
                Invariant($"{p.OverflowFloats}").PadRight(11) +
                Invariant($"{p.UnderrunFloats}").PadRight(11) +
                Invariant($"{p.SubmitToOutputLatency.TotalMilliseconds:F1} ms").PadRight(11) +
                Invariant($"{p.EpochId}").PadRight(8) +
                (p.IsAdvancing ? "yes" : "no"));
        }
        text.AppendLine();
    }

    private static void AppendLevels(StringBuilder text, IReadOnlyList<ProgramChannelLevel> levels)
    {
        if (levels.Count == 0)
        {
            // Say so explicitly: "no levels" and "metering was off" look identical in a table of dashes,
            // and the difference decides whether a silent show was actually silent.
            text.AppendLine("PROGRAM LEVELS").AppendLine("  (metering disabled)");
            return;
        }

        text.AppendLine("PROGRAM LEVELS");
        text.AppendLine("  " + "channel".PadRight(10) + "peak".PadRight(12) + "rms".PadRight(12) + "clip");
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            text.AppendLine(
                "  " + Invariant($"{i}").PadRight(10) +
                Invariant($"{level.PeakDb:F1} dB").PadRight(12) +
                Invariant($"{level.RmsDb:F1} dB").PadRight(12) +
                (level.Clipped ? "CLIP" : ""));
        }
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
