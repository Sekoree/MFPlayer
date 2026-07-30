using System;
using System.Collections.Generic;
using Avalonia;
using HaPlay.Models;

namespace HaPlay.Views.Controls;

/// <summary>
/// Pure drag geometry shared by the two normalized-rectangle editors - <see cref="CompositionPlacementCanvas"/>
/// (a cue's video placements inside a composition) and <see cref="OutputLayoutCanvas"/> (a composition's
/// sections inside an output). Kept separate from both, and free of Avalonia input, so the rules are unit
/// tested rather than eyeballed by dragging: see <c>PlacementSnapMathTests</c>.
/// <para>Two behaviours live here. <b>Snapping</b> pulls a dragged edge or centre onto a canvas guide (the
/// left/top edge, the centre, the right/bottom edge) when it comes within <see cref="SnapPixels"/> ON SCREEN -
/// so the pull feels the same on a small editor canvas and a large one, which a normalized threshold would
/// not. <b>Out-of-bounds</b> movement is allowed on purpose: an operator placing a video partly off-canvas
/// (a lower-third sliding in, a deliberately cropped fill) or letterboxing a composition inside a mismatched
/// output needs to leave the bounds, and the old hard clamp to <c>[0, 1-size]</c> made both impossible. The
/// range is still finite - a rect may travel until it is exactly fully outside, never further, so a
/// placement can always be dragged back.</para>
/// </summary>
internal static class PlacementSnapMath
{
    /// <summary>Screen-space pull distance. Deliberately generous: these canvases are small, and the guides
    /// (edges + centre) are far enough apart that a wide threshold cannot make two of them ambiguous.</summary>
    public const double SnapPixels = 7.0;

    /// <summary>Re-exported from <see cref="NormalizedRectRange"/> so the canvases read one vocabulary; the
    /// range itself is a MODEL rule (it must hold for typed and loaded rectangles too), snapping is not.</summary>
    public const double MinSize = NormalizedRectRange.MinSize;

    public const double MaxSize = NormalizedRectRange.MaxSize;

    /// <summary>The guides a dragged rect snaps to, in normalized canvas space: both edges and the centre.
    /// Order is irrelevant - the nearest within the threshold wins.</summary>
    private static readonly double[] Guides = [0.0, 0.5, 1.0];

    /// <summary>Normalized snap threshold for an axis whose canvas measures <paramref name="canvasPixels"/>.
    /// Zero (or a degenerate canvas) disables snapping on that axis rather than dividing by zero.</summary>
    public static double Threshold(double canvasPixels) =>
        canvasPixels > 0 ? SnapPixels / canvasPixels : 0.0;

    /// <summary>Snaps one axis of a MOVE. Considers the leading edge, the centre and the trailing edge, and
    /// applies whichever alignment is nearest within <paramref name="threshold"/> - so a rect can be parked
    /// flush left, centred, or flush right without pixel-hunting. Returns <paramref name="position"/>
    /// unchanged when nothing is close enough (or snapping is off), which is what lets a deliberate
    /// out-of-bounds drag pass straight through.</summary>
    public static double SnapMoveAxis(double position, double size, double threshold, bool enabled)
    {
        if (!enabled || threshold <= 0)
            return position;

        var best = position;
        var bestDelta = double.MaxValue;
        foreach (var guide in Guides)
        {
            // The three candidate positions that would put an anchor of this rect exactly on the guide.
            foreach (var candidate in stackalloc[] { guide, guide - size / 2, guide - size })
            {
                var delta = Math.Abs(candidate - position);
                if (delta <= threshold && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <summary>Snaps one axis of a RESIZE, where the leading edge is pinned and only the trailing edge
    /// moves: the size snaps so that <c>origin + size</c> lands on a guide. Also keeps the result inside
    /// <see cref="MinSize"/>..<see cref="MaxSize"/>, since a resize is the one gesture that can collapse a
    /// rect to nothing.</summary>
    public static double SnapResizeAxis(double origin, double size, double threshold, bool enabled)
    {
        if (enabled && threshold > 0)
        {
            var bestDelta = double.MaxValue;
            var best = size;
            foreach (var guide in Guides)
            {
                var candidate = guide - origin;
                var delta = Math.Abs(candidate - size);
                if (candidate >= MinSize && delta <= threshold && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = candidate;
                }
            }

            size = best;
        }

        return NormalizedRectRange.ClampSize(size);
    }

    /// <summary>Clamps a moved axis to the out-of-bounds range: from "exactly fully before the canvas"
    /// (<c>-size</c>) to "exactly fully after it" (<c>1</c>). Wide enough to park a rect completely off
    /// canvas, tight enough that it is always still reachable by dragging back.</summary>
    public static double ClampMoveAxis(double position, double size) =>
        NormalizedRectRange.ClampPosition(position, size);

    /// <summary>Applies both steps to a MOVE, per axis. <paramref name="canvas"/> is the on-screen canvas
    /// rectangle, used only to convert the pixel threshold.</summary>
    public static Point SnapAndClampMove(
        double x, double y, double width, double height, Rect canvas, bool snap)
    {
        var sx = SnapMoveAxis(x, width, Threshold(canvas.Width), snap);
        var sy = SnapMoveAxis(y, height, Threshold(canvas.Height), snap);
        return new Point(ClampMoveAxis(sx, width), ClampMoveAxis(sy, height));
    }

    /// <summary>The canvas guides that a rect is currently sitting on, as normalized x/y values - what the
    /// editors draw so the operator can see WHY a drag stopped moving. Empty when nothing is aligned.</summary>
    public static (IReadOnlyList<double> X, IReadOnlyList<double> Y) ActiveGuides(
        double x, double y, double width, double height, Rect canvas)
    {
        var tx = Threshold(canvas.Width) / 4; // drawn only when genuinely ON the guide, not merely near it
        var ty = Threshold(canvas.Height) / 4;
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var guide in Guides)
        {
            if (Math.Abs(x - guide) <= tx || Math.Abs(x + width / 2 - guide) <= tx
                || Math.Abs(x + width - guide) <= tx)
                xs.Add(guide);
            if (Math.Abs(y - guide) <= ty || Math.Abs(y + height / 2 - guide) <= ty
                || Math.Abs(y + height - guide) <= ty)
                ys.Add(guide);
        }

        return (xs, ys);
    }
}
