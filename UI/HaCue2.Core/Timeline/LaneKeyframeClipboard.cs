using System.Globalization;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Timeline;

/// <summary>
/// Portable text representation for selected keyframes. X and tangent X values are relative to the
/// first copied point, so paste can place the shape at the timeline playhead.
/// </summary>
/// <remarks>
/// <para>
/// The exchange type is <see cref="CurveKnot"/>. Automation keyframes convert their absolute time and
/// property value to the active editor viewport before using this portable normalized representation.
/// </para>
/// <para>
/// <b>Version 2 appends a field; it never redefines one.</b> A v1 line is still read (its knots
/// simply do not hold), so text copied from an older build still pastes. Same additive rule the
/// document format follows.
/// </para>
/// </remarks>
public static class LaneKeyframeClipboard
{
    public const string Header = "HaCue2-Keyframes/2";

    /// <summary>The format this replaced. Still decoded, never written.</summary>
    private const string LegacyHeader = "HaCue2-Keyframes/1";

    public static string Encode(IReadOnlyList<CurveKnot> knots, IReadOnlySet<int> selected)
    {
        var indices = selected.Where(index => index >= 0 && index < knots.Count).Order().ToList();
        if (indices.Count == 0)
            return "";

        var anchor = knots[indices[0]].X;
        var lines = new List<string> { Header };
        foreach (var index in indices)
        {
            var knot = knots[index];
            // A tangent only survives when the keyframe at its OTHER end came too: half a Bézier is
            // not a shape, and Normalize would strip it on arrival anyway.
            var keepsIncoming = selected.Contains(index - 1);
            var keepsOutgoing = selected.Contains(index + 1);
            lines.Add(string.Join(";",
                Number(knot.X - anchor),
                Number(knot.Y),
                ((int)knot.CurveToNext).ToString(CultureInfo.InvariantCulture),
                Optional(keepsOutgoing ? knot.OutHandleX - anchor : null),
                Optional(keepsOutgoing ? knot.OutHandleY : null),
                Optional(keepsIncoming ? knot.InHandleX - anchor : null),
                Optional(keepsIncoming ? knot.InHandleY : null),
                knot.Hold ? "1" : "0"));
        }
        return string.Join('\n', lines);
    }

    public static string Encode(IReadOnlyList<LanePoint> points, IReadOnlySet<int> selected) =>
        Encode([.. points.Select(point => new CurveKnot(
            point.X, point.Y, CurveToNext: point.CurveToNext,
            OutHandleX: point.OutHandleX, OutHandleY: point.OutHandleY,
            InHandleX: point.InHandleX, InHandleY: point.InHandleY))], selected);

    public static IReadOnlyList<CurveKnot>? DecodeKnots(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || (lines[0] != Header && lines[0] != LegacyHeader))
            return null;

        var knots = new List<CurveKnot>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var fields = line.Split(';');
            if (fields.Length is not (7 or 8)
                || !TryNumber(fields[0], out var x)
                || !TryNumber(fields[1], out var y)
                || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lawValue)
                || !Enum.IsDefined(typeof(FadeCurve), lawValue)
                || !TryOptional(fields[3], out var outX)
                || !TryOptional(fields[4], out var outY)
                || !TryOptional(fields[5], out var inX)
                || !TryOptional(fields[6], out var inY)
                || !TryHold(fields, out var hold)
                || x is < 0 or > 1 || y is < 0 or > 1
                || (outX is null) != (outY is null) || (inX is null) != (inY is null))
                return null;
            knots.Add(new CurveKnot(x, y, hold, (FadeCurve)lawValue, outX, outY, inX, inY));
        }

        if (knots.Count == 0 || knots.Zip(knots.Skip(1)).Any(pair => pair.First.X > pair.Second.X))
            return null;

        // Re-anchored on the way out too, so hand-authored text that does not start at zero still
        // pastes as a shape rather than at an absolute position.
        var anchor = knots[0].X;
        return [.. knots.Select(knot => knot with
        {
            X = knot.X - anchor,
            OutHandleX = knot.OutHandleX is { } handleOut ? handleOut - anchor : null,
            InHandleX = knot.InHandleX is { } handleIn ? handleIn - anchor : null,
        })];
    }

    public static IReadOnlyList<LanePoint>? Decode(string? text) =>
        DecodeKnots(text) is { } knots
            ? [.. knots.Select(knot => new LanePoint(
                knot.X, knot.Y, knot.CurveToNext,
                knot.OutHandleX, knot.OutHandleY, knot.InHandleX, knot.InHandleY))]
            : null;

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Optional(double? value) => value is { } number ? Number(number) : "";

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    /// <summary>The v2 hold flag. Absent on a v1 line, which had no holds to express.</summary>
    private static bool TryHold(string[] fields, out bool hold)
    {
        hold = false;
        if (fields.Length < 8)
            return true;

        hold = fields[7] == "1";
        return fields[7] is "0" or "1";
    }

    private static bool TryOptional(string text, out double? value)
    {
        if (text.Length == 0)
        {
            value = null;
            return true;
        }
        if (TryNumber(text, out var number))
        {
            value = number;
            return true;
        }
        value = null;
        return false;
    }
}
