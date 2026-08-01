namespace S.Media.Session;

/// <summary>One point of a user-drawn fade shape, in normalized space.</summary>
/// <param name="Progress">Position along the fade, 0 (start) to 1 (end).</param>
/// <param name="Level">Gain multiplier at that position, 0 to 1.</param>
/// <param name="Hold">When true the level stays flat until the next point instead of interpolating
/// toward it - a step rather than a ramp.</param>
public readonly record struct FadeCurvePoint(double Progress, double Level, bool Hold = false);

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
        }

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

            // Same interpolation the envelope sampler uses, so a shape drawn in the editor behaves
            // identically whether it is applied as a fade or as automation.
            var t = (progress - from.Progress) / span;
            return FadeCurves.LevelBetween(
                (float)from.Level, (float)to.Level, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(1),
                FadeCurve.Linear);
        }

        return (float)_points[^1].Level;
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

    /// <summary>Shapes linear progress (0..1) into a gain.</summary>
    public float Evaluate(double progress) =>
        Custom is { } custom
            ? custom.Evaluate(progress)
            : FadeCurves.LevelUp(
                TimeSpan.FromSeconds(Math.Clamp(progress, 0d, 1d)), TimeSpan.FromSeconds(1), Law);
}
