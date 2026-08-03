using System.Globalization;

namespace HaCue2.Core.Model;

/// <summary>
/// Reads the times a schedule or a timecode binding fires at.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>HaCue2.Core</c> for the same reason the MIDI parser is.</b> An unparseable time is a cue
/// that will never fire, and the operator has to learn that from the status pass while authoring —
/// not from a show where the thing simply did not happen and nobody can say why.
/// </para>
/// <para>
/// Two formats, and they are deliberately different shapes so a reader can tell them apart at a
/// glance: a schedule is a time of DAY (<c>22:30</c>, <c>22:30:00</c>) and a timecode is a LABEL with
/// frames (<c>01:12:44:07</c>). Four fields never means a wall clock and two never means timecode.
/// </para>
/// </remarks>
public static class TriggerTimes
{
    /// <summary>What a schedule binding's input looks like, for hints and refusals.</summary>
    public const string ScheduleSyntax = "a time of day — 22:30 or 22:30:00";

    /// <summary>What a timecode binding's input looks like.</summary>
    public const string TimecodeSyntax = "a timecode — 01:12:44:07 (hh:mm:ss:ff)";

    /// <summary>
    /// A time of day, as seconds since midnight.
    /// </summary>
    /// <remarks>
    /// Local time, deliberately: a show called for half past ten happens at half past ten in the room,
    /// and a schedule expressed in UTC would move under the operator twice a year.
    /// </remarks>
    public static double? Schedule(string input)
    {
        var parts = Split(input);

        if (parts is not { Length: 2 or 3 })
            return null;

        return parts[0] is >= 0 and < 24 && parts[1] is >= 0 and < 60
               && (parts.Length == 2 || parts[2] is >= 0 and < 60)
            ? (parts[0] * 3600) + (parts[1] * 60) + (parts.Length == 3 ? parts[2] : 0)
            : null;
    }

    /// <summary>
    /// A timecode label, as seconds — given the rate the frame field is counted in.
    /// </summary>
    /// <remarks>
    /// The rate comes from the incoming stream rather than from the document: a label authored against
    /// 25 fps and played back at 30 names a different instant, and the sender is the one that knows.
    /// It is passed in rather than assumed for exactly that reason.
    /// </remarks>
    public static double? Timecode(string input, double framesPerSecond)
    {
        var parts = Split(input);

        if (parts is not { Length: 4 } || framesPerSecond <= 0)
            return null;

        return parts[0] is >= 0 and < 24 && parts[1] is >= 0 and < 60 && parts[2] is >= 0 and < 60
               && parts[3] >= 0 && parts[3] < framesPerSecond
            ? (parts[0] * 3600) + (parts[1] * 60) + parts[2] + (parts[3] / framesPerSecond)
            : null;
    }

    /// <summary>
    /// Whether a binding's input reads as a time for its source kind, and what is wrong when it does not.
    /// </summary>
    /// <remarks>
    /// The frame field is checked against 30 — the HIGHEST of the common rates — so only the truly
    /// impossible labels (<c>:44</c>, <c>26:00:00:00</c>) are refused. Checking against the lowest
    /// would reject perfectly good 30 fps authoring on a machine where nobody has yet plugged in the
    /// stream that would have said so.
    /// </remarks>
    public static string? Refuse(TriggerInputKind kind, string input) => kind switch
    {
        TriggerInputKind.Schedule when Schedule(input) is null =>
            $"“{input}” is not {ScheduleSyntax}",
        TriggerInputKind.Timecode when Timecode(input, 30) is null =>
            $"“{input}” is not {TimecodeSyntax}",
        _ => null,
    };

    /// <summary>The colon-separated fields as numbers, or null when any of them is not one.</summary>
    private static int[]? Split(string input)
    {
        var fields = (input ?? "").Trim()
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length == 0)
            return null;

        var values = new int[fields.Length];

        for (var index = 0; index < fields.Length; index++)
        {
            if (!int.TryParse(
                    fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                return null;
        }

        return values;
    }
}
