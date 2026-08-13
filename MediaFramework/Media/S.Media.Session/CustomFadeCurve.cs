namespace S.Media.Session;

/// <summary>One point of a user-drawn fade shape, in normalized space.</summary>
/// <param name="Progress">Position along the fade, 0 (start) to 1 (end).</param>
/// <param name="Level">Gain multiplier at that position, 0 to 1.</param>
/// <param name="Hold">When true the level stays flat until the next point instead of interpolating
/// toward it - a step rather than a ramp.</param>
/// <param name="CurveToNext">The interpolation law from this point to the next. The default preserves
/// every document written before shaped custom segments were introduced.</param>
/// <param name="OutHandleX">Absolute normalized X of the outgoing cubic Bézier handle.</param>
/// <param name="OutHandleLevel">Absolute normalized level of the outgoing handle.</param>
/// <param name="InHandleX">Absolute normalized X of the incoming cubic Bézier handle.</param>
/// <param name="InHandleLevel">Absolute normalized level of the incoming handle. Tangents are
/// nullable and therefore additive: documents without them retain their previous segment law.</param>
public readonly record struct FadeCurvePoint(
    double Progress,
    double Level,
    bool Hold = false,
    FadeCurve CurveToNext = FadeCurve.Linear,
    double? OutHandleX = null,
    double? OutHandleLevel = null,
    double? InHandleX = null,
    double? InHandleLevel = null);

/// <summary>
/// A user-drawn fade shape: a normalized point list evaluated the same way a volume envelope is.
/// </summary>
/// <remarks>
/// <para>
/// A custom fade curve is structurally the same object as a volume envelope - a sorted point list with a
/// per-segment interpolation law - which is why this reuses <see cref="FadeCurves.LevelBetween"/> for
/// each segment rather than inventing a second interpolation. The difference is only that a curve is
/// normalized (progress and level both 0..1) so one shape can be applied to a fade of any duration and
/// any level range.
/// </para>
/// <para>
/// <b>Why this is not a new <see cref="FadeCurve"/> member.</b> The enum is serialized numerically in the
/// show document, and the document's only production reader outside this repo is the C ABI host. A new
/// enum member would therefore be silently misread as a different, valid law by anything built against
/// an older version - a wrong fade with no error. A separate nullable field is additive instead: readers
/// that do not know about it ignore it and fall back to the enum law, which is the documented rule for
/// evolving this format.
/// </para>
/// </remarks>
public sealed record CustomFadeCurve
{
    private readonly FadeCurvePoint[] _points;

    /// <param name="points">At least two points, sorted by progress. Endpoints need not sit exactly at
    /// 0 and 1 - evaluation clamps to the first and last.</param>
    public CustomFadeCurve(IReadOnlyList<FadeCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("a custom fade curve needs at least two points", nameof(points));

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Progress) || !double.IsFinite(p.Level))
                throw new ArgumentException("curve points must be finite", nameof(points));
            if (i > 0 && p.Progress < points[i - 1].Progress)
                throw new ArgumentException("curve points must be sorted by progress", nameof(points));
            ValidateHandle(p.InHandleX, p.InHandleLevel, nameof(points));
            ValidateHandle(p.OutHandleX, p.OutHandleLevel, nameof(points));
        }

        for (var i = 0; i + 1 < points.Count; i++)
        {
            var from = points[i];
            var to = points[i + 1];
            var hasOut = from.OutHandleX is not null;
            var hasIn = to.InHandleX is not null;
            if (hasOut != hasIn)
                throw new ArgumentException("a Bézier segment needs both endpoint handles", nameof(points));
            if (hasOut && (from.OutHandleX < from.Progress || from.OutHandleX > to.Progress
                           || to.InHandleX < from.Progress || to.InHandleX > to.Progress))
                throw new ArgumentException("Bézier handles must stay inside their segment", nameof(points));
        }
        if (points[0].InHandleX is not null || points[^1].OutHandleX is not null)
            throw new ArgumentException("curve endpoint handles must point into an existing segment", nameof(points));

        _points = [.. points];
    }

    public IReadOnlyList<FadeCurvePoint> Points => _points;

    /// <summary>
    /// The gain multiplier at <paramref name="progress"/> (0..1), clamped outside the point range.
    /// </summary>
    public float Evaluate(double progress)
    {
        if (progress <= _points[0].Progress)
            return (float)_points[0].Level;
        if (progress >= _points[^1].Progress)
            return (float)_points[^1].Level;

        for (var i = 1; i < _points.Length; i++)
        {
            var to = _points[i];
            if (progress > to.Progress)
                continue;

            var from = _points[i - 1];
            // A hold stays flat UNTIL the next point, so the step lands exactly ON that point - at
            // to.Progress the value is already the next level, not the held one.
            if (from.Hold)
                return (float)(progress >= to.Progress ? to.Level : from.Level);

            var span = to.Progress - from.Progress;
            if (span <= 0)
                return (float)to.Level;

            if (from.OutHandleX is { } outX
                && from.OutHandleLevel is { } outLevel
                && to.InHandleX is { } inX
                && to.InHandleLevel is { } inLevel)
                return (float)BezierLevel(
                    progress,
                    from.Progress, from.Level,
                    outX, outLevel,
                    inX, inLevel,
                    to.Progress, to.Level);

            // Same interpolation the envelope sampler uses, so a non-Bézier shape drawn in the editor
            // behaves identically whether it is applied as a fade or as automation. Linear remains the
            // serialized default, so old custom curves keep their exact shape.
            var t = (progress - from.Progress) / span;
            return FadeCurves.LevelBetween(
                (float)from.Level, (float)to.Level, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(1),
                from.CurveToNext);
        }

        return (float)_points[^1].Level;
    }

    private static void ValidateHandle(double? x, double? level, string parameter)
    {
        if ((x is null) != (level is null))
            throw new ArgumentException("a curve handle needs both coordinates", parameter);
        if (x is { } handleX
            && (!double.IsFinite(handleX) || !double.IsFinite(level!.Value)
                || handleX is < 0 or > 1 || level.Value is < 0 or > 1))
            throw new ArgumentException("curve handles must be finite and inside 0–1", parameter);
    }

    /// <summary>Evaluates a monotonic-X cubic Bézier at an authored X coordinate. X is inverted with a
    /// bounded binary search; handle X is constrained to the segment, so the solution is unique.</summary>
    private static double BezierLevel(
        double x,
        double x0, double y0,
        double x1, double y1,
        double x2, double y2,
        double x3, double y3)
    {
        var low = 0d;
        var high = 1d;
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var middle = (low + high) / 2;
            if (Cubic(x0, x1, x2, x3, middle) < x)
                low = middle;
            else
                high = middle;
        }

        return Cubic(y0, y1, y2, y3, (low + high) / 2);
    }

    private static double Cubic(double a, double b, double c, double d, double t)
    {
        var inverse = 1 - t;
        return (inverse * inverse * inverse * a)
               + (3 * inverse * inverse * t * b)
               + (3 * inverse * t * t * c)
               + (t * t * t * d);
    }

    /// <summary>Value-equality over the points, so two curves deserialized separately compare equal
    /// (record equality would compare the array by reference).</summary>
    public bool Equals(CustomFadeCurve? other) =>
        other is not null && _points.AsSpan().SequenceEqual(other._points);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var point in _points)
            hash.Add(point);
        return hash.ToHashCode();
    }
}

/// <summary>
/// How a fade is shaped: one of the built-in laws, or a user-drawn <see cref="CustomFadeCurve"/>.
/// </summary>
/// <remarks>
/// Implicitly converts from <see cref="FadeCurve"/>, so every existing call site keeps compiling and
/// keeps its behaviour - the custom case is purely additive.
/// </remarks>
public readonly record struct FadeShape(FadeCurve Law, CustomFadeCurve? Custom = null)
{
    public static implicit operator FadeShape(FadeCurve law) => new(law);

    /// <summary>True when this shape is a user-drawn curve rather than a built-in law.</summary>
    public bool IsCustom => Custom is not null;

    /// <summary>
    /// Reads a CUSTOM drawing from its far end - progress p evaluates the shape at 1−p.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the OTHER half of a crossfade. A built-in law needs nothing here: the session derives the two
    /// halves from one law by ramping down with <see cref="FadeCurves.LevelDown"/> and up with
    /// <see cref="FadeCurves.LevelUp"/>, which are already mirror images. A drawing has no law to invert
    /// - "used as drawn" is the rule everywhere else, and rightly so for a cue's own fade-in - so both
    /// halves of a crossfade evaluated the SAME falling shape and the incoming clip started at unity and
    /// faded out under the one it was replacing. A crossfade to silence, with a curve picker that looked
    /// like it was working.
    /// </para>
    /// <para>
    /// Ignored for a built-in law, deliberately: those are handed to the up-ramp and the down-ramp as
    /// they are, and mirroring one would undo exactly what makes the pair complementary.
    /// </para>
    /// </remarks>
    public bool Mirrored { get; init; }

    /// <summary>Shapes linear progress (0..1) into a gain.</summary>
    public float Evaluate(double progress) =>
        Custom is { } custom
            ? custom.Evaluate(Mirrored ? 1d - Math.Clamp(progress, 0d, 1d) : progress)
            : FadeCurves.LevelUp(
                TimeSpan.FromSeconds(Math.Clamp(progress, 0d, 1d)), TimeSpan.FromSeconds(1), Law);
}
