using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// One z-order across frame layers and surface layers. A composition used to order the two kinds
/// separately and hand the compositor "frames, then surfaces", so a visualizer always sat on top however
/// it was authored.
/// </summary>
/// <remarks>
/// What is pinned here is the ORDERING each surface is given - the count of frame layers that belong
/// underneath it. That the compositor then honours that count is a pixel-level fact, covered by the
/// real-GL tests in <c>SurfaceZOrderGlTests</c>.
/// </remarks>
public sealed class LayerZOrderTests
{
    private sealed class StubSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

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

    private static VideoPlacementSpec At(int layerIndex) =>
        new("screen", layerIndex, Placement: "stretch");

    private static readonly VideoFormat Source = new(64, 48, PixelFormat.Bgra32, new Rational(25, 1));

    [Fact]
    public void ASurfaceAboveEveryFrameLayer_SitsOnTop()
    {
        using var runtime = Runtime();
        using var lower = runtime.AddLayer(Source, At(1));
        using var upper = runtime.AddLayer(Source, At(2));

        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(5));

        Assert.Equal(2, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void ASurfaceBelowEveryFrameLayer_HasNothingUnderneath()
    {
        using var runtime = Runtime();
        using var frame = runtime.AddLayer(Source, At(5));

        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(1));

        // The case that was impossible before: a clip authored above a visualizer now covers it.
        Assert.Equal(0, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void ASurfaceBetweenFrameLayers_SplitsThem()
    {
        using var runtime = Runtime();
        using var bottom = runtime.AddLayer(Source, At(1));
        using var top = runtime.AddLayer(Source, At(9));

        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(5));

        Assert.Equal(1, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void AFrameLayerAddedLater_RepositionsTheSurface()
    {
        using var runtime = Runtime();
        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(5));
        Assert.Equal(0, surface.RawSlot.DrawAfterFrameLayers);

        using var below = runtime.AddLayer(Source, At(1));

        // Every surface's count depends on the frame layers around it, so adding one must re-run the merge.
        Assert.Equal(1, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void RemovingTheLastFrameLayer_ResetsTheSurfaceToTheBottom()
    {
        using var runtime = Runtime();
        var below = runtime.AddLayer(Source, At(1));
        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(5));
        Assert.Equal(1, surface.RawSlot.DrawAfterFrameLayers);

        below.Dispose();

        // The removal path used to skip the re-sort when no frame layers were left, which would strand a
        // count of 1 pointing at a list that no longer has anything in it.
        Assert.Equal(0, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void AtTheSameLayerIndex_AttachOrderDecides()
    {
        using var runtime = Runtime();
        using var firstFrame = runtime.AddLayer(Source, At(3));

        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(3));

        // Sequence breaks the tie, exactly as it always has between two frame layers: the later attach
        // goes on top, which is what an operator sees when firing onto an occupied index.
        Assert.Equal(1, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void AtTheSameLayerIndex_ASurfaceAttachedFirst_StaysUnderneath()
    {
        using var runtime = Runtime();
        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(3));

        using var laterFrame = runtime.AddLayer(Source, At(3));

        Assert.Equal(0, surface.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void SeveralSurfacesInterleaveIndependently()
    {
        using var runtime = Runtime();
        using var f1 = runtime.AddLayer(Source, At(2));
        using var f2 = runtime.AddLayer(Source, At(4));
        using var f3 = runtime.AddLayer(Source, At(6));

        using var low = runtime.AddSurfaceLayer(new StubSurface(), At(1));
        using var mid = runtime.AddSurfaceLayer(new StubSurface(), At(5));
        using var high = runtime.AddSurfaceLayer(new StubSurface(), At(7));

        Assert.Equal(0, low.RawSlot.DrawAfterFrameLayers);
        Assert.Equal(2, mid.RawSlot.DrawAfterFrameLayers);
        Assert.Equal(3, high.RawSlot.DrawAfterFrameLayers);
    }

    [Fact]
    public void MovingAPlacementUpdatesTheOrdering()
    {
        using var runtime = Runtime();
        using var frame = runtime.AddLayer(Source, At(5));
        using var surface = runtime.AddSurfaceLayer(new StubSurface(), At(1));
        Assert.Equal(0, surface.RawSlot.DrawAfterFrameLayers);

        surface.UpdatePlacement(At(9));

        // Re-authoring a visualizer's layer index is the whole point of making z-order free.
        Assert.Equal(1, surface.RawSlot.DrawAfterFrameLayers);
    }
}
