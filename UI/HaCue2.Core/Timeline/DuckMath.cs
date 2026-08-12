using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Timeline;

/// <summary>A span on a group's timeline, in milliseconds from the group's start.</summary>
public readonly record struct TimelineSpan(int StartMs, int EndMs)
{
    public int LengthMs => EndMs - StartMs;
}

/// <summary>
/// Splices ducking dips into a bed's volume lane.
/// </summary>
/// <remarks>
/// <para>
/// "Duck under…" is an AUTHORING helper, not a runtime effect: it writes ordinary keyframes into the
/// bed's own volume lane, and once written they are indistinguishable from ones somebody dragged. That
/// is the point — the operator can see exactly what will happen and fix it by hand, rather than
/// trusting a live side-chain nobody can inspect during a show.
/// </para>
/// <para>
/// Ported from HaPlay's <c>TimelineDuckMath</c>, which has been doing this for real shows. Two
/// deliberate differences: HaCue2 lane points are NORMALIZED (x is a fraction of the cue, y a 0..1
/// level factor) rather than clip milliseconds, and they carry no per-point curve — so the ramps here
/// are linear. A shaped ramp would need a curve on <see cref="LanePoint"/>, which is a model change
/// worth making only if a linear one turns out to be audible.
/// </para>
/// </remarks>
public static class DuckMath
{
    /// <summary>Touching edges do NOT overlap — a voice-over starting exactly at the bed's end needs no duck.</summary>
    public static bool Overlaps(TimelineSpan a, TimelineSpan b) =>
        a.StartMs < b.EndMs && b.StartMs < a.EndMs;

    /// <summary>
    /// Sorts and merges overlapping or touching spans.
    /// </summary>
    /// <remarks>
    /// Adjacent spans merge so back-to-back voice-overs make ONE dip — the alternative is the bed
    /// bobbing up between two sentences, which is the artefact ducking is supposed to prevent.
    /// </remarks>
    public static IReadOnlyList<TimelineSpan> Merge(IEnumerable<TimelineSpan> spans)
    {
        var merged = new List<TimelineSpan>();

        foreach (var span in spans.Where(span => span.LengthMs > 0)
                     .OrderBy(span => span.StartMs).ThenBy(span => span.EndMs))
        {
            if (merged.Count > 0 && span.StartMs <= merged[^1].EndMs)
                merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, span.EndMs) };
            else
                merged.Add(span);
        }

        return merged;
    }

    /// <summary>
    /// Writes a dip into the bed's lane for every overlapping voice span.
    /// </summary>
    /// <param name="points">The bed's existing volume lane. Never mutated.</param>
    /// <param name="bedStartMs">Where the bed sits on the group timeline.</param>
    /// <param name="bedLengthMs">How long the bed plays for. Zero or less means nothing to duck.</param>
    /// <param name="voices">Spans on the SAME group timeline that should push the bed down.</param>
    /// <param name="depthDb">How far down, in decibels. Negative.</param>
    /// <param name="rampMs">How long each ramp takes.</param>
    /// <param name="leadMs">How early the dip starts before the voice, and how late it recovers.</param>
    /// <remarks>
    /// Per merged overlap the dip spans <c>[start − lead − ramp, end + lead + ramp]</c> in bed time,
    /// with four points: restore, depth, depth, restore. The restore LEVELS are the bed's own lane
    /// sampled at the dip edges — a bed already riding at −6 dB stays at −6 outside the dip rather
    /// than being yanked to unity. Existing points inside a dip are replaced; ones outside survive.
    /// </remarks>
    public static IReadOnlyList<LanePoint> ApplyDucks(
        IReadOnlyList<LanePoint> points,
        int bedStartMs,
        int bedLengthMs,
        IEnumerable<TimelineSpan> voices,
        double depthDb,
        int rampMs,
        int leadMs)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (bedLengthMs <= 0)
            return points;

        rampMs = Math.Max(0, rampMs);
        leadMs = Math.Max(0, leadMs);

        // Timeline → bed time, padded by lead+ramp. Merging happens on the PADDED spans, so two dips
        // that would collide fuse into one hold instead of bobbing up between them.
        var pad = leadMs + rampMs;
        var footprints = new List<TimelineSpan>();

        foreach (var voice in voices)
        {
            var start = voice.StartMs - bedStartMs;
            var end = voice.EndMs - bedStartMs;

            if (end <= start || end <= 0 || start >= bedLengthMs)
                continue;

            footprints.Add(new TimelineSpan(start - pad, end + pad));
        }

        if (footprints.Count == 0)
            return points;

        var factor = Factor(depthDb);
        var result = points;

        // Left to right, so each dip samples the lane as already spliced. Merged footprints are
        // disjoint, so a later dip never disturbs an earlier one.
        foreach (var dip in Merge(footprints))
            result = Splice(result, dip, bedLengthMs, factor, rampMs);

        return result;
    }

    /// <summary>The lane's level at a position, interpolating between points as playback would.</summary>
    public static double Sample(IReadOnlyList<LanePoint> points, double x)
    {
        if (points.Count == 0)
            return 1;

        if (x <= points[0].X)
            return points[0].Y;

        if (points.Count > 1)
        {
            try
            {
                return new CustomFadeCurve([
                    .. points.Select(point => new FadeCurvePoint(
                        point.X, point.Y, CurveToNext: point.CurveToNext,
                        OutHandleX: point.OutHandleX, OutHandleLevel: point.OutHandleY,
                        InHandleX: point.InHandleX, InHandleLevel: point.InHandleY)),
                ]).Evaluate(x);
            }
            catch (ArgumentException)
            {
                // Validation reports malformed authored tangents; ducking can still fall back to the
                // legacy linear sampler so an operator can repair the lane rather than losing the tool.
            }
        }

        for (var index = 1; index < points.Count; index++)
        {
            if (x > points[index].X)
                continue;

            var (previous, next) = (points[index - 1], points[index]);
            var span = next.X - previous.X;

            return span <= 0
                ? next.Y
                : previous.Y + ((next.Y - previous.Y) * ((x - previous.X) / span));
        }

        return points[^1].Y;
    }

    private static IReadOnlyList<LanePoint> Splice(
        IReadOnlyList<LanePoint> points, TimelineSpan dip, int bedLengthMs, double factor, int rampMs)
    {
        // t0..t3 = restore-in, depth-in, depth-out, restore-out. The footprint is at least two ramps
        // long by construction, so t1 never passes t2.
        double At(int milliseconds) => Math.Clamp((double)milliseconds / bedLengthMs, 0, 1);

        var t0 = At(dip.StartMs);
        var t1 = At(dip.StartMs + rampMs);
        var t2 = At(dip.EndMs - rampMs);
        var t3 = At(dip.EndMs);

        // Sampled BEFORE anything is removed, and at the unclamped edges, so a bed riding at −6 dB
        // returns to −6 rather than to unity.
        var restoreIn = Sample(points, t0);
        var restoreOut = Sample(points, t3);

        var before = points.Where(point => point.X < t0).ToList();
        var after = points.Where(point => point.X > t3).ToList();
        if (before.Count > 0)
            before[^1] = before[^1] with { OutHandleX = null, OutHandleY = null };
        if (after.Count > 0)
            after[0] = after[0] with { InHandleX = null, InHandleY = null };

        var spliced = new List<LanePoint>(points.Count + 4);
        spliced.AddRange(before);
        spliced.AddRange(after);

        // A ramp squeezed off the clip edge drops its restore point: holding the depth to the edge is
        // what "the voice-over is already talking when the bed starts" should sound like.
        if (t0 > 0)
            spliced.Add(new LanePoint(t0, restoreIn));

        spliced.Add(new LanePoint(t1, Math.Clamp(restoreIn * factor, 0, 1)));
        spliced.Add(new LanePoint(t2, Math.Clamp(restoreOut * factor, 0, 1)));

        if (t3 < 1)
            spliced.Add(new LanePoint(t3, restoreOut));

        return [.. spliced.OrderBy(point => point.X)];
    }

    /// <summary>Decibels as a level factor; the document's silence floor maps to zero.</summary>
    private static double Factor(double decibels) =>
        decibels <= GainRange.SilenceFloorDb ? 0 : Math.Pow(10, decibels / 20);
}
