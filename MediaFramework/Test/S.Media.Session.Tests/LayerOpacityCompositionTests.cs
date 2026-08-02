using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// The video level composition: <c>authored x fade x automation</c>, the mirror of the audio side's
/// <c>source x fade x envelope x master</c>.
/// </summary>
/// <remarks>
/// Before this chain existed, all three mechanisms wrote the slot's opacity directly and the last writer
/// won. The concrete defect - covered below - is that a live placement edit re-applied the AUTHORED
/// opacity, so editing a placement during a fade snapped the layer to full and the fade then carried on
/// from a value nobody had asked for.
/// </remarks>
public sealed class LayerOpacityCompositionTests
{
    private sealed class StubSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    /// <summary>CPU compositing behind the surface-host capability, so visualizer-shaped slots can be
    /// created headlessly (the plain CPU compositor refuses them).</summary>
    private sealed class SurfaceHost(VideoFormat output) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);

        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime) => _inner.Composite(frameLayers, presentationTime);

        public void Dispose() => _inner.Dispose();
    }

    private static ClipCompositionRuntime Runtime() =>
        new(new ClipCompositionDefinition("screen", "Screen", 64, 48, 25, 1),
            outputs: [],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new SurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));

    private static VideoPlacementSpec Placement(double opacity) =>
        new("screen", 0, Opacity: opacity, Placement: "stretch");

    [Fact]
    public void AFreshLayer_RendersAtItsAuthoredOpacity()
    {
        using var runtime = Runtime();

        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(0.8));

        Assert.Equal(0.8f, layer.BaseOpacity, 4);
        Assert.Equal(1f, layer.FadeLevel, 4);
        Assert.Equal(1f, layer.AutomationLevel, 4);
        Assert.Equal(0.8f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void AFadeScalesTheAuthoredOpacity_RatherThanReplacingIt()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(0.5));

        layer.FadeLevel = 0.5f;

        // Half of a layer authored at half is a quarter - not "half", which is what writing an absolute
        // opacity used to produce and which silently discarded the authoring.
        Assert.Equal(0.25f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void ALivePlacementEdit_DoesNotDisturbAFadeInFlight()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(1.0));
        layer.FadeLevel = 0.25f; // mid fade-out

        layer.UpdatePlacement(Placement(0.6));

        // THE defect: this used to write the authored 0.6 straight onto the slot, snapping the layer
        // brighter mid-fade and leaving the ramp to continue from somewhere else entirely.
        Assert.Equal(0.25f, layer.FadeLevel, 4);
        Assert.Equal(0.6f, layer.BaseOpacity, 4);
        Assert.Equal(0.15f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void AutomationAndFade_Multiply_AndNeitherClobbersTheOther()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(1.0));

        layer.AutomationLevel = 0.5f;
        layer.FadeLevel = 0.5f;

        Assert.Equal(0.25f, layer.EffectiveOpacity, 4);

        // ...and the lane keeps moving underneath the fade without resetting it.
        layer.AutomationLevel = 1f;
        Assert.Equal(0.5f, layer.FadeLevel, 4);
        Assert.Equal(0.5f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void EveryComponentIsClamped_SoNoCombinationExceedsFull()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(1.0));

        layer.FadeLevel = 4f;
        layer.AutomationLevel = 4f;

        // Opacity has no headroom above full the way gain does, so out-of-range is a clamp, not a boost.
        Assert.Equal(1f, layer.EffectiveOpacity, 4);

        layer.FadeLevel = -2f;
        Assert.Equal(0f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void AFrameLayer_ComposesIdenticallyToASurfaceLayer()
    {
        using var runtime = Runtime();
        var source = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));

        using var layer = runtime.AddLayer(source, Placement(0.5));
        layer.FadeLevel = 0.5f;
        layer.AutomationLevel = 0.5f;

        // The two slot kinds have separate implementations; a chain that only held on one of them would
        // make a visualizer and a video clip fade differently on the same canvas.
        Assert.Equal(0.125f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void ClearingAnAuthoredOpacityToZero_BeatsAnyFade()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(1.0));

        layer.UpdatePlacement(Placement(0));

        // A placement authored invisible stays invisible however the ramps move - the product is zero.
        layer.FadeLevel = 1f;
        layer.AutomationLevel = 1f;
        Assert.Equal(0f, layer.EffectiveOpacity, 4);
    }
}
