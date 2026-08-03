using System.Globalization;

namespace HaCue2.Core.Model;

/// <summary>
/// Reading and writing the times a trim window is expressed in.
/// </summary>
/// <remarks>
/// <para>
/// The fields used to be seconds with one decimal, which meant half an hour was typed as
/// <c>1800.0</c>. That is fine for a two-second sting and useless for the case this exists for —
/// trimming thirty minutes off the front of a recording — so <c>30:00</c> and <c>1:05:30.250</c> read
/// too, and seconds still do.
/// </para>
/// <para>
/// <b>A leading minus counts back from the END.</b> The document stores an out-point as an absolute
/// position, so "ten minutes off the end" was <c>length − 600</c> — arithmetic the operator had to do
/// themselves, against a length the app never showed them. <c>-10:00</c> is the same thing said the way
/// it is meant, and it is resolved against the probed length at the moment it is typed.
/// </para>
/// </remarks>
public static class ClipTimes
{
    /// <summary>What the fields accept, for the hint under them.</summary>
    public const string Syntax = "1800 · 30:00 · 1:05:30.250 · -10:00 from the end · end";

    /// <summary>
    /// A typed time as milliseconds, or null when it is not one.
    /// </summary>
    /// <param name="length">
    /// The file's probed length, for a from-the-end value. Without one a negative time cannot be
    /// resolved and is refused rather than guessed — an out-point placed against an assumed length
    /// would cut the cue somewhere nobody chose.
    /// </param>
    public static int? Parse(string text, TimeSpan? length = null)
    {
        var trimmed = (text ?? "").Trim().Replace('−', '-');

        if (trimmed.Length == 0)
            return null;

        var fromEnd = trimmed.StartsWith('-');

        if (fromEnd)
            trimmed = trimmed[1..].TrimStart();

        var fields = trimmed.Split(':');

        if (fields.Length > 3)
            return null;

        double total = 0;

        for (var index = 0; index < fields.Length; index++)
        {
            // Only the LAST field may be fractional: 1:30.5 is ninety and a half seconds, and 1.5:30
            // is not a time anybody means.
            var last = index == fields.Length - 1;

            if (!double.TryParse(
                    fields[index], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || value < 0
                || (!last && value != Math.Floor(value)))
                return null;

            // Minutes and seconds only carry past 59 when they are the leading field: "90" is a minute
            // and a half, "1:90" is not a time.
            if (index > 0 && value >= 60)
                return null;

            total = (total * 60) + value;
        }

        var milliseconds = (int)Math.Round(total * 1000);

        if (!fromEnd)
            return milliseconds;

        if (length is not { } probed)
            return null;

        return (int)Math.Max(0, Math.Round(probed.TotalMilliseconds) - milliseconds);
    }

    /// <summary>
    /// Milliseconds as the shortest sensible clock reading.
    /// </summary>
    /// <remarks>
    /// Hours only when there are hours, and thousandths only when they are not zero — a trim window on
    /// a sting reads <c>2.250</c> rather than <c>0:00:02.250</c>, and one on a concert reads
    /// <c>1:05:30</c> rather than trailing three zeros nobody set.
    /// </remarks>
    public static string Format(int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var fraction = span.Milliseconds == 0 ? "" : $".{span.Milliseconds:000}";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}{fraction}";

        return span.TotalMinutes >= 1
            ? $"{span.Minutes}:{span.Seconds:00}{fraction}"
            : $"{span.Seconds}{fraction}";
    }
}
