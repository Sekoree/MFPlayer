using System;
using System.Collections.Generic;
using System.Linq;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>One half-open [Start, End) span in ms - on the group's timeline epoch or on a clip's
/// own (post-StartOffset) axis, per the consuming function's contract.</summary>
internal readonly record struct TimelineIntervalMs(int StartMs, int EndMs)
{
    public int LengthMs => EndMs - StartMs;
}

/// <summary>
/// Pure math for the timeline editor's "Duck under…" authoring helper (timeline doc Phase D,
/// "sidechain-lite"): overlap detection, interval merging and the envelope splice that writes a dip
/// into a bed's <see cref="MediaCueNode.VolumeEnvelope"/> for the span of overlapping voice-over
/// lanes. Editor sugar only - the output is ordinary envelope keyframes; the runtime knows nothing
/// about ducking.
/// <para>Time bases: lane overlap is computed on the GROUP TIMELINE (audible starts -
/// <see cref="TimelineMath.BlockStartMs"/> = TimelineStartMs + PreWaitMs). Envelope keyframes are
/// CLIP-relative (post-StartOffset), and a block renders exactly its trimmed clip, so clip time 0 IS
/// the block's audible start: timeline → clip is a single <c>− BlockStartMs(bed)</c> shift, and the
/// StartOffset trim is accounted for by clamping to <c>[0, EffectiveDurationMs]</c>, never by adding
/// the offset itself.</para>
/// </summary>
internal static class TimelineDuckMath
{
    /// <summary>A lane's audible block span on the group timeline - the same rectangle the canvas
    /// draws (pre-wait shifted start; unprobed media uses the 5 s fallback width).</summary>
    public static TimelineIntervalMs BlockIntervalMs(CueNodeViewModel node)
    {
        var start = TimelineMath.BlockStartMs(node);
        return new TimelineIntervalMs(start, start + TimelineMath.BlockDurationMs(node));
    }

    /// <summary>True when the half-open intervals share a non-empty span (touching edges do NOT
    /// overlap - a voice-over starting exactly at the bed's end needs no duck).</summary>
    public static bool Overlaps(TimelineIntervalMs a, TimelineIntervalMs b) =>
        a.StartMs < b.EndMs && b.StartMs < a.EndMs;

    /// <summary>Sort and merge overlapping/adjacent intervals; empty and negative-length inputs are
    /// dropped. Adjacent (touching) intervals merge so back-to-back voice-overs make ONE dip.</summary>
    public static List<TimelineIntervalMs> MergeIntervals(IEnumerable<TimelineIntervalMs> intervals)
    {
        var merged = new List<TimelineIntervalMs>();
        foreach (var interval in intervals.Where(i => i.LengthMs > 0)
                     .OrderBy(i => i.StartMs).ThenBy(i => i.EndMs))
        {
            if (merged.Count > 0 && interval.StartMs <= merged[^1].EndMs)
                merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, interval.EndMs) };
            else
                merged.Add(interval);
        }
        return merged;
    }

    /// <summary>
    /// Write duck dips into a bed's envelope for every voice-over overlap and return the new list
    /// (the input list is never mutated; the ORIGINAL reference comes back when nothing overlaps).
    /// Per merged overlap the dip spans <c>[start − lead − ramp, end + lead + ramp]</c> in bed clip
    /// time with keyframes [restore, depth, depth, restore]: restore levels are the bed's own
    /// envelope SAMPLED at the dip edges (a bed riding at −6 dB stays −6 outside the dip) and the
    /// depth level is that sample + <paramref name="depthDb"/> (clamped to the −60..+12 authoring
    /// range). <paramref name="curve"/> shapes the two ramps. Existing keyframes inside a dip span
    /// are REPLACED, ones outside are preserved, and re-applying with the same inputs is a no-op
    /// change-wise (idempotent). Dips are clamped to the trimmed clip <c>[0, bedEffectiveMs]</c>;
    /// a ramp squeezed fully off the clip edge collapses to holding the depth from/to that edge.
    /// Overlaps closer than 2·(lead+ramp) merge into one dip (no recover-bump between them).
    /// </summary>
    public static IReadOnlyList<CueAutomationPoint> ApplyDucks(
        IReadOnlyList<CueAutomationPoint> envelope,
        int bedBlockStartMs,
        int bedEffectiveMs,
        IEnumerable<TimelineIntervalMs> voiceTimelineIntervals,
        double depthDb,
        int rampMs,
        int leadMs,
        CueFadeCurve curve)
    {
        rampMs = Math.Max(0, rampMs);
        leadMs = Math.Max(0, leadMs);
        if (bedEffectiveMs <= 0)
            return envelope;

        // Timeline → bed clip time (see the class doc), padded by lead+ramp into dip footprints.
        // Merging happens on the PADDED spans, so ducks that would collide fuse into one hold.
        var pad = leadMs + rampMs;
        var footprints = new List<TimelineIntervalMs>();
        foreach (var voice in voiceTimelineIntervals)
        {
            var start = voice.StartMs - bedBlockStartMs;
            var end = voice.EndMs - bedBlockStartMs;
            if (end <= start || end <= 0 || start >= bedEffectiveMs)
                continue; // zero-length, or no overlap with the bed's trimmed clip
            footprints.Add(new TimelineIntervalMs(start - pad, end + pad));
        }

        if (footprints.Count == 0)
            return envelope;

        // Left-to-right so each dip's restore levels sample the envelope as already spliced - merged
        // footprints are disjoint with a gap, so later dips never disturb earlier ones.
        var result = envelope;
        foreach (var dip in MergeIntervals(footprints))
            result = SpliceDip(result, dip, bedEffectiveMs, depthDb, rampMs, curve);
        return result;
    }

    /// <summary>Splice ONE dip into the envelope: sample the restore levels at the (unclamped) dip
    /// edges, drop every existing point inside the span, and insert the up-to-4 synthesized points
    /// clamped to the trimmed clip.</summary>
    private static IReadOnlyList<CueAutomationPoint> SpliceDip(
        IReadOnlyList<CueAutomationPoint> envelope,
        TimelineIntervalMs dip,
        int bedEffectiveMs,
        double depthDb,
        int rampMs,
        CueFadeCurve curve)
    {
        // t0..t3 = restore-in, depth-in, depth-out, restore-out. Footprint length ≥ 2·ramp by
        // construction, so t1 ≤ t2 always.
        var t0 = dip.StartMs;
        var t1 = t0 + rampMs;
        var t2 = dip.EndMs - rampMs;
        var t3 = dip.EndMs;

        // Sampling at the unclamped edges keeps re-apply idempotent: the second pass lands exactly
        // on (or flat-extrapolates to) the restore points the first pass wrote.
        var restoreIn = RoundLevel(TimelineMath.EnvelopeLevelDbAt(envelope, t0));
        var restoreOut = RoundLevel(TimelineMath.EnvelopeLevelDbAt(envelope, t3));
        var depthIn = ClampLevel(RoundLevel(restoreIn + depthDb));
        var depthOut = ClampLevel(RoundLevel(restoreOut + depthDb));

        var c0 = Math.Clamp(t0, 0, bedEffectiveMs);
        var c1 = Math.Clamp(t1, 0, bedEffectiveMs);
        var c2 = Math.Clamp(t2, 0, bedEffectiveMs);
        var c3 = Math.Clamp(t3, 0, bedEffectiveMs);

        var synthesized = new List<CueAutomationPoint>(4);
        // A ramp clamped to zero width (dip starts before / ends past the clip) drops its restore
        // point - flat-before-first/after-last then holds the depth to the clip edge, which is what
        // "the voice-over is already talking when the bed starts" should sound like. With ramp = 0
        // the coincident restore+depth pair is intentional: an instant step.
        if (!(rampMs > 0 && c0 == c1))
            synthesized.Add(new CueAutomationPoint { TimeMs = c0, LevelDb = restoreIn, CurveToNext = curve });
        synthesized.Add(new CueAutomationPoint { TimeMs = c1, LevelDb = depthIn });
        synthesized.Add(new CueAutomationPoint { TimeMs = c2, LevelDb = depthOut, CurveToNext = curve });
        if (!(rampMs > 0 && c2 == c3))
            synthesized.Add(new CueAutomationPoint { TimeMs = c3, LevelDb = restoreOut });

        // Existing points strictly left of the (unclamped) span, the dip, then strictly right - the
        // concatenation is sorted by construction, so coincident-time order stays deterministic.
        var result = new List<CueAutomationPoint>(envelope.Count + synthesized.Count);
        result.AddRange(envelope.Where(p => p.TimeMs < t0));
        result.AddRange(synthesized);
        result.AddRange(envelope.Where(p => p.TimeMs > t3));
        return result;
    }

    private static double ClampLevel(double db) =>
        Math.Clamp(db, CueAutomationPoint.SilenceLevelDb, CueAutomationPoint.MaxLevelDb);

    /// <summary>Authored values stay tidy - same 0.1 dB step the canvas's point drags round to.</summary>
    private static double RoundLevel(double db) =>
        Math.Round(db / TimelineMath.EnvelopeLevelStepDb) * TimelineMath.EnvelopeLevelStepDb;
}
