using System;

namespace HaPlay.Models;

/// <summary>
/// The legal range of a normalized placement rectangle - a cue's video placement inside a composition, or a
/// composition's section inside an output. Lives in the model layer because it is not an interaction rule: a
/// rectangle typed into a spin box, or loaded from a project file, has to land in exactly the same range as
/// one produced by dragging. (The editors' SNAPPING is the interaction half and lives with the canvases, in
/// <c>HaPlay.Views.Controls.PlacementSnapMath</c>.)
/// <para>Rectangles may sit partly or wholly OUTSIDE their canvas on purpose. Both editors used to clamp
/// position to <c>[0, 1-size]</c>, which forbade a lower third sliding in from off-canvas, a source
/// deliberately oversized to fill-and-crop, and a composition letterboxed off-centre in a mismatched output.
/// The range is still finite in both directions, so anything pushed out of view can always be dragged
/// back.</para>
/// </summary>
public static class NormalizedRectRange
{
    /// <summary>Smallest normalized edge length. A rect that can reach zero cannot be grabbed again.</summary>
    public const double MinSize = 0.02;

    /// <summary>Largest normalized edge length - twice the canvas. Oversizing is legitimate (fill-and-crop),
    /// but an unbounded handle drag would send an edge to a size no one can pull back.</summary>
    public const double MaxSize = 2.0;

    /// <summary>Clamps a size to <see cref="MinSize"/>..<see cref="MaxSize"/>, honouring a caller's own
    /// larger minimum (the output-layout editor uses one source pixel).</summary>
    public static double ClampSize(double size, double minimum = MinSize) =>
        Math.Clamp(size, Math.Max(MinSize, minimum), MaxSize);

    /// <summary>Clamps one axis of a position to the reachable range: from "exactly fully before the canvas"
    /// (<c>-size</c>) to "exactly fully after it" (<c>1</c>).</summary>
    public static double ClampPosition(double position, double size) =>
        Math.Clamp(position, -size, 1.0);
}
