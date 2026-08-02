using S.Media.Core.Video;
using S.Media.Present.SDL3;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Free z-ordering between surface layers and frame layers. Surfaces used to render unconditionally on top
/// of every frame layer, which made "a visualizer is an ordinary layer" untrue: nothing could ever be
/// authored above one.
/// </summary>
/// <remarks>
/// These read real pixels back off a real GL context, because the thing under test is the order of draw
/// calls into one framebuffer - a mock host records what it was handed, not what actually landed on the
/// canvas, and the failure mode this guards against (the canvas clear running once per run and erasing
/// earlier runs) is invisible to anything but the pixels.
/// </remarks>
[Collection(GlContextCollection.Name)]
public sealed class SurfaceZOrderGlTests
{
    private const int W = 32;
    private const int H = 16;
    private static readonly VideoFormat Canvas = new(W, H, PixelFormat.Bgra32, new Rational(30, 1));

    /// <summary>Fills the whole canvas with one opaque colour, so whoever draws last simply wins.</summary>
    private sealed class SolidSurface(float r, float g, float b) : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }

        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
        {
            gl.ClearColor(r, g, b, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        public void Dispose() { }
    }

    /// <summary>A full-canvas opaque frame layer of one colour.</summary>
    private static CompositorLayer FrameLayer(byte b, byte g, byte r)
    {
        var pixels = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            pixels[i * 4] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = 255;
        }

        var frame = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(30, 1)),
            pixels, W * 4,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
        return new CompositorLayer(frame, LayerTransform2D.Identity, 1f, BlendMode.SourceOver);
    }

    private static readonly (byte B, byte G, byte R) Red = (0, 0, 255);
    private static readonly (byte B, byte G, byte R) Blue = (255, 0, 0);

    [SkippableFact]
    public void ASurfaceWithNoDrawOrder_StaysOnTop_AsItAlwaysDid()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // Green surface, no DrawAfterFrameLayers: the historical contract, and what any host that ignores
        // the new field must keep doing.
        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)]);

        AssertGreen(pixel);
    }

    [SkippableFact]
    public void ASurfaceBelowAFrameLayer_IsCoveredByIt()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // DrawAfterFrameLayers = 0: nothing underneath, so the one frame layer paints over it. This is the
        // case that was impossible before.
        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)
                { DrawAfterFrameLayers = 0 }]);

        AssertColour(pixel, Red, "the frame layer above the surface");
    }

    [SkippableFact]
    public void ASurfaceBetweenTwoFrameLayers_CoversTheLowerAndIsCoveredByTheUpper()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R), FrameLayer(Blue.B, Blue.G, Blue.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)
                { DrawAfterFrameLayers = 1 }]);

        // Blue is the top frame layer, so it wins - and the fact that it is not GREEN proves the surface
        // was spliced in the middle rather than left on top.
        AssertColour(pixel, Blue, "the frame layer above the surface");
    }

    [SkippableFact]
    public void FrameLayersBelowASurface_AreNotErasedByTheSurfacePass()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // THE clear trap: the canvas clear must happen once per composite, not once per frame-layer run.
        // A half-transparent surface over a red frame layer should tint it, not reveal a cleared canvas.
        var pixels = CompositeToPixels(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)
                { DrawAfterFrameLayers = 1 }]);
        var (_, _, _, a) = PixelAt(pixels, W / 2, H / 2);

        Assert.True(a > 250, $"the canvas must stay opaque where a frame layer painted it, got alpha {a}");
    }

    [SkippableFact]
    public void AnInvisibleSurface_DoesNotSwallowTheFrameLayersBelowIt()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // Opacity 0 skips the surface's render - but its placement still says where the frame layers below
        // it stop, so skipping the whole placement would strand them undrawn.
        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 0f)
                { DrawAfterFrameLayers = 0 }]);

        AssertColour(pixel, Red, "the frame layer under an invisible surface");
    }

    [SkippableFact]
    public void ADrawOrderPastTheEnd_IsClampedToOnTop()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // A stale count (the frame layer it was computed against has since gone) must not index out of
        // range - it just means "above everything left".
        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)
                { DrawAfterFrameLayers = 99 }]);

        AssertGreen(pixel);
    }

    [SkippableFact]
    public void TwoSurfacesStraddlingAFrameLayer_EachLandOnTheirOwnSide()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        // The bottom surface renders FIRST, before any frame layer - the case where the canvas state must
        // be re-established before the frame run even though no frame layer has been drawn yet.
        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R)],
            [
                new CompositorSurfaceLayer(new SolidSurface(0, 0, 1), LayerTransform2D.Identity, 1f)
                    { DrawAfterFrameLayers = 0 },
                new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 1f)
                    { DrawAfterFrameLayers = 1 },
            ]);

        // Blue surface, then the red frame layer, then the green surface: green wins.
        AssertGreen(pixel);
    }

    [SkippableFact]
    public void WithNoSurfaces_FrameLayerOrderIsUnchanged()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var error), $"no GL on this host: {error}");

        var pixel = CompositeCentrePixel(
            [FrameLayer(Red.B, Red.G, Red.R), FrameLayer(Blue.B, Blue.G, Blue.R)],
            [new CompositorSurfaceLayer(new SolidSurface(0, 1, 0), LayerTransform2D.Identity, 0f)]);

        AssertColour(pixel, Blue, "the last frame layer");
    }

    private static void AssertGreen((byte B, byte G, byte R, byte A) pixel) =>
        Assert.True(
            pixel.G > 200 && pixel.R < 40 && pixel.B < 40,
            $"expected the green surface on top, got b={pixel.B} g={pixel.G} r={pixel.R}");

    private static void AssertColour((byte B, byte G, byte R, byte A) pixel, (byte B, byte G, byte R) want, string what)
    {
        Assert.True(
            Math.Abs(pixel.B - want.B) < 40 && Math.Abs(pixel.G - want.G) < 40 && Math.Abs(pixel.R - want.R) < 40,
            $"expected {what} (b={want.B} g={want.G} r={want.R}), got b={pixel.B} g={pixel.G} r={pixel.R}");
    }

    private static (byte B, byte G, byte R, byte A) CompositeCentrePixel(
        IReadOnlyList<CompositorLayer> frameLayers, IReadOnlyList<CompositorSurfaceLayer> surfaceLayers) =>
        PixelAt(CompositeToPixels(frameLayers, surfaceLayers), W / 2, H / 2);

    /// <summary>Composites a few times (the readback is pipelined, so early frames lag) and returns the
    /// final frame's BGRA pixels.</summary>
    private static byte[] CompositeToPixels(
        IReadOnlyList<CompositorLayer> frameLayers, IReadOnlyList<CompositorSurfaceLayer> surfaceLayers)
    {
        Assert.True(SDL3GLVideoCompositor.TryCreate(Canvas, out var compositor, out var error), error);
        try
        {
            var host = Assert.IsAssignableFrom<IVideoCompositorSurfaceHost>(compositor);
            for (var i = 0; i < 3; i++)
                host.CompositeWithSurfaces(frameLayers, surfaceLayers, TimeSpan.FromMilliseconds(i * 33)).Dispose();
            using var frame = host.CompositeWithSurfaces(frameLayers, surfaceLayers, TimeSpan.FromMilliseconds(99));
            var stride = frame.Strides[0];
            var plane = frame.Planes[0].Span;
            var pixels = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
                plane.Slice(y * stride, W * 4).CopyTo(pixels.AsSpan(y * W * 4));
            return pixels;
        }
        finally
        {
            compositor?.Dispose();
        }
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int x, int y)
    {
        var i = (y * W + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }
}
