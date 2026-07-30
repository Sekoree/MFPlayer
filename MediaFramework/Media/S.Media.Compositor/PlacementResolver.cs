using S.Media.Core.Video;

namespace S.Media.Compositor;

/// <summary>How a (cropped) source is fitted into its destination rectangle.</summary>
public enum PlacementFit
{
    /// <summary>Uniform scale to cover the dest rect; overflow is trimmed (centered).</summary>
    Cover,

    /// <summary>Uniform scale to fit inside the dest rect; letterboxed/pillarboxed (centered).</summary>
    Contain,

    /// <summary>Non-uniform scale to exactly fill the dest rect (may change aspect).</summary>
    Stretch,

    /// <summary>Uniform scale so the width fills the dest rect; height overflow trimmed / underflow centered.</summary>
    FillWidth,

    /// <summary>Uniform scale so the height fills the dest rect; width overflow trimmed / underflow centered.</summary>
    FillHeight,
}

/// <summary>
/// Resolves a layer placement - a destination rectangle on the canvas, a fit mode, and per-edge source
/// crop insets - into the <see cref="LayerTransform2D"/> + <see cref="RectNormalized"/> source crop that a
/// compositor consumes. The crop is expanded to absorb any fit overflow so the visible image maps exactly
/// into the destination rect and never spills onto neighbouring layers (enabling clean split-screen).
/// </summary>
public static class PlacementResolver
{
    /// <param name="destRect">Destination rectangle on the canvas, normalized to [0,1].</param>
    /// <param name="fit">How the cropped source fills the destination rectangle.</param>
    /// <param name="insetLeft">Fraction [0,1) trimmed from the source's left edge (and similarly the others).</param>
    public static (LayerTransform2D Transform, RectNormalized Crop) Resolve(
        RectNormalized destRect,
        PlacementFit fit,
        float insetLeft,
        float insetTop,
        float insetRight,
        float insetBottom,
        VideoFormat source,
        VideoFormat canvas)
    {
        if (source.Width <= 0 || source.Height <= 0 || canvas.Width <= 0 || canvas.Height <= 0)
            return (LayerTransform2D.Identity, RectNormalized.Full);

        // Edges are ORDERED but deliberately NOT clamped to [0,1]. A layer may sit partly or wholly outside
        // the canvas on purpose (a lower third sliding in, a source oversized to fill and crop, a layer
        // dragged off the edge), and clamping turned that into a SMALLER destination rectangle - so the fit
        // below scaled the source DOWN into whatever sliver still overlapped. A full-canvas layer nudged into
        // the corner then rendered complete but shrunk instead of being cut off by the canvas edge, and a
        // negative offset was erased altogether, which is the whole of the reported defect. Clipping against
        // the canvas happens after the fit, below, where it can be applied to the correct edge.
        var x0 = MathF.Min(destRect.X0, destRect.X1);
        var x1 = MathF.Max(destRect.X0, destRect.X1);
        var y0 = MathF.Min(destRect.Y0, destRect.Y1);
        var y1 = MathF.Max(destRect.Y0, destRect.Y1);
        var dx = x0 * canvas.Width;
        var dy = y0 * canvas.Height;
        var dw = MathF.Max(1f, (x1 - x0) * canvas.Width);
        var dh = MathF.Max(1f, (y1 - y0) * canvas.Height);

        // User crop sub-rectangle (after edge insets), in source pixels.
        var sx0 = Math.Clamp(insetLeft, 0f, 0.99f) * source.Width;
        var sy0 = Math.Clamp(insetTop, 0f, 0.99f) * source.Height;
        var sx1 = (1f - Math.Clamp(insetRight, 0f, 0.99f)) * source.Width;
        var sy1 = (1f - Math.Clamp(insetBottom, 0f, 0.99f)) * source.Height;
        if (sx1 <= sx0) sx1 = MathF.Min(source.Width, sx0 + 1f);
        if (sy1 <= sy0) sy1 = MathF.Min(source.Height, sy0 + 1f);
        var cw = sx1 - sx0;
        var ch = sy1 - sy0;

        var (scaleX, scaleY) = fit switch
        {
            PlacementFit.Stretch => (dw / cw, dh / ch),
            PlacementFit.Cover => (MathF.Max(dw / cw, dh / ch), MathF.Max(dw / cw, dh / ch)),
            PlacementFit.FillWidth => (dw / cw, dw / cw),
            PlacementFit.FillHeight => (dh / ch, dh / ch),
            _ => (MathF.Min(dw / cw, dh / ch), MathF.Min(dw / cw, dh / ch)), // Contain
        };

        var imgW = cw * scaleX;
        var imgH = ch * scaleY;

        // Where the scaled image overflows the dest rect, trim the source crop on that axis so the visible
        // part maps exactly into the dest rect (centered) - this is what keeps adjacent layers from spilling.
        if (imgW > dw + 0.5f)
        {
            var trimSrc = (imgW - dw) / scaleX;
            sx0 += trimSrc * 0.5f;
            sx1 -= trimSrc * 0.5f;
            cw = sx1 - sx0;
            imgW = cw * scaleX;
        }

        if (imgH > dh + 0.5f)
        {
            var trimSrc = (imgH - dh) / scaleY;
            sy0 += trimSrc * 0.5f;
            sy1 -= trimSrc * 0.5f;
            ch = sy1 - sy0;
            imgH = ch * scaleY;
        }

        // Center the (possibly smaller) image within the dest rect - letterbox/pillarbox on underflow.
        var ox = dx + (dw - imgW) * 0.5f;
        var oy = dy + (dh - imgH) * 0.5f;

        // Clip the placed image against the CANVAS, discarding whatever hangs over an edge. This is ONE-SIDED
        // by nature - what overhangs the right edge is the image's right side - and that is exactly what
        // distinguishes it from the fit-overflow trim above, which is centred because it is absorbing a
        // scale mismatch rather than an edge. Doing it here, on the placed image, instead of by clamping the
        // destination rect, is what keeps the authored scale intact: the layer is cut off, not shrunk.
        var visX0 = MathF.Max(ox, 0f);
        var visY0 = MathF.Max(oy, 0f);
        var visX1 = MathF.Min(ox + imgW, canvas.Width);
        var visY1 = MathF.Min(oy + imgH, canvas.Height);
        if (visX1 > visX0 && visY1 > visY0)
        {
            sx0 += (visX0 - ox) / scaleX;
            sx1 -= (ox + imgW - visX1) / scaleX;
            sy0 += (visY0 - oy) / scaleY;
            sy1 -= (oy + imgH - visY1) / scaleY;
            ox = visX0;
            oy = visY0;
        }
        else
        {
            // Wholly off canvas - the far end of the authorable range. Collapse the crop so no stray line of
            // pixels is sampled; the transform would place it outside the canvas in any case.
            sx1 = sx0;
            sy1 = sy0;
        }

        // Source pixel s -> dest: scale*s + (origin - scale*cropTopLeft).
        var transform = new LayerTransform2D(
            scaleX, 0f, ox - scaleX * sx0,
            0f, scaleY, oy - scaleY * sy0);

        var crop = new RectNormalized(
            sx0 / source.Width,
            sy0 / source.Height,
            sx1 / source.Width,
            sy1 / source.Height).Clamped();

        return (transform, crop);
    }
}
