using System.Globalization;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using S.Media.Session;

namespace HaCue2.Presentation;

/// <summary>
/// The built-in fade laws every curve picker offers, and how each one is drawn.
/// </summary>
/// <remarks>
/// <para>
/// This is product vocabulary, not sample data - it lived in <c>SampleShow</c>, which meant that file
/// could never be deleted however much of the real app landed, and its size stopped meaning anything.
/// A project's OWN custom curves are saved as presets on its document and are not here.
/// </para>
/// <para>
/// The order is load-bearing: the index into this list is the <c>FadeCurve</c> enum value, and
/// "custom" is one past the last law rather than a fifth member of it. A custom curve is never a new
/// enum member - enums round-trip numerically and the sidecar's other reader is the C ABI host, which
/// would decode an unknown member as a different valid law and quietly play the wrong shape.
/// </para>
/// </remarks>
public static class CurveLibrary
{
    private static readonly IReadOnlyList<CurveKnot> Rising =
        [new CurveKnot(0, 0), new CurveKnot(1, 1)];
    private static readonly IReadOnlyList<CurveKnot> Falling =
        [new CurveKnot(0, 1), new CurveKnot(1, 0)];

    /// <summary>The useful editable starting shape for each fade-bearing control. A custom choice must
    /// start in the same direction as the built-in law it replaces; otherwise selecting "custom" on
    /// a fade-out would immediately turn that fade into a fade-in.</summary>
    public static IReadOnlyList<CurveKnot> EmptyShape(string which) => which switch
    {
        "fadeOut" or "crossfade" or "fade" or "projectStopFade" => Falling,
        _ => Rising,
    };

    /// <summary>
    /// Thumbnail geometries in a 44 × 26 box, drawn top-left (unity) to bottom-right (silence) so a
    /// fade-out reads as a descent.
    /// </summary>
    public static IReadOnlyList<CurveOption> Curves { get; } =
    [
        new("linear", "M2,2 L42,24", FadeCurve.Linear),
        new("eq-power", "M2,2 Q30,4 42,24", FadeCurve.EqualPower),
        new("expo", "M2,2 Q10,22 42,24", FadeCurve.Exponential),
        new("s-curve", "M2,2 C18,2 26,24 42,24", FadeCurve.SCurve),
        new("custom ✎", "M2,2 L14,6 L22,18 L32,12 L42,24", IsCustom: true),
    ];

    /// <summary>The choices for one project: built-ins, its reusable drawings, then a new custom
    /// drawing. Project presets are real picker entries rather than data the document can store but
    /// the UI cannot reach.</summary>
    public static IReadOnlyList<CurveOption> Choices(HaCueProject project) =>
    [
        .. Curves.Where(option => !option.IsCustom),
        .. project.CurvePresets.Select(PresetOption),
        Curves.Single(option => option.IsCustom),
    ];

    private static CurveOption PresetOption(CurvePreset preset) => new(
        preset.Name.Length > 0 ? preset.Name : "unnamed preset",
        Thumbnail([
            .. preset.Points.Select(point => new CurveKnot(
                point.Progress, point.Level, point.Hold, point.CurveToNext,
                point.OutHandleX, point.OutHandleLevel, point.InHandleX, point.InHandleLevel)),
        ]),
        PresetId: preset.Id);

    /// <summary>Samples shaped segments into the polyline both editors draw. The handles remain the
    /// authored knots; these intermediate points are visual only.</summary>
    public static IReadOnlyList<CurvePoint> Shape(IReadOnlyList<CurveKnot> knots)
    {
        if (knots.Count == 0)
            return [];

        var shape = new List<CurvePoint> { new(knots[0].X, 1 - knots[0].Y) };
        if (knots.Count == 1)
            return shape;
        CustomFadeCurve? evaluator;
        try
        {
            evaluator = new CustomFadeCurve([
                .. knots.Select(knot => new FadeCurvePoint(
                    knot.X, knot.Y, knot.Hold, knot.CurveToNext,
                    knot.OutHandleX, knot.OutHandleY, knot.InHandleX, knot.InHandleY)),
            ]);
        }
        catch (ArgumentException)
        {
            // Invalid imported/hand-edited project data is reported by ProjectValidator. The editor
            // must still open so the operator can repair it, hence this visual-only law fallback.
            evaluator = null;
        }

        for (var index = 0; index + 1 < knots.Count; index++)
        {
            var from = knots[index];
            var to = knots[index + 1];

            if (from.Hold)
            {
                shape.Add(new CurvePoint(to.X, 1 - from.Y));
                shape.Add(new CurvePoint(to.X, 1 - to.Y));
                continue;
            }

            const int samples = 24;
            for (var step = 1; step <= samples; step++)
            {
                var progress = (double)step / samples;
                var x = from.X + ((to.X - from.X) * progress);
                var level = evaluator?.Evaluate(x) ?? FadeCurves.LevelBetween(
                    (float)from.Y,
                    (float)to.Y,
                    TimeSpan.FromSeconds(progress),
                    TimeSpan.FromSeconds(1),
                    from.CurveToNext);
                shape.Add(new CurvePoint(
                    x,
                    1 - level));
            }
        }

        return shape;
    }

    /// <summary>The selected knots' authored Bézier handles in canvas coordinates.</summary>
    public static IReadOnlyList<CurveTangent> Tangents(
        IReadOnlyList<CurveKnot> knots, IReadOnlySet<int> selected)
    {
        var handles = new List<CurveTangent>();
        foreach (var index in selected.Where(index => index >= 0 && index < knots.Count))
        {
            var knot = knots[index];
            if (knot.InHandleX is { } inX && knot.InHandleY is { } inY)
                handles.Add(new CurveTangent(
                    index, true, knot.X, 1 - knot.Y, inX, 1 - inY));
            if (knot.OutHandleX is { } outX && knot.OutHandleY is { } outY)
                handles.Add(new CurveTangent(
                    index, false, knot.X, 1 - knot.Y, outX, 1 - outY));
        }
        return handles;
    }

    private static string Thumbnail(IReadOnlyList<CurveKnot> knots)
    {
        var sampled = Shape(knots);
        if (sampled.Count < 2)
            return "M2,24 L42,2";

        return string.Join(
            " ",
            sampled.Select((point, index) => string.Create(
                CultureInfo.InvariantCulture,
                $"{(index == 0 ? 'M' : 'L')}{2 + (point.X * 40):0.###},{2 + (point.Y * 22):0.###}")));
    }
}
