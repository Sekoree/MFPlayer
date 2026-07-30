using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Compositor.Tests;

/// <summary>
/// A layer placed partly or wholly OUTSIDE the canvas must be CLIPPED by the canvas edge - it must not be
/// squeezed to fit inside it.
/// <para>Reported from a live show: a full-canvas video layer dragged into the composition's lower-right
/// corner, so three quarters of it should have fallen off the canvas, instead rendered the whole frame
/// complete but shrunk into that corner. Dragging right trimmed the image on the wrong sides (symmetrically,
/// left AND right), and dragging left or up did nothing at all. One line caused all of it:
/// <c>PlacementResolver.Resolve</c> opened with <c>destRect.Clamped()</c>, so an off-canvas destination
/// became a SMALLER destination, and the fit logic then scaled the source down into it. The authored size was
/// lost, and the overflow trim - which is centred, correctly, for absorbing FIT overflow - took the excess off
/// both sides instead of off the edge that actually hangs over.</para>
/// <para>These assert the source CROP, because that is what says which part of the image survives. The
/// distinction the old code could not express: shrink-to-fit keeps the whole frame (crop stays full), clipping
/// discards the off-canvas part (crop shrinks on that side only).</para>
/// </summary>
public sealed class PlacementResolverOutOfBoundsTests
{
    private static readonly Rational Fps = new(60, 1);
    private static readonly VideoFormat Canvas = new(1920, 1080, PixelFormat.Bgra32, Fps);
    private static readonly VideoFormat Source = new(1280, 720, PixelFormat.Bgra32, Fps);

    /// <summary>A full-size layer offset by (dx, dy) of the canvas - what dragging a 1.00x1.00 placement
    /// does.</summary>
    private static RectNormalized FullSizeAt(float dx, float dy) => new(dx, dy, dx + 1f, dy + 1f);

    [Fact]
    public void FullSizeLayerAtTheLowerRightCorner_ShowsItsTopLeftQuarter_NotTheWholeFrameShrunk()
    {
        // The exact report: dragged so only the top-left quarter of the layer is still on the canvas.
        var (transform, crop) = PlacementResolver.Resolve(
            FullSizeAt(0.5f, 0.5f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);

        // Scale is that of the AUTHORED size (a full-canvas layer stretched over the whole canvas), NOT of
        // the quarter it happens to overlap - the old code halved it, which is the shrink.
        Assert.Equal(Canvas.Width / (float)Source.Width, transform.M11, 3);
        Assert.Equal(Canvas.Height / (float)Source.Height, transform.M22, 3);

        // Only the top-left quarter of the SOURCE survives; the rest hangs off the right/bottom edges.
        Assert.Equal(0f, crop.X0, 3);
        Assert.Equal(0f, crop.Y0, 3);
        Assert.Equal(0.5f, crop.X1, 3);
        Assert.Equal(0.5f, crop.Y1, 3);

        // …and that surviving quarter is drawn in the canvas's lower-right quarter, starting exactly at the
        // canvas midpoint.
        Assert.Equal(Canvas.Width * 0.5f, transform.Apply(crop.X0 * Source.Width, 0f).X, 1);
        Assert.Equal(Canvas.Height * 0.5f, transform.Apply(0f, crop.Y0 * Source.Height).Y, 1);
        // …and its bottom-right corner lands exactly on the canvas's far corner: cut off, not shrunk.
        Assert.Equal(Canvas.Width, transform.Apply(crop.X1 * Source.Width, 0f).X, 1);
        Assert.Equal(Canvas.Height, transform.Apply(0f, crop.Y1 * Source.Height).Y, 1);
    }

    [Fact]
    public void FullSizeLayerPushedRight_LosesItsRIGHTSide_NotBothSides()
    {
        // "Moving right sort of trims the frame on the left and maybe the right" - the centred overflow trim
        // took it off both. What hangs over the right edge is the image's right side, and only that.
        var (transform, crop) = PlacementResolver.Resolve(
            FullSizeAt(0.25f, 0f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);

        Assert.Equal(0f, crop.X0, 3);      // the left edge of the image is still on canvas, untouched
        Assert.Equal(0.75f, crop.X1, 3);   // the right quarter is off-canvas and gone
        Assert.Equal(0f, crop.Y0, 3);      // the vertical axis is untouched
        Assert.Equal(1f, crop.Y1, 3);
        Assert.Equal(Canvas.Width * 0.25f, transform.Apply(crop.X0 * Source.Width, 0f).X, 1);
        // The surviving right edge lands on the canvas edge - the overhang was cut, not squeezed in.
        Assert.Equal(Canvas.Width, transform.Apply(crop.X1 * Source.Width, 0f).X, 1);
    }

    [Fact]
    public void FullSizeLayerPushedLeft_LosesItsLEFTSide_AndDoesNotJustFillTheCanvas()
    {
        // "Moving left or up does nothing - it fills the composition." A negative destination was clamped
        // away entirely, so the layer rendered as if it were still at 0.
        var (transform, crop) = PlacementResolver.Resolve(
            FullSizeAt(-0.25f, 0f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);

        Assert.Equal(0.25f, crop.X0, 3);   // the left quarter is off-canvas and gone
        Assert.Equal(1f, crop.X1, 3);      // the right edge is still on canvas
        // The transform is expressed over SOURCE pixels, so what matters is where the crop's leading edge
        // lands: the first surviving column must sit exactly on the canvas's left edge. (Tx itself is
        // legitimately negative here - the un-drawn columns to its left are still part of the mapping.)
        Assert.Equal(0f, transform.Apply(crop.X0 * Source.Width, 0f).X, 1);
    }

    [Fact]
    public void FullSizeLayerPushedUp_LosesItsTOPSide()
    {
        var (transform, crop) = PlacementResolver.Resolve(
            FullSizeAt(0f, -0.25f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);

        Assert.Equal(0.25f, crop.Y0, 3);
        Assert.Equal(1f, crop.Y1, 3);
        Assert.Equal(0f, transform.Apply(0f, crop.Y0 * Source.Height).Y, 1);
    }

    [Fact]
    public void AnInsideLayerIsUnaffected_AndCoverStillTrimsSymmetrically()
    {
        // The regression guard for everything that was already right. A layer wholly inside the canvas must
        // resolve exactly as before…
        var (_, inside) = PlacementResolver.Resolve(
            new RectNormalized(0.25f, 0.25f, 0.75f, 0.75f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);
        Assert.Equal(RectNormalized.Full, inside);

        // …and Cover's overflow trim - which absorbs FIT overflow, not canvas overhang - stays centred, which
        // is what keeps a split-screen layer from spilling onto its neighbour.
        var (_, cover) = PlacementResolver.Resolve(
            new RectNormalized(0f, 0f, 0.5f, 1f), PlacementFit.Cover, 0, 0, 0, 0, Source, Canvas);
        Assert.True(cover.X0 > 0f, "Cover should trim the source horizontally");
        Assert.Equal(cover.X0, 1f - cover.X1, 3); // symmetric: equal amounts off each side
    }

    [Fact]
    public void AFullyOffCanvasLayer_ResolvesWithoutThrowing_AndShowsNothingOnCanvas()
    {
        // The far end of the drag range (NormalizedRectRange allows exactly fully-outside), so it must be a
        // no-op rather than a crash or a stray sliver.
        var (_, crop) = PlacementResolver.Resolve(
            FullSizeAt(1f, 1f), PlacementFit.Stretch, 0, 0, 0, 0, Source, Canvas);
        Assert.True(crop.Width <= 0.001f || crop.Height <= 0.001f, $"expected an empty crop, got {crop}");
    }
}
