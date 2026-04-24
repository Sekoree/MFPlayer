using System;
using System.Threading;
using System.Threading.Tasks;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// §10.4 — AVRouter live diagnostics stream + drift snapshot exposure.
/// </summary>
public sealed class AVRouterDiagnosticsEventTests
{
    [Fact]
    public async Task AVRouterDiagnostics_Fires_WhileRunning()
    {
        using var router = new AVRouter(new AVRouterOptions
        {
            AudioTickCadence = TimeSpan.FromMilliseconds(5)
        });

        int count = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        router.AVRouterDiagnostics += snap =>
        {
            if (!snap.IsRunning) return;
            if (Interlocked.Increment(ref count) >= 2)
                tcs.TrySetResult();
        };

        await router.StartAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await router.StopAsync();

        Assert.True(count >= 2);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_ExposesVideoRouteDriftSnapshots()
    {
        using var router = new AVRouter();

        var inputId = router.RegisterVideoInput(new VideoChannelStub());
        var epId = router.RegisterEndpoint(new VideoEndpointStub());
        router.CreateRoute(inputId, epId, new VideoRouteOptions { LiveMode = false });

        var route = Assert.Single(router.GetDiagnosticsSnapshot().Routes);
        Assert.Equal("Video", route.Kind);
        Assert.True(route.PushVideoDrift.HasValue);
        Assert.True(route.PullVideoDrift.HasValue);
        Assert.False(route.PushVideoDrift!.Value.HasOrigin);
        Assert.False(route.PullVideoDrift!.Value.HasOrigin);
    }

    private sealed class VideoEndpointStub : IVideoEndpoint
    {
        public string Name => "VideoStub";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public void Dispose() { }
    }

    private sealed class VideoChannelStub : IVideoChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(1920, 1080, PixelFormat.Yuv420p, 30, 1);
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;

        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public IVideoSubscription Subscribe(VideoSubscriptionOptions options) => new NopSub();
        public void Dispose() { }

        private sealed class NopSub : IVideoSubscription
        {
            public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;

            public bool TryRead(out VideoFrame frame)
            {
                frame = default;
                return false;
            }

            public int Count => 0;
            public int Capacity => 4;
            public bool IsCompleted => false;
            public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
            public void Dispose() { }
        }
    }
}
