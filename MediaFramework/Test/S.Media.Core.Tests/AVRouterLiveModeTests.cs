using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers §6.1 / R23: per-route <see cref="VideoRouteOptions.LiveMode"/> is stored on
/// the route entry and surfaced via <see cref="RouterDiagnosticsSnapshot"/>.
/// The full PTS-gate bypass is tested via the diagnostics plumbing; the functional
/// bypass is exercised by the NDIAutoPlayer integration path.
/// </summary>
public sealed class AVRouterLiveModeTests
{
    [Fact]
    public void LiveMode_Default_IsFalse()
    {
        Assert.False(new VideoRouteOptions().LiveMode);
    }

    [Fact]
    public void LiveMode_True_IsReflectedInDiagnostics()
    {
        using var router = new AVRouter();
        var videoInputId = router.RegisterVideoInput(new VideoChannelStub());
        var videoEpId    = router.RegisterEndpoint(new VideoEndpointStub());

        router.CreateRoute(videoInputId, videoEpId,
            new VideoRouteOptions { LiveMode = true });

        var route = router.GetDiagnosticsSnapshot().Routes
            .Single(r => r.Kind == "Video");
        Assert.True(route.LiveMode);
    }

    [Fact]
    public void LiveMode_False_IsReflectedInDiagnostics()
    {
        using var router = new AVRouter();
        var videoInputId = router.RegisterVideoInput(new VideoChannelStub());
        var videoEpId    = router.RegisterEndpoint(new VideoEndpointStub());

        router.CreateRoute(videoInputId, videoEpId,
            new VideoRouteOptions { LiveMode = false });

        var route = router.GetDiagnosticsSnapshot().Routes
            .Single(r => r.Kind == "Video");
        Assert.False(route.LiveMode);
    }

    [Fact]
    public void LiveMode_DefaultRoute_DiagnosticsShowFalse()
    {
        using var router = new AVRouter();
        var videoInputId = router.RegisterVideoInput(new VideoChannelStub());
        var videoEpId    = router.RegisterEndpoint(new VideoEndpointStub());

        router.CreateRoute(videoInputId, videoEpId); // default: LiveMode = false

        var route = router.GetDiagnosticsSnapshot().Routes
            .Single(r => r.Kind == "Video");
        Assert.False(route.LiveMode);
    }

    // ── stubs ─────────────────────────────────────────────────────────────

    private sealed class VideoEndpointStub : IVideoEndpoint
    {
        public string Name      => "VideoStub";
        public bool   IsRunning => false;
        public Task   StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task   StopAsync (CancellationToken ct = default) => Task.CompletedTask;
        public void   ReceiveFrame(in VideoFrameHandle handle) { _ = handle; }
        public void   Dispose() { }
    }

    private sealed class VideoChannelStub : IVideoChannel
    {
        public Guid          Id            { get; } = Guid.NewGuid();
        public bool          IsOpen        => true;
        public bool          CanSeek       => false;
        public void          Seek(TimeSpan position) { }
        public VideoFormat   SourceFormat  { get; } = new(1920, 1080, PixelFormat.Yuv420p, 30, 1);
        public TimeSpan      Position      => TimeSpan.Zero;
        public int           BufferDepth   => 4;
        public int           BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler?                          EndOfStream    { add { } remove { } }
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public IVideoSubscription Subscribe(VideoSubscriptionOptions opts) => new NopSub();
        public void Dispose() { }

        private sealed class NopSub : IVideoSubscription
        {
            public int  FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
            public bool TryRead(out VideoFrame frame) { frame = default; return false; }
            public int  Count     => 0;
            public int  Capacity  => 4;
            public bool IsCompleted => false;
            public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
            public void Dispose() { }
        }
    }
}
