using System.Collections.Concurrent;
using S.Media.Compositor;
using S.Media.Core;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>
/// Dual-voice crossfade, GPU-surface half (Ideas/Dual-Voice-Crossfade-Design.md, the gap left by the
/// 2026-07-29 review pass). A frame-backed clip can be taken off master alignment because its layer picks
/// a picture out of a queue of submitted frames; a <c>SurfaceLayerSlot</c> (NXT-10) has no queue - it
/// RENDERS at whatever instant the composite hands it - so a crossfade tail's surface would render the
/// INCOMING clip's time the moment <c>ReplaceAsync</c> re-binds the group's timeline. The fix threads a
/// per-surface clock (<c>SurfaceSlot.RenderTimeSource</c> → <see cref="CompositorSurfaceLayer.RenderTime"/>
/// → the surface's <c>Render</c> <c>masterTime</c>); these tests observe the instant each surface is asked
/// to render at, which is the only observable that distinguishes "the tail is playing its own clip" from
/// "the tail is posing at the new clip's playhead".
/// <para>The CPU compositor refuses surfaces, so the host here is a fake that composites on the CPU behind
/// the surface capability and resolves the render time exactly as <c>GlVideoCompositor</c> does
/// (<c>RenderTime ?? presentationTime</c>); <c>S.Media.Compositor.Tests.SurfaceRenderTimeGlTests</c> pins
/// that resolution on a real GL context.</para>
/// </summary>
public sealed class CrossfadeSurfaceTests
{
    /// <summary>One surface render observation: which surface, the instant it renders at, and the
    /// composite's own master time (they differ only for a detached tail).</summary>
    private readonly record struct SurfaceSample(
        IVideoCompositorLayerSurface Surface, TimeSpan RenderTime, TimeSpan MasterTime);

    private sealed class InertSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }
        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity) { }
        public void Dispose() { }
    }

    private sealed class SurfaceRecordingHost(VideoFormat output, ConcurrentQueue<SurfaceSample> samples)
        : IVideoCompositorSurfaceHost
    {
        private readonly CpuVideoCompositor _inner = new(output);

        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat format) => _inner.Configure(format);
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts) => _inner.Composite(layers, pts);

        public VideoFrame CompositeWithSurfaces(
            IReadOnlyList<CompositorLayer> frameLayers,
            IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
            TimeSpan presentationTime)
        {
            foreach (var layer in surfaceLayers)
                samples.Enqueue(new SurfaceSample(
                    layer.Surface, layer.RenderTime ?? presentationTime, presentationTime));
            return _inner.Composite(frameLayers, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>The MMD/visualizer shape: a seekable video source that ALSO renders itself as a GPU layer
    /// surface, so a single-placement clip on a surface-hosting composition takes the NXT-10 path.</summary>
    private sealed class SurfaceCapableVideoSource(int frameCount)
        : IVideoSource, ISeekableSource, ILayerSurfaceVideoSource
    {
        private readonly SyntheticVideoSource _frames = new(frameCount);

        public IVideoCompositorLayerSurface? CreatedSurface;

        public VideoFormat Format => _frames.Format;
        public IReadOnlyList<PixelFormat> NativePixelFormats => _frames.NativePixelFormats;
        public bool IsExhausted => _frames.IsExhausted;
        public TimeSpan Duration => _frames.Duration;
        public TimeSpan Position => _frames.Position;
        public void Seek(TimeSpan position) => _frames.Seek(position);
        public void SelectOutputFormat(PixelFormat format) => _frames.SelectOutputFormat(format);
        public bool TryReadNextFrame(out VideoFrame frame) => _frames.TryReadNextFrame(out frame);

        public IVideoCompositorLayerSurface CreateLayerSurface() => CreatedSurface = new InertSurface();
    }

    private sealed class SurfaceCapableProvider(int frameCount) : IMediaDecoderProvider
    {
        private readonly List<(string Uri, SurfaceCapableVideoSource Source)> _opened = [];

        public string Name => "surface-capable";

        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Video ? 1.0 : 0.0;

        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
        {
            var source = new SurfaceCapableVideoSource(frameCount);
            lock (_opened)
                _opened.Add((uri, source));
            return source;
        }

        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
            throw new NotSupportedException("surface-capable provider is video-only");

        /// <summary>The surface the FIRED clip for <paramref name="uri"/> placed. The standby engine may
        /// open warm copies of the same uri; only the committed one is ever asked for a surface.</summary>
        public async Task<IVideoCompositorLayerSurface> WaitForSurfaceAsync(string uri, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_opened)
                {
                    var placed = _opened
                        .Where(o => o.Uri == uri && o.Source.CreatedSurface is not null)
                        .Select(o => o.Source.CreatedSurface!)
                        .ToArray();
                    if (placed.Length > 0)
                        return Assert.Single(placed);
                }

                Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for '{uri}' to place a layer surface");
                await Task.Delay(25);
            }
        }
    }

    /// <summary>Both voices on ONE surface-hosting composition - the real crossfade shape, and what makes
    /// the bug visible: one composite carries both surfaces and can only be stamped with one master time.
    /// The outgoing clip starts 40 s into its media ("A at 3:12 into B at 0:00").</summary>
    private static ShowDocument SurfaceCues(TimeSpan outgoingStartOffset) => new(
        Version: 1,
        Cues: [new CueDefinition("c1", 1, "ONE"), new CueDefinition("c2", 2, "TWO")],
        Clips:
        [
            new ShowClipBinding("c1", "surf://c1", CompositionId: "screen") { StartOffset = outgoingStartOffset },
            new ShowClipBinding("c2", "surf://c2", CompositionId: "screen"),
        ],
        Compositions: [new ShowComposition("screen", "Screen", 16, 16, 30, 1)],
        Routes: []);

    /// <summary>Dequeues until a sample satisfies <paramref name="predicate"/> (consuming the backlog, so
    /// successive waits observe strictly later composites).</summary>
    private static async Task<SurfaceSample> WaitForSampleAsync(
        ConcurrentQueue<SurfaceSample> samples, Func<SurfaceSample, bool> predicate, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            while (samples.TryDequeue(out var sample))
                if (predicate(sample))
                    return sample;
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Crossfade_OutgoingSurfaceTail_KeepsRenderingItsOwnClipTime()
    {
        var samples = new ConcurrentQueue<SurfaceSample>();
        var provider = new SurfaceCapableProvider(frameCount: 90_000); // 50 min of runway
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(provider)),
            compositorFactory: fmt => new ClipCompositionCompositor(
                new SurfaceRecordingHost(fmt, samples), RequiresBgraLayerConversion: true, "TEST-SURFACE-RECORDER"));
        await session.LoadDocumentAsync(SurfaceCues(TimeSpan.FromSeconds(40)));
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        var tail = await provider.WaitForSurfaceAsync("surf://c1", TimeSpan.FromSeconds(20));

        // While c1 IS the active clip its surface renders at the composition's master time, which is its
        // own trimmed-in position (~40 s). This also proves the clock reflects StartOffset at all.
        await WaitForSampleAsync(
            samples,
            s => ReferenceEquals(s.Surface, tail) && s.RenderTime >= TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(20),
            "the active surface rendering at its trimmed-in position");

        // A 30 s window keeps the tail alive for the whole measurement.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(30), FadeCurve.EqualPower));
        var incoming = await provider.WaitForSurfaceAsync("surf://c2", TimeSpan.FromSeconds(20));

        // The composition's master time has moved to the INCOMING clip (near 0) - the whole reason the
        // tail cannot keep using it.
        var incomingSample = await WaitForSampleAsync(
            samples,
            s => ReferenceEquals(s.Surface, incoming),
            TimeSpan.FromSeconds(20),
            "the incoming clip's surface to composite");
        Assert.Equal(incomingSample.MasterTime, incomingSample.RenderTime); // a normal surface: master time
        Assert.True(incomingSample.MasterTime < TimeSpan.FromSeconds(10),
            $"the incoming clip should be near its start, was at {incomingSample.MasterTime}");

        // The tail: still on ITS clip's coordinate, ~40 s away from the master time it is composited with,
        // and still ADVANCING (a per-surface clock that is merely frozen would be no better than the bug).
        var baseline = await WaitForSampleAsync(
            samples,
            s => ReferenceEquals(s.Surface, tail),
            TimeSpan.FromSeconds(20),
            "the outgoing surface's first post-handoff composite");
        Assert.True(baseline.RenderTime >= TimeSpan.FromSeconds(40),
            $"the tail's surface jumped to the incoming clip's time ({baseline.RenderTime})");
        Assert.True(baseline.RenderTime - baseline.MasterTime >= TimeSpan.FromSeconds(30),
            $"the tail rendered at the composite's master time ({baseline.RenderTime} vs {baseline.MasterTime})");

        var advanced = await WaitForSampleAsync(
            samples,
            s => ReferenceEquals(s.Surface, tail)
                 && s.RenderTime >= baseline.RenderTime + TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(20),
            "the outgoing surface's own clock to keep advancing");
        Assert.True(advanced.RenderTime - advanced.MasterTime >= TimeSpan.FromSeconds(25),
            "the tail drifted onto the composite's master time as the window ran");
    }

    [Fact]
    public async Task WithoutACrossfade_TheSurfaceAlwaysRendersAtTheCompositionMasterTime()
    {
        // The non-crossfade path must be untouched: no clip ever detaches, so every surface renders at
        // exactly the instant the composite is stamped with, as before this seam existed.
        var samples = new ConcurrentQueue<SurfaceSample>();
        var provider = new SurfaceCapableProvider(frameCount: 90_000);
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(provider)),
            compositorFactory: fmt => new ClipCompositionCompositor(
                new SurfaceRecordingHost(fmt, samples), RequiresBgraLayerConversion: true, "TEST-SURFACE-RECORDER"));
        await session.LoadDocumentAsync(SurfaceCues(TimeSpan.FromSeconds(40)));
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        await provider.WaitForSurfaceAsync("surf://c1", TimeSpan.FromSeconds(20));
        await WaitForSampleAsync(
            samples, s => s.RenderTime > TimeSpan.Zero, TimeSpan.FromSeconds(20), "the first surface composite");

        // Butt splice (no crossfade window): c1 is released before c2 commits, and c2's surface composites
        // on the master time like any ordinary clip.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", crossfade: null));
        await provider.WaitForSurfaceAsync("surf://c2", TimeSpan.FromSeconds(20));

        var seen = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (seen < 20)
        {
            while (samples.TryDequeue(out var sample))
            {
                Assert.Equal(sample.MasterTime, sample.RenderTime);
                seen++;
            }

            Assert.True(DateTime.UtcNow < deadline, $"only observed {seen} surface composites");
            await Task.Delay(25);
        }
    }
}
