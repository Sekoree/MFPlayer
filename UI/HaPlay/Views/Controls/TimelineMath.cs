using System;
using System.Collections.Generic;
using Avalonia;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>What a pointer position lands on inside one timeline lane item.</summary>
internal enum TimelineHitKind
{
    None,
    Block,
    Marker,
    LeftEdge,
    RightEdge,
    FadeInHandle,
    FadeOutHandle,
    EnvelopePoint,
}

/// <summary>
/// Pure geometry, hit-test and snap math for <see cref="TimelineCanvas"/>. Kept free of control
/// state so block/handle/marker picking and snapping are directly unit-testable.
/// </summary>
internal static class TimelineMath
{
    public const double RulerHeight = 26;
    public const double LaneHeight = 36;
    public const double LaneGap = 4;
    public const double BlockVPad = 5;
    public const double EdgeGripPx = 6;
    public const double FadeHandlePx = 9;
    public const double MarkerHalfPx = 8;
    public const double SnapThresholdPx = 8;

    /// <summary>Block width for a media cue whose source was never probed (no known duration).</summary>
    public const int FallbackBlockDurationMs = 5000;

    public static double LaneTop(int laneIndex) => RulerHeight + laneIndex * (LaneHeight + LaneGap);

    public static double LanesHeight(int laneCount) =>
        RulerHeight + Math.Max(0, laneCount) * (LaneHeight + LaneGap);

    public static double XForMs(double ms, double pxPerMs) => ms * pxPerMs;

    public static double MsForX(double x, double pxPerMs) => pxPerMs <= 0 ? 0 : x / pxPerMs;

    /// <summary>True when the cue renders as a zero-length diamond marker instead of a block.</summary>
    public static bool IsMarker(CueNodeViewModel node) =>
        node.Kind is not (CueNodeKind.Media or CueNodeKind.Group);

    /// <summary>Rendered block width source: the effective (trimmed) duration, group roll-up for
    /// nested groups, or the 5 s fallback when the media duration is unknown.</summary>
    public static int BlockDurationMs(CueNodeViewModel node) => node.Kind switch
    {
        CueNodeKind.Media => node.EffectiveDurationMs > 0 ? node.EffectiveDurationMs : FallbackBlockDurationMs,
        CueNodeKind.Group => node.RolledDurationMs > 0
            ? (int)Math.Min(int.MaxValue, node.RolledDurationMs)
            : FallbackBlockDurationMs,
        _ => 0,
    };

    /// <summary>Trim/fade handles only make sense on a media block with a known (probed) duration.</summary>
    public static bool IsTrimmable(CueNodeViewModel node) =>
        node.Kind == CueNodeKind.Media && node.DurationMs > 0;

    public static Rect BlockRect(int laneIndex, int startMs, int durationMs, double pxPerMs) => new(
        XForMs(Math.Max(0, startMs), pxPerMs),
        LaneTop(laneIndex) + BlockVPad,
        Math.Max(2, durationMs * pxPerMs),
        LaneHeight - BlockVPad * 2);

    /// <summary>Fade-in handle center: the point where the fade ramp meets full level on the block's
    /// top edge - dragging it changes the fade length.</summary>
    public static Point FadeInHandleCenter(Rect block, int fadeInMs, double pxPerMs) => new(
        Math.Min(block.Right, block.X + Math.Max(0, fadeInMs) * pxPerMs), block.Y);

    public static Point FadeOutHandleCenter(Rect block, int fadeOutMs, double pxPerMs) => new(
        Math.Max(block.X, block.Right - Math.Max(0, fadeOutMs) * pxPerMs), block.Y);

    public static Point MarkerCenter(int laneIndex, int startMs, double pxPerMs) => new(
        XForMs(Math.Max(0, startMs), pxPerMs), LaneTop(laneIndex) + LaneHeight / 2);

    public static bool MarkerContains(Point center, Point p) =>
        Math.Abs(p.X - center.X) + Math.Abs(p.Y - center.Y) <= MarkerHalfPx;

    /// <summary>Hit-test one block: fade handles beat edge grips beat the body, so a short fade's
    /// handle parked at a corner stays grabbable. Trim edges and fades require a trimmable block.</summary>
    public static TimelineHitKind HitTestBlock(
        Rect block, int fadeInMs, int fadeOutMs, double pxPerMs, Point p, bool trimmable)
    {
        var nearVertically = p.Y >= block.Y - FadeHandlePx && p.Y <= block.Bottom;
        if (trimmable && nearVertically)
        {
            if (Distance(FadeInHandleCenter(block, fadeInMs, pxPerMs), p) <= FadeHandlePx)
                return TimelineHitKind.FadeInHandle;
            if (Distance(FadeOutHandleCenter(block, fadeOutMs, pxPerMs), p) <= FadeHandlePx)
                return TimelineHitKind.FadeOutHandle;
        }

        var inVerticalRange = p.Y >= block.Y && p.Y <= block.Bottom;
        if (trimmable && inVerticalRange)
        {
            if (Math.Abs(p.X - block.X) <= EdgeGripPx)
                return TimelineHitKind.LeftEdge;
            if (Math.Abs(p.X - block.Right) <= EdgeGripPx)
                return TimelineHitKind.RightEdge;
        }

        return block.Contains(p) ? TimelineHitKind.Block : TimelineHitKind.None;
    }

    /// <summary>
    /// Snap a candidate time: nearest other-block edge within the pixel threshold wins, else the
    /// grid multiple when a grid is set; snapping disabled returns the raw (clamped ≥ 0) value.
    /// </summary>
    public static int Snap(
        double ms, bool snapEnabled, int gridMs, IEnumerable<int> edgeCandidatesMs, double pxPerMs,
        double thresholdPx = SnapThresholdPx)
    {
        if (double.IsNaN(ms) || ms < 0)
            ms = 0;
        if (!snapEnabled)
            return (int)Math.Round(ms);

        var thresholdMs = pxPerMs > 0 ? thresholdPx / pxPerMs : 0;
        var bestDistance = double.MaxValue;
        var snapped = ms;
        foreach (var edge in edgeCandidatesMs)
        {
            var distance = Math.Abs(edge - ms);
            if (distance <= thresholdMs && distance < bestDistance)
            {
                bestDistance = distance;
                snapped = edge;
            }
        }

        if (bestDistance == double.MaxValue && gridMs > 0)
            snapped = Math.Round(ms / gridMs) * gridMs;
        return (int)Math.Max(0, Math.Round(snapped));
    }

    /// <summary>Ruler tick spacing: the smallest "nice" step whose label pitch stays readable.</summary>
    public static int RulerStepMs(double pxPerMs, double minLabelPx = 70)
    {
        ReadOnlySpan<int> steps = [100, 200, 500, 1_000, 2_000, 5_000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000];
        foreach (var step in steps)
        {
            if (step * pxPerMs >= minLabelPx)
                return step;
        }
        return steps[^1];
    }

    public static string FormatRulerLabel(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        var head = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        return ms % 1000 == 0 ? head : $"{head}.{ts.Milliseconds:D3}".TrimEnd('0');
    }

    // ---- Volume envelope (Phase B) -------------------------------------------------------------
    //
    // Coordinate mapping: envelope times are CLIP-relative (post-StartOffset) and the block spans
    // exactly the trimmed clip, so time 0 = the block's LEFT edge and x = block.X + timeMs·pxPerMs.
    // Levels map linearly IN dB onto the block's inner height: +12 dB (CueAutomationPoint.MaxLevelDb)
    // at the block's top edge, −60 dB (SilenceLevelDb) at its bottom. That puts the 0 dB unity line
    // at a FIXED reference 12/72 = ⅙ of the block height below the top (not mid-lane): the top sixth
    // is boost headroom, everything below the line is attenuation - the renderer draws the line so
    // points are easy to park at unity.

    public const double EnvelopePointHitPx = 6;
    public const double EnvelopeLineHitPx = 5;
    public const double EnvelopePointRadiusPx = 3;

    /// <summary>Rounding step for dragged point levels - keeps authored values tidy (0.1 dB).</summary>
    public const double EnvelopeLevelStepDb = 0.1;

    /// <summary>Y for a level: linear in dB, +12 dB = block top, −60 dB = block bottom (see the
    /// mapping note above). Levels outside the authoring range clamp onto the edges.</summary>
    public static double EnvelopeYForDb(Rect block, double levelDb)
    {
        var clamped = Math.Clamp(levelDb, CueAutomationPoint.SilenceLevelDb, CueAutomationPoint.MaxLevelDb);
        return block.Y + (CueAutomationPoint.MaxLevelDb - clamped)
            / (CueAutomationPoint.MaxLevelDb - CueAutomationPoint.SilenceLevelDb) * block.Height;
    }

    /// <summary>Inverse of <see cref="EnvelopeYForDb"/>, clamped to the −60..+12 authoring range.</summary>
    public static double EnvelopeDbForY(Rect block, double y)
    {
        var t = block.Height <= 0 ? 0 : (y - block.Y) / block.Height;
        return Math.Clamp(
            CueAutomationPoint.MaxLevelDb - t * (CueAutomationPoint.MaxLevelDb - CueAutomationPoint.SilenceLevelDb),
            CueAutomationPoint.SilenceLevelDb, CueAutomationPoint.MaxLevelDb);
    }

    /// <summary>Clip-relative time for an x position, clamped to the trimmed clip [0, maxTimeMs].</summary>
    public static int EnvelopeTimeForX(Rect block, double x, double pxPerMs, int maxTimeMs) =>
        pxPerMs <= 0 ? 0 : (int)Math.Clamp(Math.Round((x - block.X) / pxPerMs), 0, Math.Max(0, maxTimeMs));

    /// <summary>Canvas position of one envelope keyframe. X clamps to the block's right edge so a
    /// point authored past the trimmed range (e.g. after a re-trim) stays visible and grabbable -
    /// dragging it pulls its time back into range.</summary>
    public static Point EnvelopePointCenter(Rect block, CueAutomationPoint point, double pxPerMs) =>
        new(Math.Min(block.X + Math.Max(0, point.TimeMs) * pxPerMs, block.Right),
            EnvelopeYForDb(block, point.LevelDb));

    /// <summary>
    /// The envelope level (dB) at a clip time - the editor-side mirror of the runtime's
    /// <c>VolumeEnvelopes.Sample</c>: flat before the first / after the last point, and in between the
    /// segment's curve-shaped interpolation IN LINEAR GAIN (converted back to dB), so the drawn line
    /// is exactly what playback applies. Empty envelope = unity (0 dB).
    /// </summary>
    public static double EnvelopeLevelDbAt(IReadOnlyList<CueAutomationPoint> envelope, double timeMs)
    {
        if (envelope.Count == 0)
            return 0;
        if (timeMs <= envelope[0].TimeMs)
            return ClampDb(envelope[0].LevelDb);
        if (timeMs >= envelope[^1].TimeMs)
            return ClampDb(envelope[^1].LevelDb);

        var i = 0;
        while (i + 1 < envelope.Count && envelope[i + 1].TimeMs <= timeMs)
            i++;
        var from = envelope[i];
        var to = envelope[i + 1];
        var span = to.TimeMs - from.TimeMs;
        var t = span <= 0 ? 1 : (timeMs - from.TimeMs) / span;

        // FadeCurves.LevelBetween: rising segments shape progress, falling ones shape the mirror, so
        // every curve eases the same way in both directions.
        var start = DbToGain(from.LevelDb);
        var target = DbToGain(to.LevelDb);
        var gain = target < start
            ? target + (start - target) * ShapeCurve(1 - t, from.CurveToNext)
            : start + (target - start) * ShapeCurve(t, from.CurveToNext);
        return GainToDb(gain);
    }

    /// <summary>Insert position that keeps the envelope time-sorted (after any point at the same time).</summary>
    public static int EnvelopeInsertIndex(IReadOnlyList<CueAutomationPoint> envelope, int timeMs)
    {
        var i = 0;
        while (i < envelope.Count && envelope[i].TimeMs <= timeMs)
            i++;
        return i;
    }

    /// <summary>Nearest keyframe within <see cref="EnvelopePointHitPx"/> of <paramref name="p"/>, else −1.</summary>
    public static int EnvelopePointHit(Rect block, IReadOnlyList<CueAutomationPoint> envelope, double pxPerMs, Point p)
    {
        var best = -1;
        var bestDistance = EnvelopePointHitPx;
        for (var i = 0; i < envelope.Count; i++)
        {
            var distance = Distance(EnvelopePointCenter(block, envelope[i], pxPerMs), p);
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>True when <paramref name="p"/> sits on the envelope line: within the block's x range
    /// and vertically within <see cref="EnvelopeLineHitPx"/> of the curve at that x (near-vertical
    /// steps are only grabbable right at the step - click beside them instead).</summary>
    public static bool EnvelopeLineHit(Rect block, IReadOnlyList<CueAutomationPoint> envelope, double pxPerMs, Point p)
    {
        if (pxPerMs <= 0 || p.X < block.X || p.X > block.Right)
            return false;
        var y = EnvelopeYForDb(block, EnvelopeLevelDbAt(envelope, (p.X - block.X) / pxPerMs));
        return Math.Abs(p.Y - y) <= EnvelopeLineHitPx;
    }

    /// <summary>Index of the segment (the point owning <c>CurveToNext</c>) containing
    /// <paramref name="timeMs"/>: i where points[i].TimeMs ≤ t &lt; points[i+1].TimeMs, −1 in the
    /// flat lead-in/tail regions (no segment to curve there).</summary>
    public static int EnvelopeSegmentAt(IReadOnlyList<CueAutomationPoint> envelope, double timeMs)
    {
        for (var i = 0; i + 1 < envelope.Count; i++)
        {
            if (envelope[i].TimeMs <= timeMs && timeMs < envelope[i + 1].TimeMs)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Clamp a dragged point's candidate time so the list STAYS time-sorted: a point cannot pass its
    /// neighbors (it stops at them - chosen over reordering so a drag never changes which point owns
    /// which segment curve; coincident times are allowed and render as an instant step) nor leave the
    /// trimmed clip [0, maxTimeMs].
    /// </summary>
    public static int EnvelopeClampDragTime(
        IReadOnlyList<CueAutomationPoint> envelope, int index, double candidateMs, int maxTimeMs)
    {
        var lower = index > 0 ? envelope[index - 1].TimeMs : 0;
        var upper = index < envelope.Count - 1 ? envelope[index + 1].TimeMs : Math.Max(0, maxTimeMs);
        upper = Math.Min(upper, Math.Max(0, maxTimeMs));
        if (double.IsNaN(candidateMs))
            candidateMs = lower;
        return (int)Math.Clamp(Math.Round(candidateMs), lower, Math.Max(lower, upper));
    }

    /// <summary>Right-click cycle order for a segment's curve: Linear → EqualPower → Exponential →
    /// SCurve → Linear.</summary>
    public static CueFadeCurve NextCurve(CueFadeCurve curve) => curve switch
    {
        CueFadeCurve.Linear => CueFadeCurve.EqualPower,
        CueFadeCurve.EqualPower => CueFadeCurve.Exponential,
        CueFadeCurve.Exponential => CueFadeCurve.SCurve,
        _ => CueFadeCurve.Linear,
    };

    /// <summary>Signed dB readout ("+3.0 dB" / "-6.5 dB" / "0.0 dB") for the drag tooltip.</summary>
    public static string FormatDbLabel(double db) =>
        db.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";

    private static double ClampDb(double db) => double.IsNaN(db)
        ? 0
        : Math.Clamp(db, CueAutomationPoint.SilenceLevelDb, CueAutomationPoint.MaxLevelDb);

    /// <summary>−60 dB floor and below map to zero gain (the runtime mapper's convention).</summary>
    private static double DbToGain(double db) =>
        db <= CueAutomationPoint.SilenceLevelDb ? 0 : Math.Pow(10, db / 20);

    private static double GainToDb(double gain) => gain <= 0
        ? CueAutomationPoint.SilenceLevelDb
        : Math.Clamp(20 * Math.Log10(gain), CueAutomationPoint.SilenceLevelDb, CueAutomationPoint.MaxLevelDb);

    /// <summary>UI mirror of the session's <c>FadeCurves.Shape</c> (0→0, 1→1, monotonic).</summary>
    private static double ShapeCurve(double p, CueFadeCurve curve) => curve switch
    {
        CueFadeCurve.EqualPower => Math.Sin(p * Math.PI / 2d),
        CueFadeCurve.Exponential => p * p * p,
        CueFadeCurve.SCurve => p * p * (3d - 2d * p),
        _ => p,
    };

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
