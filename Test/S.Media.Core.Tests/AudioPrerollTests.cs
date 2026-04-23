using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// §5.4 — optional audio preroll inside <see cref="AVRouter.StartAsync"/>.
/// When <see cref="AVRouterOptions.MinBufferedFramesPerInput"/> is positive
/// <i>and</i> at least one audio input + one pull-video endpoint are registered,
/// <c>StartAsync</c> awaits until every audio input has the requested number
/// of frames buffered or the deadline hits. Pure-audio or pure-video graphs
/// skip the wait regardless — there is no AV race to protect against.
/// </summary>
public sealed class AudioPrerollTests
{
    [Fact]
    public async Task StartAsync_WithPrerollSetButNoVideoEndpoint_SkipsWait()
    {
        var opts = new AVRouterOptions
        {
            MinBufferedFramesPerInput = 4096,
            WaitForAudioPreroll       = TimeSpan.FromMilliseconds(500)
        };
        using var router = new AVRouter(opts);
        var channel = new AudioChannel(new AudioFormat(48000, 2));
        router.RegisterAudioInput(channel);

        var start = System.Diagnostics.Stopwatch.StartNew();
        await router.StartAsync();
        start.Stop();

        // Pure-audio graph: no wait even though the ring is empty.
        Assert.True(start.ElapsedMilliseconds < 100,
            $"StartAsync should not wait without a pull-video endpoint, but took {start.ElapsedMilliseconds}ms.");
        await router.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WithPrerollAndPullVideo_WaitsUntilThresholdReached()
    {
        var opts = new AVRouterOptions
        {
            MinBufferedFramesPerInput = 256,
            WaitForAudioPreroll       = TimeSpan.FromSeconds(2)
        };
        using var router = new AVRouter(opts);
        using var pullVideo = new FakePullVideoEndpoint();
        router.RegisterEndpoint(pullVideo);

        var channel = new AudioChannel(new AudioFormat(48000, 2));
        router.RegisterAudioInput(channel);

        // Fill the ring on a background thread shortly after StartAsync begins.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            var frame = new float[256 * 2];
            channel.TryWrite(frame);
        });

        var start = System.Diagnostics.Stopwatch.StartNew();
        await router.StartAsync();
        start.Stop();

        Assert.True(start.ElapsedMilliseconds >= 40,
            $"StartAsync should have blocked until the buffer filled (elapsed {start.ElapsedMilliseconds}ms).");
        Assert.True(start.ElapsedMilliseconds < 1500,
            $"StartAsync should have completed well before the 2s deadline (elapsed {start.ElapsedMilliseconds}ms).");
        await router.StopAsync();
    }

    [Fact]
    public async Task StartAsync_PrerollDeadlineHit_StartsAnyway()
    {
        var opts = new AVRouterOptions
        {
            MinBufferedFramesPerInput = 1_000_000,
            WaitForAudioPreroll       = TimeSpan.FromMilliseconds(200)
        };
        using var router = new AVRouter(opts);
        using var pullVideo = new FakePullVideoEndpoint();
        router.RegisterEndpoint(pullVideo);

        var channel = new AudioChannel(new AudioFormat(48000, 2));
        router.RegisterAudioInput(channel);

        var start = System.Diagnostics.Stopwatch.StartNew();
        await router.StartAsync();
        start.Stop();

        Assert.True(start.ElapsedMilliseconds >= 180,
            $"StartAsync should have waited for the deadline (elapsed {start.ElapsedMilliseconds}ms).");
        Assert.True(start.ElapsedMilliseconds < 600,
            $"StartAsync should have released near the deadline (elapsed {start.ElapsedMilliseconds}ms).");
        Assert.True(router.IsRunning);
        await router.StopAsync();
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakePullVideoEndpoint : IPullVideoEndpoint
    {
        public string Name => "fake-pull-video";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public IVideoPresentCallback? PresentCallback { get; set; }
        public void Dispose() { }
    }
}

