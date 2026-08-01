using System.Collections.Concurrent;
using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// Several visualizer cues on one composition (rev-3 decision D5). The slot dictionary was keyed by
/// composition alone, so attaching a second visualizer silently replaced the first - which made
/// "the visualizer is an ordinary layer like any other" untrue.
/// </summary>
public sealed class MultiVisualizerTests
{
    /// <summary>Surface-hosting CPU compositor (no GL) so visualizers can attach headlessly.</summary>
    private sealed class SurfaceHost(VideoFormat output) : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);
        public readonly ConcurrentQueue<int> SurfaceLayerCounts = new();
        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output) => _inner.Configure(output);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);

        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime)
        {
            SurfaceLayerCounts.Enqueue(surfaceLayers.Count);
            return _inner.Composite(frameLayers, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class MinimalSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    /// <summary>Counts surface creations: projectM crashes if a source is asked for more than one
    /// surface, so the "one surface per source, N placements per surface" rule is a hard constraint.</summary>
    private sealed class FakeVisualizer : IAudioVisualSource, ILayerSurfaceVideoSource, IDisposable
    {
        public int SurfacesCreated { get; private set; }
        public bool Disposed { get; private set; }
        public VideoFormat Format => new(64, 64, PixelFormat.Bgra32, new Rational(60, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats => [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public bool TryReadNextFrame(out VideoFrame frame) { frame = null!; return false; }
        public void SelectOutputFormat(PixelFormat format) { }
        AudioFormat IAudioOutput.Format => new(48_000, 2);
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public IVideoCompositorLayerSurface CreateLayerSurface() { SurfacesCreated++; return new MinimalSurface(); }
        public void Dispose() => Disposed = true;
    }

    private static ShowDocument CanvasDoc() => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 128, 72, 60, 1)],
        Routes: []);

    private static ShowSession Session() =>
        new(MediaRegistry.Build(_ => { }),
            compositorFactory: fmt => new ClipCompositionCompositor(
                new SurfaceHost(fmt), RequiresBgraLayerConversion: true, "TEST-SURFACE-HOST"));

    [Fact]
    public async Task TwoVisualizerCues_CoexistOnOneComposition()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var first = new FakeVisualizer();
        var second = new FakeVisualizer();

        Assert.True(await session.SetCompositionVisualizerAsync("screen", first, visualizerId: "cue-a"));
        Assert.True(await session.SetCompositionVisualizerAsync("screen", second, visualizerId: "cue-b"));

        // Attaching the second must not tear the first down - that silent replacement is the bug.
        Assert.False(first.Disposed, "the first visualizer was replaced by the second");
        Assert.False(second.Disposed);
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task RemovingOneVisualizer_LeavesTheOtherRunning()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var first = new FakeVisualizer();
        var second = new FakeVisualizer();
        await session.SetCompositionVisualizerAsync("screen", first, visualizerId: "cue-a");
        await session.SetCompositionVisualizerAsync("screen", second, visualizerId: "cue-b");

        Assert.True(await session.SetCompositionVisualizerAsync("screen", null, visualizerId: "cue-a"));

        Assert.True(first.Disposed, "the removed visualizer was not torn down");
        Assert.False(second.Disposed, "removing one visualizer stopped the other");
        Assert.True(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task ClearingWithoutAnId_RemovesEveryVisualizerOnTheComposition()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var first = new FakeVisualizer();
        var second = new FakeVisualizer();
        await session.SetCompositionVisualizerAsync("screen", first, visualizerId: "cue-a");
        await session.SetCompositionVisualizerAsync("screen", second, visualizerId: "cue-b");

        // "Clear this canvas" has always meant all of it, and still does.
        Assert.True(await session.SetCompositionVisualizerAsync("screen", null));

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(await session.HasCompositionVisualizerAsync("screen"));
    }

    [Fact]
    public async Task ReattachingTheSameId_ReplacesInPlace_AsBefore()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var first = new FakeVisualizer();
        var second = new FakeVisualizer();
        await session.SetCompositionVisualizerAsync("screen", first, visualizerId: "cue-a");

        Assert.True(await session.SetCompositionVisualizerAsync("screen", second, visualizerId: "cue-a"));

        Assert.True(first.Disposed, "same-id reattach must replace, not accumulate");
        Assert.False(second.Disposed);
    }

    [Fact]
    public async Task WithoutAnId_TheHistoricalSingleSlotBehaviourIsUnchanged()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var first = new FakeVisualizer();
        var second = new FakeVisualizer();

        await session.SetCompositionVisualizerAsync("screen", first);
        await session.SetCompositionVisualizerAsync("screen", second);

        // Callers that never name a visualizer keep replace-in-place semantics.
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
    }

    [Fact]
    public async Task EachVisualizerGetsExactlyOneSurface_HoweverManyPlacements()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        var viz = new FakeVisualizer();

        Assert.True(await session.SetCompositionVisualizerAsync(
            "screen", viz, visualizerId: "cue-a",
            placements:
            [
                new VideoPlacementSpec("screen", 10, Placement: "stretch"),
                new VideoPlacementSpec("screen", 11, Placement: "stretch"),
                new VideoPlacementSpec("screen", 12, Placement: "stretch"),
            ]));

        // Hard constraint: projectM crashes when a source is asked for more than one surface, so N
        // placements share ONE surface and only the first layer owns it.
        Assert.Equal(1, viz.SurfacesCreated);
    }

    [Fact]
    public async Task PlacementUpdates_AddressTheRightVisualizer()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(CanvasDoc());
        await session.SetCompositionVisualizerAsync("screen", new FakeVisualizer(), visualizerId: "cue-a");
        await session.SetCompositionVisualizerAsync("screen", new FakeVisualizer(), visualizerId: "cue-b");

        Assert.True(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen", new VideoPlacementSpec("screen", 5, Placement: "stretch"), 0, visualizerId: "cue-b"));
        Assert.False(await session.UpdateCompositionVisualizerPlacementAsync(
            "screen", new VideoPlacementSpec("screen", 5, Placement: "stretch"), 0, visualizerId: "no-such-cue"));
    }
}
