using System.Globalization;
using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Timeline;

/// <summary>Portable text representation for selected automation keyframes. X and tangent X values are
/// relative to the first copied point, so paste can place the shape at the timeline playhead.</summary>
public static class LaneKeyframeClipboard
{
    public const string Header = "HaCue2-Keyframes/1";

    public static string Encode(IReadOnlyList<LanePoint> points, IReadOnlySet<int> selected)
    {
        var indices = selected.Where(index => index >= 0 && index < points.Count).Order().ToList();
        if (indices.Count == 0)
            return "";

        var anchor = points[indices[0]].X;
        var lines = new List<string> { Header };
        foreach (var index in indices)
        {
            var point = points[index];
            var keepsIncoming = selected.Contains(index - 1);
            var keepsOutgoing = selected.Contains(index + 1);
            lines.Add(string.Join(";",
                Number(point.X - anchor),
                Number(point.Y),
                ((int)point.CurveToNext).ToString(CultureInfo.InvariantCulture),
                Optional(keepsOutgoing ? point.OutHandleX - anchor : null),
                Optional(keepsOutgoing ? point.OutHandleY : null),
                Optional(keepsIncoming ? point.InHandleX - anchor : null),
                Optional(keepsIncoming ? point.InHandleY : null)));
        }
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<LanePoint>? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines[0] != Header)
            return null;

        var points = new List<LanePoint>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var fields = line.Split(';');
            if (fields.Length != 7
                || !TryNumber(fields[0], out var x)
                || !TryNumber(fields[1], out var y)
                || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lawValue)
                || !Enum.IsDefined(typeof(FadeCurve), lawValue)
                || !TryOptional(fields[3], out var outX)
                || !TryOptional(fields[4], out var outY)
                || !TryOptional(fields[5], out var inX)
                || !TryOptional(fields[6], out var inY)
                || x is < 0 or > 1 || y is < 0 or > 1
                || (outX is null) != (outY is null) || (inX is null) != (inY is null))
                return null;
            points.Add(new LanePoint(x, y, (FadeCurve)lawValue, outX, outY, inX, inY));
        }

        if (points.Count == 0 || points.Zip(points.Skip(1)).Any(pair => pair.First.X > pair.Second.X))
            return null;
        var anchor = points[0].X;
        return [.. points.Select(point => point with
        {
            X = point.X - anchor,
            OutHandleX = point.OutHandleX is { } outX ? outX - anchor : null,
            InHandleX = point.InHandleX is { } inX ? inX - anchor : null,
        })];
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Optional(double? value) => value is { } number ? Number(number) : "";

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

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
