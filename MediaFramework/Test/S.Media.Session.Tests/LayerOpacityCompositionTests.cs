using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// The video level composition: <c>authored x fade x automation x modifier</c>, the mirror of the
/// audio side's <c>source x fade x envelope x modifier x master</c>.
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
    public void GroupModifier_ComposesWithoutReplacingPlacementAutomationOrFade()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(0.5));

        layer.AutomationLevel = 0.8f;
        layer.FadeLevel = 0.5f;
        layer.ModifierLevel = 0.5f;

        Assert.Equal(0.5f, layer.BaseOpacity, 4);
        Assert.Equal(0.8f, layer.AutomationLevel, 4);
        Assert.Equal(0.5f, layer.FadeLevel, 4);
        Assert.Equal(0.5f, layer.ModifierLevel, 4);
        Assert.Equal(0.1f, layer.EffectiveOpacity, 4);

        layer.ModifierLevel = 1f;
        Assert.Equal(0.8f, layer.AutomationLevel, 4);
        Assert.Equal(0.5f, layer.FadeLevel, 4);
        Assert.Equal(0.2f, layer.EffectiveOpacity, 4);
    }

    [Fact]
    public void TransformPropertiesComposeIndependentlyOverLiveAuthoredPlacement()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(
            new StubSurface(),
            new VideoPlacementSpec("screen", 0, Opacity: 0.5, DestX: 0.2, DestY: 0.1,
                DestWidth: 0.75, DestHeight: 0.8, RotationDegrees: 10));

        layer.SetPlacementAutomation(ShowPlacementProperty.DestX, 0.8);
        layer.SetPlacementAutomation(ShowPlacementProperty.DestWidth, 0.4);

        Assert.Equal(0.8, layer.EffectivePlacement.DestX, 6);
        Assert.Equal(0.1, layer.EffectivePlacement.DestY, 6);
        Assert.Equal(0.4, layer.EffectivePlacement.DestWidth, 6);
        Assert.Equal(10, layer.EffectivePlacement.RotationDegrees, 6);
        Assert.Equal(0.5f, layer.BaseOpacity, 4);

        // A live authoring edit changes every non-automated field but leaves X/width under their own lanes.
        layer.UpdatePlacement(new VideoPlacementSpec(
            "screen", 0, Opacity: 0.6, DestX: 0.3, DestY: 0.35,
            DestWidth: 0.9, DestHeight: 0.7, RotationDegrees: 25));
        Assert.Equal(0.8, layer.EffectivePlacement.DestX, 6);
        Assert.Equal(0.35, layer.EffectivePlacement.DestY, 6);
        Assert.Equal(0.4, layer.EffectivePlacement.DestWidth, 6);
        Assert.Equal(0.7, layer.EffectivePlacement.DestHeight, 6);
        Assert.Equal(25, layer.EffectivePlacement.RotationDegrees, 6);
        Assert.Equal(0.6f, layer.BaseOpacity, 4);

        layer.ClearPlacementAutomation(ShowPlacementProperty.DestX);
        Assert.Equal(0.3, layer.EffectivePlacement.DestX, 6);
        Assert.Equal(0.4, layer.EffectivePlacement.DestWidth, 6);
    }

    [Fact]
    public void FrameAndSurfaceLayersUseTheSameTransformSlots()
    {
        using var runtime = Runtime();
        var source = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        using var frame = runtime.AddLayer(source, Placement(1));
        using var surface = runtime.AddSurfaceLayer(new StubSurface(), Placement(1));

        foreach (var layer in new ClipCompositionRuntime.IPlacedClipLayer[] { frame, surface })
        {
            layer.SetPlacementAutomation(ShowPlacementProperty.DestY, -0.25);
            layer.SetPlacementAutomation(ShowPlacementProperty.DestHeight, 1.5);
            layer.SetPlacementAutomation(ShowPlacementProperty.RotationDegrees, -45);
            Assert.Equal(-0.25, layer.EffectivePlacement.DestY, 6);
            Assert.Equal(1.5, layer.EffectivePlacement.DestHeight, 6);
            Assert.Equal(-45, layer.EffectivePlacement.RotationDegrees, 6);
        }
    }

    [Fact]
    public void EffectParametersKeepStableInstanceIdentityAndComposeOverLiveAuthoring()
    {
        using var runtime = Runtime();
        var chromaId = Guid.NewGuid().ToString();
        var colorId = Guid.NewGuid().ToString();
        using var layer = runtime.AddLayer(
            new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1)),
            new VideoPlacementSpec(
                "screen", 0,
                ChromaKey: new S.Media.Compositor.ChromaKeySettings(
                    0, 1, 0, Similarity: 0.4f, Smoothness: 0.1f, SpillSuppression: 0.2f),
                ColorAdjust: new S.Media.Compositor.Effects.BrightnessContrastSettings(0.1f, 1.2f),
                ChromaKeyInstanceId: chromaId,
                ColorAdjustInstanceId: colorId));

        layer.SetEffectAutomation(chromaId, ShowPlacementEffectProperty.ChromaSimilarity, 0.8);
        layer.SetEffectAutomation(colorId, ShowPlacementEffectProperty.ColorContrast, 2.5);

        var effects = layer.RawSlot.Effects!;
        Assert.Equal(0.8f, effects[0].Values[3], 5);
        Assert.Equal(0.1f, effects[0].Values[4], 5);
        Assert.Equal(0.1f, effects[1].Values[0], 5);
        Assert.Equal(2.5f, effects[1].Values[1], 5);

        // Editing the authored settings preserves only the parameters owned by automation.
        layer.UpdatePlacement(layer.EffectivePlacement with
        {
            ChromaKey = new S.Media.Compositor.ChromaKeySettings(
                0, 0, 1, Similarity: 0.5f, Smoothness: 0.3f, SpillSuppression: 0.4f),
            ColorAdjust = new S.Media.Compositor.Effects.BrightnessContrastSettings(-0.2f, 1.5f),
        });
        effects = layer.RawSlot.Effects!;
        Assert.Equal(0.8f, effects[0].Values[3], 5);
        Assert.Equal(0.3f, effects[0].Values[4], 5);
        Assert.Equal(-0.2f, effects[1].Values[0], 5);
        Assert.Equal(2.5f, effects[1].Values[1], 5);

        layer.ClearEffectAutomation(chromaId, ShowPlacementEffectProperty.ChromaSimilarity);
        Assert.Equal(0.5f, layer.RawSlot.Effects![0].Values[3], 5);
    }

    [Fact]
    public void EveryComponentIsClamped_SoNoCombinationExceedsFull()
    {
        using var runtime = Runtime();
        using var layer = runtime.AddSurfaceLayer(new StubSurface(), Placement(1.0));

        layer.FadeLevel = 4f;
        layer.AutomationLevel = 4f;
        layer.ModifierLevel = 4f;

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
