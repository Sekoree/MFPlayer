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

/// <summary>Where a projected envelope keyframe sits relative to the block that owns it. Out-of-range
/// points are not a rendering accident to be hidden: the runtime sampler interpolates toward them and
/// flat-extrapolates the outermost one, so they still shape the AUDIBLE curve.</summary>
internal enum TimelineEnvelopeRange
{
    InRange,

    /// <summary>Negative clip time - left of the block's left edge.</summary>
    BeforeStart,

    /// <summary>Clip time past the trimmed length - right of the block's right edge.</summary>
    BeyondEnd,
}

/// <summary>
/// One envelope keyframe projected onto the canvas: its index in the envelope, its centre in canvas
/// coordinates, and where that centre fell relative to the block. The centre is NEVER clamped - this
/// is the single source of truth the renderer AND the hit-test consume, so a presentation decision
/// (what to do about an out-of-range point) can no longer desync drawing from picking.
/// </summary>
internal readonly record struct EnvelopePointProjection(int Index, Point Center, TimelineEnvelopeRange Range)
{
    public bool IsInRange => Range == TimelineEnvelopeRange.InRange;
}

/// <summary>
/// The single stand-in the canvas draws - and picks - for ALL the out-of-range keyframes at one block
/// edge: a chevron badge carrying their <see cref="Count"/>. <see cref="PointIndex"/> is the
/// INNERMOST of them (the one adjacent to the in-range run), so dragging it pulls it back into range
/// without fighting <see cref="TimelineMath.EnvelopeClampDragTime"/>'s neighbour clamp, and repeated
/// select+Delete peels the run off from the inside out.
/// </summary>
internal readonly record struct EnvelopeEdgeIndicator(
    TimelineEnvelopeRange Edge, int Count, int PointIndex, Rect Bounds);

/// <summary>
/// One block's envelope in view terms: every keyframe projected (unclamped) plus the edge indicators
/// standing in for the out-of-range ones. Produced by <see cref="TimelineMath.ProjectEnvelope"/> and
/// consumed by both <c>TimelineCanvas.RenderEnvelope</c> and
/// <see cref="TimelineMath.HitTestEnvelope(in EnvelopeOverlay, Point)"/>.
/// </summary>
internal readonly record struct EnvelopeOverlay(
    IReadOnlyList<EnvelopePointProjection> Points,
    EnvelopeEdgeIndicator? BeforeStart,
    EnvelopeEdgeIndicator? BeyondEnd)
{
    public static readonly EnvelopeOverlay Empty = new([], null, null);

    /// <summary>Where keyframe <paramref name="index"/> landed (in-range for anything out of list
    /// bounds - callers use it to decide whether a SELECTED point draws as a dot or as a lit badge).</summary>
    public TimelineEnvelopeRange RangeOf(int index) =>
        index >= 0 && index < Points.Count ? Points[index].Range : TimelineEnvelopeRange.InRange;
}

/// <summary>What a pointer landed on in an envelope overlay: a keyframe index (−1 = nothing) and
/// whether it was reached through an edge indicator rather than its own dot.</summary>
internal readonly record struct EnvelopeHit(int PointIndex, bool ViaEdgeIndicator)
{
    public static readonly EnvelopeHit None = new(-1, false);

    public bool IsHit => PointIndex >= 0;
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

    /// <summary>The lane position (ms on the group's plan epoch) a node's block/marker renders at: the
    /// authored <c>TimelineStartMs</c> PLUS its <c>PreWaitMs</c> - the AUDIBLE start the trigger plan
    /// fires at. Rendering at the raw authored start understated a pre-waited lane's real start; drags
    /// write back <c>blockStart − PreWaitMs</c> (floored at 0, so a block can never sit left of its own
    /// pre-wait).</summary>
    public static int BlockStartMs(CueNodeViewModel node) =>
        Math.Max(0, node.TimelineStartMs) + Math.Max(0, node.PreWaitMs);

    /// <summary>Normalized window [0,1] of a media source's full duration that the (trimmed) block
    /// shows - the slice of the whole-file waveform peaks to draw inside the block.</summary>
    public static (double StartFrac, double EndFrac) WaveformWindow(
        int startOffsetMs, int effectiveMs, int endOffsetMs)
    {
        var total = (double)Math.Max(0, startOffsetMs) + Math.Max(0, effectiveMs) + Math.Max(0, endOffsetMs);
        if (total <= 0 || effectiveMs <= 0)
            return (0, 1);
        var start = Math.Max(0, startOffsetMs) / total;
        return (start, Math.Clamp(start + effectiveMs / total, start, 1));
    }

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
    //
    // OUT-OF-RANGE keyframes: every EDITOR path clamps authored times into [0, EffectiveDurationMs]
    // (EnvelopeTimeForX, EnvelopeClampDragTime, TimelineDuckMath.ApplyDucks), but a RIGHT-edge trim
    // shrinks that range without rewriting the stored times - so BeyondEnd is the case the editor
    // itself produces. A LEFT-edge trim only raises StartOffsetMs and rebases nothing, so BeforeStart
    // cannot arise from trimming; it is still projected (and indicated) symmetrically because
    // CueAutomationPoint.TimeMs is a plain int that an externally authored/edited project file can
    // carry negative, and the runtime honours such a point (VolumeEnvelopes.Sample interpolates from
    // the first point forwards and flat-extrapolates it backwards).

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

    /// <summary>Chevron-badge size (px) for the out-of-range keyframe indicator: the DRAWN box and its
    /// HIT box are one and the same rectangle (<see cref="EnvelopeEdgeIndicator.Bounds"/>), so the two
    /// cannot drift apart.</summary>
    public const double EnvelopeEdgeIndicatorWidthPx = 20;

    /// <inheritdoc cref="EnvelopeEdgeIndicatorWidthPx"/>
    public const double EnvelopeEdgeIndicatorHeightPx = 14;

    /// <summary>How far the pointer must travel before a drag STARTED ON AN EDGE BADGE begins editing.
    /// A badge sits at the block's edge, nowhere near the keyframe it stands for, so the ordinary
    /// absolute-position drag would teleport that keyframe to the badge's own x on the first pixel of
    /// hand jitter - turning a click (the way the badge is selected for Delete) into a silent,
    /// destructive edit. Dot drags need no threshold: the dot IS under the pointer, so one pixel of
    /// jitter is one pixel of edit.</summary>
    public const double EnvelopeBadgeDragThresholdPx = 5;

    /// <summary>
    /// Project one keyframe onto the canvas: x = block.X + timeMs·pxPerMs, y from the dB mapping.
    /// UNCLAMPED on x by design - a point authored past the trimmed end (or, from an externally
    /// edited file, before the trimmed start) projects OUTSIDE the block and says so through
    /// <see cref="EnvelopePointProjection.Range"/>. Clamping here is what let several such points
    /// stack invisibly on one pixel column where only the last was pickable; the presentation
    /// decision now belongs to the view adapter (<c>TimelineCanvas.RenderEnvelope</c>), which is why
    /// hit-testing consumes this same projection.
    /// </summary>
    public static EnvelopePointProjection ProjectEnvelopePoint(
        Rect block, CueAutomationPoint point, int index, double pxPerMs)
    {
        var x = block.X + point.TimeMs * pxPerMs;
        var range = x < block.X
            ? TimelineEnvelopeRange.BeforeStart
            : x > block.Right
                ? TimelineEnvelopeRange.BeyondEnd
                : TimelineEnvelopeRange.InRange;
        return new EnvelopePointProjection(index, new Point(x, EnvelopeYForDb(block, point.LevelDb)), range);
    }

    /// <summary>
    /// Project a whole envelope and fold the out-of-range points into (at most) one edge indicator per
    /// side. The indicator is inset by <see cref="EdgeGripPx"/> so it does not swallow the block's trim
    /// grip, and is suppressed on a block too narrow to hold two of them - a case where the renderer
    /// draws nothing and the hit-test therefore finds nothing, which keeps the two in agreement.
    /// </summary>
    public static EnvelopeOverlay ProjectEnvelope(
        Rect block, IReadOnlyList<CueAutomationPoint> envelope, double pxPerMs)
    {
        if (envelope.Count == 0)
            return EnvelopeOverlay.Empty;

        var points = new List<EnvelopePointProjection>(envelope.Count);
        int beforeCount = 0, beyondCount = 0, beforeIndex = -1, beyondIndex = -1;
        for (var i = 0; i < envelope.Count; i++)
        {
            var projection = ProjectEnvelopePoint(block, envelope[i], i, pxPerMs);
            points.Add(projection);
            switch (projection.Range)
            {
                case TimelineEnvelopeRange.BeforeStart:
                    beforeCount++;
                    beforeIndex = i; // innermost = the LAST one before the in-range run
                    break;
                case TimelineEnvelopeRange.BeyondEnd:
                    beyondCount++;
                    if (beyondIndex < 0)
                        beyondIndex = i; // innermost = the FIRST one after the in-range run
                    break;
            }
        }

        return new EnvelopeOverlay(
            points,
            EdgeIndicator(block, TimelineEnvelopeRange.BeforeStart, beforeCount, beforeIndex),
            EdgeIndicator(block, TimelineEnvelopeRange.BeyondEnd, beyondCount, beyondIndex));
    }

    private static EnvelopeEdgeIndicator? EdgeIndicator(
        Rect block, TimelineEnvelopeRange edge, int count, int pointIndex)
    {
        // Two badges must fit side by side without overlapping, whichever edges are actually in play.
        if (count <= 0 || block.Width < EnvelopeEdgeIndicatorWidthPx * 2
            || block.Height < EnvelopeEdgeIndicatorHeightPx)
            return null;

        var halfW = EnvelopeEdgeIndicatorWidthPx / 2;
        var halfH = EnvelopeEdgeIndicatorHeightPx / 2;
        var cx = edge == TimelineEnvelopeRange.BeforeStart
            ? block.X + EdgeGripPx + halfW
            : block.Right - EdgeGripPx - halfW;
        cx = Math.Clamp(cx, block.X + halfW, block.Right - halfW);
        return new EnvelopeEdgeIndicator(
            edge, count, pointIndex,
            new Rect(cx - halfW, block.Center.Y - halfH,
                EnvelopeEdgeIndicatorWidthPx, EnvelopeEdgeIndicatorHeightPx));
    }

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

    /// <summary>
    /// Pick a keyframe out of a projected overlay: the nearest IN-RANGE dot within
    /// <see cref="EnvelopePointHitPx"/>, else the edge indicator under <paramref name="p"/> (which
    /// resolves to the innermost out-of-range point it stands for). Only ever returns points the
    /// renderer actually drew - dots are the unambiguous in-range ones, and everything else is
    /// reachable exactly through the badge that represents it.
    /// </summary>
    public static EnvelopeHit HitTestEnvelope(in EnvelopeOverlay overlay, Point p)
    {
        var best = -1;
        var bestDistance = EnvelopePointHitPx;
        foreach (var projection in overlay.Points)
        {
            if (!projection.IsInRange)
                continue;
            var distance = Distance(projection.Center, p);
            if (distance <= bestDistance)
            {
                best = projection.Index;
                bestDistance = distance;
            }
        }
        if (best >= 0)
            return new EnvelopeHit(best, false);

        // Dots beat badges (they are the precise targets); between two badges the nearer centre wins.
        var indicator = NearerIndicatorAt(overlay.BeforeStart, overlay.BeyondEnd, p);
        return indicator is { } hit ? new EnvelopeHit(hit.PointIndex, true) : EnvelopeHit.None;
    }

    /// <summary>Convenience overload: project <paramref name="envelope"/> and pick in one step.</summary>
    public static EnvelopeHit HitTestEnvelope(
        Rect block, IReadOnlyList<CueAutomationPoint> envelope, double pxPerMs, Point p) =>
        HitTestEnvelope(ProjectEnvelope(block, envelope, pxPerMs), p);

    private static EnvelopeEdgeIndicator? NearerIndicatorAt(
        EnvelopeEdgeIndicator? a, EnvelopeEdgeIndicator? b, Point p)
    {
        var hitA = a is { } ia && ia.Bounds.Contains(p) ? a : null;
        var hitB = b is { } ib && ib.Bounds.Contains(p) ? b : null;
        if (hitA is null || hitB is null)
            return hitA ?? hitB;
        return Distance(hitA.Value.Bounds.Center, p) <= Distance(hitB.Value.Bounds.Center, p) ? hitA : hitB;
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
