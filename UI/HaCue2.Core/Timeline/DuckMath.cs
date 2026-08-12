using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Core.Timeline;

/// <summary>A span on a group's timeline, in milliseconds from the group's start.</summary>
public readonly record struct TimelineSpan(int StartMs, int EndMs)
{
    public int LengthMs => EndMs - StartMs;
}

/// <summary>
/// Splices ducking dips into a bed's volume automation track.
/// </summary>
/// <remarks>
/// <para>
/// "Duck under…" is an AUTHORING helper, not a runtime effect: it writes ordinary keyframes into the
/// bed's own volume track, and once written they are indistinguishable from ones somebody dragged. That
/// is the point — the operator can see exactly what will happen and fix it by hand, rather than
/// trusting a live side-chain nobody can inspect during a show.
/// </para>
/// <para>
/// Ported from HaPlay's <c>TimelineDuckMath</c>, which has been doing this for real shows. Two
/// Automation keys use absolute clip milliseconds and absolute dB, so ducking no longer needs the media
/// duration merely to reinterpret normalized coordinates and never loses its meaning after a trim edit.
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
    /// Writes a dip into the bed's track for every overlapping voice span.
    /// </summary>
    /// <param name="points">The bed's existing volume track. Never mutated.</param>
    /// <param name="bedStartMs">Where the bed sits on the group timeline.</param>
    /// <param name="bedLengthMs">How long the bed plays for. Zero or less means nothing to duck.</param>
    /// <param name="voices">Spans on the SAME group timeline that should push the bed down.</param>
    /// <param name="depthDb">How far down, in decibels. Negative.</param>
    /// <param name="rampMs">How long each ramp takes.</param>
    /// <param name="leadMs">How early the dip starts before the voice, and how late it recovers.</param>
    /// <remarks>
    /// Per merged overlap the dip spans <c>[start − lead − ramp, end + lead + ramp]</c> in bed time,
    /// with four points: restore, depth, depth, restore. The restore LEVELS are the bed's own track
    /// sampled at the dip edges — a bed already riding at −6 dB stays at −6 outside the dip rather
    /// than being yanked to unity. Existing points inside a dip are replaced; ones outside survive.
    /// </remarks>
    public static IReadOnlyList<AutomationKeyframe> ApplyDucks(
        IReadOnlyList<AutomationKeyframe> points,
        int bedStartMs,
        int bedLengthMs,
        IEnumerable<TimelineSpan> voices,
        double depthDb,
        int rampMs,
        int leadMs,
        double authoredLevelDb = 0)
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

        var result = points;

        // Left to right, so each dip samples the lane as already spliced. Merged footprints are
        // disjoint, so a later dip never disturbs an earlier one.
        foreach (var dip in Merge(footprints))
            result = Splice(result, dip, bedLengthMs, depthDb, rampMs, authoredLevelDb);

        return result;
    }

    /// <summary>Schema-1 normalized-lane adapter retained so old project fixtures exercise migration.</summary>
    public static IReadOnlyList<LanePoint> ApplyDucks(
        IReadOnlyList<LanePoint> points,
        int bedStartMs,
        int bedLengthMs,
        IEnumerable<TimelineSpan> voices,
        double depthDb,
        int rampMs,
        int leadMs)
    {
        if (bedLengthMs <= 0)
            return points;
        rampMs = Math.Max(0, rampMs);
        leadMs = Math.Max(0, leadMs);
        var pad = leadMs + rampMs;
        var footprints = voices
            .Select(voice => new TimelineSpan(
                voice.StartMs - bedStartMs - pad,
                voice.EndMs - bedStartMs + pad))
            .Where(span => span.EndMs > span.StartMs && span.EndMs > 0 && span.StartMs < bedLengthMs)
            .ToList();
        if (footprints.Count == 0)
            return points;
        IReadOnlyList<LanePoint> result = points;
        foreach (var dip in Merge(footprints))
            result = SpliceLegacy(result, dip, bedLengthMs, Factor(depthDb), rampMs);
        return result;
    }

    /// <summary>The lane's level at a position, interpolating between points as playback would.</summary>
    public static double Sample(
        IReadOnlyList<AutomationKeyframe> points,
        long timeMs,
        double authoredLevelDb = 0)
    {
        if (points.Count == 0)
            return authoredLevelDb;

        var ordered = points.OrderBy(point => point.TimeMs).ThenBy(point => point.Id).ToList();
        if (timeMs <= ordered[0].TimeMs)
            return ordered[0].Value;

        for (var index = 1; index < ordered.Count; index++)
        {
            if (timeMs > ordered[index].TimeMs)
                continue;

            var (previous, next) = (ordered[index - 1], ordered[index]);
            var span = next.TimeMs - previous.TimeMs;
            if (previous.Hold)
                return previous.Value;
            var progress = span <= 0 ? 1 : (double)(timeMs - previous.TimeMs) / span;
            var shaped = FadeCurves.ShapeProgress(progress, previous.Curve.Law);

            return span <= 0
                ? next.Value
                : previous.Value + ((next.Value - previous.Value) * shaped);
        }

        return ordered[^1].Value;
    }

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
            catch (ArgumentException) { }
        }
        for (var index = 1; index < points.Count; index++)
        {
            if (x > points[index].X)
                continue;
            var previous = points[index - 1];
            var next = points[index];
            var span = next.X - previous.X;
            return span <= 0 ? next.Y : previous.Y + ((next.Y - previous.Y) * ((x - previous.X) / span));
        }
        return points[^1].Y;
    }

    private static IReadOnlyList<AutomationKeyframe> Splice(
        IReadOnlyList<AutomationKeyframe> points,
        TimelineSpan dip,
        int bedLengthMs,
        double depthDb,
        int rampMs,
        double authoredLevelDb)
    {
        // t0..t3 = restore-in, depth-in, depth-out, restore-out. The footprint is at least two ramps
        // long by construction, so t1 never passes t2.
        long At(int milliseconds) => Math.Clamp(milliseconds, 0, bedLengthMs);

        var t0 = At(dip.StartMs);
        var t1 = At(dip.StartMs + rampMs);
        var t2 = At(dip.EndMs - rampMs);
        var t3 = At(dip.EndMs);

        // Sampled BEFORE anything is removed, and at the unclamped edges, so a bed riding at −6 dB
        // returns to −6 rather than to unity.
        var restoreIn = Sample(points, t0, authoredLevelDb);
        var restoreOut = Sample(points, t3, authoredLevelDb);

        var before = points.Where(point => point.TimeMs < t0).ToList();
        var after = points.Where(point => point.TimeMs > t3).ToList();

        var spliced = new List<AutomationKeyframe>(points.Count + 4);
        spliced.AddRange(before);
        spliced.AddRange(after);

        // A ramp squeezed off the clip edge drops its restore point: holding the depth to the edge is
        // what "the voice-over is already talking when the bed starts" should sound like.
        if (t0 > 0)
            spliced.Add(Key(t0, restoreIn));

        spliced.Add(Key(t1, Math.Clamp(restoreIn + depthDb, GainRange.SilenceFloorDb, 12)));
        spliced.Add(Key(t2, Math.Clamp(restoreOut + depthDb, GainRange.SilenceFloorDb, 12)));

        if (t3 < bedLengthMs)
            spliced.Add(Key(t3, restoreOut));

        return [.. spliced.OrderBy(point => point.TimeMs).ThenBy(point => point.Id)];
    }

    private static AutomationKeyframe Key(long timeMs, double value) => new()
    {
        TimeMs = timeMs,
        Value = value,
        Curve = new CurveSpec { Law = FadeCurve.Linear },
    };

    private static IReadOnlyList<LanePoint> SpliceLegacy(
        IReadOnlyList<LanePoint> points, TimelineSpan dip, int bedLengthMs, double factor, int rampMs)
    {
        double At(int milliseconds) => Math.Clamp((double)milliseconds / bedLengthMs, 0, 1);
        var t0 = At(dip.StartMs);
        var t1 = At(dip.StartMs + rampMs);
        var t2 = At(dip.EndMs - rampMs);
        var t3 = At(dip.EndMs);
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
        if (t0 > 0)
            spliced.Add(new LanePoint(t0, restoreIn));
        spliced.Add(new LanePoint(t1, Math.Clamp(restoreIn * factor, 0, 1)));
        spliced.Add(new LanePoint(t2, Math.Clamp(restoreOut * factor, 0, 1)));
        if (t3 < 1)
            spliced.Add(new LanePoint(t3, restoreOut));
        return [.. spliced.OrderBy(point => point.X)];
    }

    private static double Factor(double decibels) =>
        decibels <= GainRange.SilenceFloorDb ? 0 : Math.Pow(10, decibels / 20);
}
