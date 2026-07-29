using S.Media.Compositor.Effects;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Real-GL coverage for the per-surface render clock (<see cref="CompositorSurfaceLayer.RenderTime"/>,
/// the surface half of the dual-voice crossfade fix). A surface RENDERS at the instant it is handed -
/// there is no frame queue to re-select from - so a crossfade tail can only keep showing its own clip if
/// the compositor forwards a per-placement time. These tests observe exactly that: a fake surface records
/// the <c>masterTime</c> the GL compositor asks it to render at. Skips when the host has no GL.
/// </summary>
[Collection(GlContextCollection.Name)]
public sealed class SurfaceRenderTimeGlTests
{
    private const int W = 16;
    private const int H = 8;
    private static readonly VideoFormat Canvas = new(W, H, PixelFormat.Bgra32, new Rational(30, 1));

    /// <summary>Records every instant it is asked to render at (and paints, so the draw path is real).</summary>
    private sealed class TimeRecordingSurface : IVideoCompositorLayerSurface
    {
        public readonly List<TimeSpan> RenderTimes = [];

        public void ConfigureGl(GL gl, VideoFormat canvas) { }

        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
        {
            RenderTimes.Add(masterTime);
            gl.ClearColor(0f, 1f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        public void Dispose() { }
    }

    [SkippableFact]
    public void DetachedSurface_RendersAtItsOwnTime_WhileTheRestOfTheCompositeKeepsMasterTime()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var glError), $"no GL on this host: {glError}");

        var tail = new TimeRecordingSurface();     // the crossfade's outgoing voice
        var incoming = new TimeRecordingSurface(); // the clip that now owns the canvas clock

        Assert.True(SDL3GLVideoCompositor.TryCreate(Canvas, out var compositor, out var error), error);
        using (compositor)
        {
            var host = Assert.IsAssignableFrom<IVideoCompositorSurfaceHost>(compositor);
            var master = TimeSpan.FromSeconds(1.5);
            var tailTime = TimeSpan.FromSeconds(192); // "A at 3:12 crossfading into B at 0:00"

            host.CompositeWithSurfaces(
                [],
                [
                    new CompositorSurfaceLayer(tail, LayerTransform2D.Identity, 1f) { RenderTime = tailTime },
                    new CompositorSurfaceLayer(incoming, LayerTransform2D.Identity, 1f),
                ],
                master).Dispose();

            Assert.Equal(tailTime, Assert.Single(tail.RenderTimes));
            Assert.Equal(master, Assert.Single(incoming.RenderTimes));
        }
    }

    [SkippableFact]
    public void SurfaceWithoutARenderTime_StillGetsTheCompositeMasterTime_Unchanged()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var glError), $"no GL on this host: {glError}");

        var surface = new TimeRecordingSurface();
        Assert.True(SDL3GLVideoCompositor.TryCreate(Canvas, out var compositor, out var error), error);
        using (compositor)
        {
            var host = Assert.IsAssignableFrom<IVideoCompositorSurfaceHost>(compositor);
            var layer = new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f);
            for (var i = 0; i < 3; i++)
                host.CompositeWithSurfaces([], [layer], TimeSpan.FromMilliseconds(i * 33)).Dispose();
        }

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(66)],
            surface.RenderTimes);
    }

    [SkippableFact]
    public void RenderTime_AlsoReachesTheIndirectPath_EffectChainAndMappingSections()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var glError), $"no GL on this host: {glError}");

        // A crossfade tail may carry a chroma key or a section mapping, which routes the surface through
        // the intermediate-FBO path; the per-placement clock must survive that hop too.
        var keyed = new TimeRecordingSurface();
        var mapped = new TimeRecordingSurface();
        var keyedTime = TimeSpan.FromSeconds(41);
        var mappedTime = TimeSpan.FromSeconds(42);

        Assert.True(SDL3GLVideoCompositor.TryCreate(Canvas, out var compositor, out var error), error);
        using (compositor)
        {
            var host = Assert.IsAssignableFrom<IVideoCompositorSurfaceHost>(compositor);
            host.CompositeWithSurfaces(
                [],
                [
                    new CompositorSurfaceLayer(keyed, LayerTransform2D.Identity, 1f)
                    {
                        Effects = [ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen)],
                        RenderTime = keyedTime,
                    },
                    new CompositorSurfaceLayer(mapped, LayerTransform2D.Identity, 1f)
                    {
                        MappingSections = [new WarpSection(RectNormalized.Full, LayerTransform2D.Identity, 1f)],
                        RenderTime = mappedTime,
                    },
                ],
                TimeSpan.FromSeconds(1)).Dispose();

            Assert.Equal(keyedTime, Assert.Single(keyed.RenderTimes));
            Assert.Equal(mappedTime, Assert.Single(mapped.RenderTimes));
        }
    }
}
