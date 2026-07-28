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

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
