using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Journal;

/// <summary>One point of an editable curve, in normalized space.</summary>
/// <param name="Hold">Flat until the next point instead of ramping toward it. Curves only.</param>
public readonly record struct CurveKnot(
    double X,
    double Y,
    bool Hold = false,
    FadeCurve CurveToNext = FadeCurve.Linear);

/// <summary>
/// Something the curve editor can edit.
/// </summary>
/// <remarks>
/// A fade curve and an automation lane are the same object with different names — a sorted list of
/// normalized points — which is why the plan insists on one editor for both. They differ in exactly
/// two ways, both captured here: a lane's points have no hold flag, and the two live on different
/// document types. Everything else, including every edit below, is shared.
/// </remarks>
public interface ICurveTarget
{
    /// <summary>What an undo entry is ABOUT — the cue, or the preset.</summary>
    Guid Subject { get; }

    /// <summary>Distinguishes two curves on one subject, e.g. a cue's fade-in from its fade-out.</summary>
    string Property { get; }

    bool SupportsHold { get; }

    /// <summary>
    /// Whether the document actually HOLDS a curve, as opposed to <see cref="Read"/> inventing the
    /// straight line an untouched editor opens on.
    /// </summary>
    bool HasStored { get; }

    /// <summary>
    /// The named law beside the points, when this target has one.
    /// </summary>
    /// <remarks>
    /// Null for a preset and for an automation lane, and the difference is real rather than an
    /// omission: a preset IS a drawn shape and a lane IS a drawn shape, so "equal power" is not
    /// something either of them can be. Only a cue's <c>CurveSpec</c> carries a law that the drawn
    /// points then override, which is why picking a named curve there has to CLEAR them.
    /// </remarks>
    FadeCurve? Law { get; }

    /// <summary>Writes the named law. Only reached when <see cref="Law"/> is not null.</summary>
    void WriteLaw(FadeCurve law);

    /// <summary>The reusable project shape currently winning over the law/inline points.</summary>
    Guid? PresetId { get; }

    /// <summary>Writes or clears the reusable project shape. Non-spec targets ignore null.</summary>
    void WritePreset(Guid? presetId);

    IReadOnlyList<CurveKnot> Read();

    void Write(IReadOnlyList<CurveKnot> knots);

    /// <summary>
    /// Puts the document back to having no curve at all.
    /// </summary>
    /// <remarks>
    /// Needed because undoing the FIRST edit of an untouched curve must restore absence, not the line
    /// the editor opened on. A stored straight line is not the same document: an inline point list
    /// beats the chosen law, so the undo would have quietly replaced equal-power with linear.
    /// </remarks>
    void Clear();
}

/// <summary>A cue's fade curve, held inline on its <see cref="CurveSpec"/>.</summary>
public sealed class CurveSpecTarget(
    Guid subject,
    string property,
    CurveSpec spec,
    HaCueProject? project = null,
    IReadOnlyList<CurveKnot>? emptyShape = null) : ICurveTarget
{
    public Guid Subject { get; } = subject;
    public string Property { get; } = property;
    public bool SupportsHold => true;
    public bool HasStored => spec.Points is { Count: > 1 };

    public IReadOnlyList<CurveKnot> Read() =>
        spec.Points is { Count: > 1 } points
            ? [.. points.Select(point => new CurveKnot(
                point.Progress, point.Level, point.Hold, point.CurveToNext))]
            : spec.PresetId is { } presetId
              && project?.CurvePresets.FirstOrDefault(candidate => candidate.Id == presetId) is { Points.Count: > 1 } preset
                ? [.. preset.Points.Select(point => new CurveKnot(
                    point.Progress, point.Level, point.Hold, point.CurveToNext))]
            // A spec that has never been drawn on opens with the owning control's natural direction.
            // Fade-ins rise while fade-outs and stops fall. Nothing is written until an edit happens.
            : emptyShape is { Count: > 1 }
                ? emptyShape
                : [new CurveKnot(0, 0), new CurveKnot(1, 1)];

    public void Write(IReadOnlyList<CurveKnot> knots) =>
        spec.Points = [.. knots.Select(knot => new FadeCurvePoint(
            knot.X, knot.Y, knot.Hold, knot.CurveToNext))];

    public void Clear() => spec.Points = null;

    public FadeCurve? Law => spec.Law;

    public void WriteLaw(FadeCurve law) => spec.Law = law;

    public Guid? PresetId => spec.PresetId;

    public void WritePreset(Guid? presetId) => spec.PresetId = presetId;
}

/// <summary>A named project curve preset.</summary>
public sealed class CurvePresetTarget(CurvePreset preset) : ICurveTarget
{
    public Guid Subject { get; } = preset.Id;
    public string Property => "preset";
    public bool SupportsHold => true;
    public bool HasStored => preset.Points.Count > 1;

    public IReadOnlyList<CurveKnot> Read() =>
        preset.Points is { Count: > 1 }
            ? [.. preset.Points.Select(point => new CurveKnot(
                point.Progress, point.Level, point.Hold, point.CurveToNext))]
            : [new CurveKnot(0, 0), new CurveKnot(1, 1)];

    public void Write(IReadOnlyList<CurveKnot> knots) =>
        preset.Points = [.. knots.Select(knot => new FadeCurvePoint(
            knot.X, knot.Y, knot.Hold, knot.CurveToNext))];

    public void Clear() => preset.Points = [];

    /// <summary>A preset IS the drawn shape. There is no law for a named curve to fall back to.</summary>
    public FadeCurve? Law => null;

    public void WriteLaw(FadeCurve law) => throw new NotSupportedException("a preset has no law");

    public Guid? PresetId => null;

    public void WritePreset(Guid? presetId)
    {
        if (presetId is not null)
            throw new NotSupportedException("a preset cannot select another preset");
    }
}

/// <summary>An automation lane on a cue (register item 18).</summary>
public sealed class EffectLaneTarget(Guid subject, EffectLane lane) : ICurveTarget
{
    public Guid Subject { get; } = subject;
    public string Property { get; } = $"lane:{lane.Id}";

    /// <summary>A lane ramps continuously; there is no step in the model to write a hold into.</summary>
    public bool SupportsHold => false;

    public bool HasStored => lane.Points.Count > 1;

    public IReadOnlyList<CurveKnot> Read() =>
        lane.Points is { Count: > 1 }
            ? [.. lane.Points.Select(point => new CurveKnot(
                point.X, point.Y, CurveToNext: point.CurveToNext))]
            : [new CurveKnot(0, 1), new CurveKnot(1, 1)];

    public void Write(IReadOnlyList<CurveKnot> knots) =>
        lane.Points = [.. knots.Select(knot => new LanePoint(knot.X, knot.Y, knot.CurveToNext))];

    public void Clear() => lane.Points = [];

    /// <summary>A lane is drawn, not chosen. Same reason a preset has no law.</summary>
    public FadeCurve? Law => null;

    public void WriteLaw(FadeCurve law) => throw new NotSupportedException("a lane has no law");

    public Guid? PresetId => null;

    public void WritePreset(Guid? presetId)
    {
        if (presetId is not null)
            throw new NotSupportedException("an automation lane cannot select a fade preset");
    }
}

/// <summary>
/// Replaces a curve's whole point list as one undoable step.
/// </summary>
/// <remarks>
/// Whole-list rather than per-point, unlike most commands here, because the list's SHAPE is what
/// changes: adding a point renumbers everything after it, and dragging one past its neighbour re-sorts
/// them. A per-index command would have to describe that renumbering, and an undo that replayed it in
/// the wrong order would leave a curve nobody drew. The lists are a handful of points.
/// </remarks>
public sealed class SetCurveCommand : ICoalescingCommand
{
    private readonly ICurveTarget _target;
    private readonly IReadOnlyList<CurveKnot> _before;

    /// <summary>Whether there WAS a curve before this command — see <see cref="ICurveTarget.Clear"/>.</summary>
    private readonly bool _existed;

    private readonly Guid? _presetBefore;

    private IReadOnlyList<CurveKnot> _after;

    public SetCurveCommand(ICurveTarget target, IReadOnlyList<CurveKnot> knots, string description)
    {
        _target = target;
        _existed = target.HasStored;
        _presetBefore = target.PresetId;
        _before = target.Read();
        _after = knots;
        Key = new CoalesceKey(target.Subject, target.Property);
        Description = description;
    }

    public CoalesceKey Key { get; }
    public string Domain => "cues";
    public string Description { get; }

    public void Apply(HaCueProject project)
    {
        // Drawing detaches a cue from a named preset. Otherwise the preset continues to win in
        // CurveSpec.Resolve and the canvas edit is saved but never heard.
        _target.WritePreset(null);
        _target.Write(_after);
    }

    public void Revert(HaCueProject project)
    {
        if (_existed)
            _target.Write(_before);
        else
            _target.Clear();

        _target.WritePreset(_presetBefore);
    }

    public void MergeFrom(ICoalescingCommand newer)
    {
        if (newer is SetCurveCommand other)
            _after = other._after;
    }
}

/// <summary>
/// Picks a named law, and drops the drawn points that would otherwise beat it.
/// </summary>
/// <remarks>
/// <b>Both halves, or neither.</b> <c>CurveSpec.Resolve</c> follows preset → inline points → law, so
/// setting the law while an inline list survives changes nothing an operator can hear: they would pick
/// "linear", watch the canvas keep their custom shape, and reasonably conclude the control is broken.
/// Undo restores both, which is the whole reason this is one command rather than two.
/// </remarks>
public sealed class SetCurveLawCommand : IProjectCommand
{
    private readonly ICurveTarget _target;
    private readonly FadeCurve _before;
    private readonly IReadOnlyList<CurveKnot> _points;
    private readonly bool _existed;
    private readonly FadeCurve _after;
    private readonly Guid? _presetBefore;

    public SetCurveLawCommand(ICurveTarget target, FadeCurve law, string description)
    {
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _before = target.Law ?? law;
        _existed = target.HasStored;
        _points = target.Read();
        _after = law;
        _presetBefore = target.PresetId;
        Description = description;
    }

    public string Domain => "cues";
    public string Description { get; }

    public void Apply(HaCueProject project)
    {
        _target.WriteLaw(_after);
        _target.WritePreset(null);
        _target.Clear();
    }

    public void Revert(HaCueProject project)
    {
        _target.WriteLaw(_before);

        // Only if there WAS a drawn curve. Writing the straight line the editor opens on would leave
        // an inline list that beats the law we just restored — the same trap Clear() exists for.
        if (_existed)
            _target.Write(_points);
        else
            _target.Clear();

        _target.WritePreset(_presetBefore);
    }
}

/// <summary>Selects a reusable project curve, clearing inline points that would otherwise be hidden
/// underneath it. Undo restores the complete former source of the curve.</summary>
public sealed class SetCurvePresetCommand : IProjectCommand
{
    private readonly ICurveTarget _target;
    private readonly Guid? _beforePreset;
    private readonly IReadOnlyList<CurveKnot> _beforePoints;
    private readonly bool _hadPoints;
    private readonly Guid _afterPreset;

    public SetCurvePresetCommand(ICurveTarget target, Guid presetId, string description)
    {
        _target = target;
        _beforePreset = target.PresetId;
        _beforePoints = target.Read();
        _hadPoints = target.HasStored;
        _afterPreset = presetId;
        Description = description;
    }

    public string Domain => "cues";
    public string Description { get; }

    public void Apply(HaCueProject project)
    {
        _target.Clear();
        _target.WritePreset(_afterPreset);
    }

    public void Revert(HaCueProject project)
    {
        if (_hadPoints)
            _target.Write(_beforePoints);
        else
            _target.Clear();
        _target.WritePreset(_beforePreset);
    }
}

/// <summary>The edits a curve editor makes: move a point, add one, remove one, toggle its hold.</summary>
public static class CurveEdits
{
    /// <summary>Two is the fewest a curve can have and still be a shape.</summary>
    public const int MinimumPoints = 2;

    /// <summary>How close counts as the same point when adding — in fractions of the canvas.</summary>
    private const double SamePointDistance = 0.01;

    public static SetCurveCommand Move(ICurveTarget target, int index, double x, double y)
    {
        var knots = target.Read().ToList();
        if (index < 0 || index >= knots.Count)
            return new SetCurveCommand(target, Normalize(knots), "move curve point");

        knots[index] = knots[index] with { X = x, Y = y };
        return new SetCurveCommand(target, Normalize(knots), "move curve point");
    }

    public static SetCurveCommand Add(ICurveTarget target, double x, double y)
    {
        var knots = target.Read().ToList();
        knots.Add(new CurveKnot(x, y));
        return new SetCurveCommand(target, Normalize(knots), "add curve point");
    }

    /// <summary>
    /// Removes a point, unless it is one of the last two.
    /// </summary>
    /// <remarks>
    /// Refusing rather than allowing-and-repairing: the engine's <c>CustomFadeCurve</c> throws on
    /// fewer than two points, and a curve that threw when the show ran would be a fade that did not
    /// happen. The gesture that reaches here is "drag a point off the canvas", which is easy to do by
    /// accident.
    /// </remarks>
    public static SetCurveCommand? Remove(ICurveTarget target, int index)
    {
        var knots = target.Read().ToList();
        if (index < 0 || index >= knots.Count || knots.Count <= MinimumPoints)
            return null;

        knots.RemoveAt(index);
        return new SetCurveCommand(target, Normalize(knots), "remove curve point");
    }

    /// <summary>
    /// The named laws the picker offers, in the order it draws them.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the thumbnails, because this is the list that has to agree with the
    /// DOCUMENT. The thumbnails are drawings of these; getting them out of step would be a picker whose
    /// pictures and effects disagree, which is the one failure nobody would look for.
    /// </remarks>
    public static IReadOnlyList<FadeCurve> Laws { get; } =
        [FadeCurve.Linear, FadeCurve.EqualPower, FadeCurve.Exponential, FadeCurve.SCurve];

    /// <summary>
    /// Picks a named law for a target that has one, or refuses.
    /// </summary>
    /// <remarks>
    /// Null for a preset or a lane, which have no law to set — see <see cref="ICurveTarget.Law"/>. Also
    /// null when the law is ALREADY the one asked for and nothing is drawn over it: re-selecting the
    /// current entry is what happens when the picker is rebuilt, and it must not push an undo step
    /// nobody performed.
    /// </remarks>
    public static SetCurveLawCommand? PickLaw(ICurveTarget target, FadeCurve law)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Law is not { } current)
            return null;

        return current == law && !target.HasStored
            ? null
            : new SetCurveLawCommand(target, law, $"use the {Name(law)} curve");
    }

    /// <summary>Where a law sits in <see cref="Laws"/>, or −1 for one the picker does not offer.</summary>
    public static int LawIndex(FadeCurve law)
    {
        for (var index = 0; index < Laws.Count; index++)
        {
            if (Laws[index] == law)
                return index;
        }

        return -1;
    }

    /// <summary>The operator-facing name of a law, matching the picker's captions.</summary>
    public static string Name(FadeCurve law) => law switch
    {
        FadeCurve.Linear => "linear",
        FadeCurve.Exponential => "expo",
        FadeCurve.SCurve => "s-curve",
        _ => "eq-power",
    };

    public static SetCurveCommand? SetHold(ICurveTarget target, int index, bool hold)
    {
        if (!target.SupportsHold)
            return null;

        var knots = target.Read().ToList();
        if (index < 0 || index >= knots.Count)
            return null;

        knots[index] = knots[index] with { Hold = hold };
        return new SetCurveCommand(target, Normalize(knots), hold ? "hold segment" : "ramp segment");
    }

    /// <summary>Shapes the segment beginning at one point. A lane and a fade use the same laws all the
    /// way through compilation and runtime evaluation.</summary>
    public static SetCurveCommand? SetSegment(ICurveTarget target, int index, FadeCurve curve)
    {
        var knots = target.Read().ToList();
        if (index < 0 || index >= knots.Count)
            return null;

        knots[index] = knots[index] with { Hold = false, CurveToNext = curve };
        return new SetCurveCommand(target, Normalize(knots), $"use {Name(curve)} segment");
    }

    /// <summary>Chooses one of the project's reusable shapes for a cue curve.</summary>
    public static SetCurvePresetCommand? PickPreset(ICurveTarget target, Guid presetId, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Law is null || target.PresetId == presetId)
            return null;

        return new SetCurvePresetCommand(target, presetId, $"use curve preset {name}");
    }

    /// <summary>
    /// Clamps every point into the canvas and puts them back in order.
    /// </summary>
    /// <remarks>
    /// Sorting here rather than forbidding a drag past a neighbour: dragging one point across another
    /// is a normal way to reshape a curve, and a drag that stuck at a neighbour would feel broken. The
    /// engine requires the list sorted, so it is sorted on the way in rather than checked on the way
    /// out. The sort is STABLE, so two points at the same x keep the order they were drawn in.
    /// </remarks>
    private static IReadOnlyList<CurveKnot> Normalize(List<CurveKnot> knots) =>
    [
        .. knots
            .Select(knot => knot with { X = Fraction(knot.X), Y = Fraction(knot.Y) })
            .OrderBy(knot => knot.X),
    ];

    /// <summary>
    /// A number into 0..1, treating anything non-finite as 0.
    /// </summary>
    /// <remarks>
    /// <c>Math.Clamp(NaN, 0, 1)</c> returns NaN, so clamping alone is not enough — and a lane measured
    /// before it has been laid out divides by a zero width, which is exactly where a NaN comes from.
    /// <c>CustomFadeCurve</c> rejects non-finite points, so one reaching the document would be a fade
    /// that threw when the show ran rather than when it was drawn.
    /// </remarks>
    private static double Fraction(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>Whether a point already sits where one is about to be added.</summary>
    public static bool HasPointNear(ICurveTarget target, double x) =>
        target.Read().Any(knot => Math.Abs(knot.X - x) < SamePointDistance);
}
