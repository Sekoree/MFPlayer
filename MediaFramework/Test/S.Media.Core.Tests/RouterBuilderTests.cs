using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Covers §5.10 — <see cref="RouterBuilder"/> composes the router atomically:
/// registrations apply in <see cref="RouterBuilder.Build"/>; a failure in any
/// step disposes the half-wired router and rethrows.
/// </summary>
public sealed class RouterBuilderTests
{
    [Fact]
    public void Build_EmptyBuilder_ReturnsFreshRouter()
    {
        using var router = new RouterBuilder().Build();
        Assert.NotNull(router);
        Assert.False(router.IsRunning);
    }

    [Fact]
    public void Build_ReturnsRouterWithWiredInputsAndEndpoints()
    {
        using var channel = new FakeAudioChannel(44100, 2);
        using var endpoint = new FakeAudioEndpoint();

        using var router = new RouterBuilder()
            .AddAudioInput(channel, out var inputToken)
            .AddEndpoint(endpoint, out var endpointToken)
            .AddRoute(inputToken, endpointToken)
            .Build();

        var diag = router.GetDiagnosticsSnapshot();
        Assert.Single(diag.Inputs);
        Assert.Single(diag.Endpoints);
        Assert.Single(diag.Routes);
    }

    [Fact]
    public void Build_Options_ArePassedThrough()
    {
        var opts = new AVRouterOptions { InternalTickCadence = TimeSpan.FromMilliseconds(3) };
        using var router = new RouterBuilder()
            .WithOptions(opts)
            .Build();

        Assert.Equal(TimeSpan.FromMilliseconds(3), router.EffectiveTickCadence);
    }

    [Fact]
    public void Build_PartialFailure_DisposesRouter()
    {
        using var channel = new FakeAudioChannel(44100, 2);

        var builder = new RouterBuilder()
            .AddAudioInput(channel, out var inputToken);

        // Reference a non-existent endpoint token so Build's step for AddRoute
        // throws KeyNotFoundException partway through construction.
        builder.AddRoute(inputToken, endpointToken: 999);

        Assert.ThrowsAny<Exception>(() => builder.Build());
        // Router was disposed inside Build — no public way to observe that
        // directly beyond "no resource leak", but the test asserts the
        // exception propagation contract.
    }

    [Fact]
    public void Build_WithExplicitAudioRouteOptions_CreatesRoute()
    {
        using var channel = new FakeAudioChannel(44100, 2);
        using var endpoint = new FakeAudioEndpoint();

        using var router = new RouterBuilder()
            .AddAudioInput(channel, out var i)
            .AddEndpoint(endpoint, out var e)
            .AddRoute(i, e, new AudioRouteOptions { Gain = 0.5f })
            .Build();

        var diag = router.GetDiagnosticsSnapshot();
        Assert.Single(diag.Routes);
        Assert.Equal(0.5f, diag.Routes[0].Gain);
    }

    // ── Minimal stubs ───────────────────────────────────────────────────────

    private sealed class FakeAudioChannel : IAudioChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AudioFormat SourceFormat { get; }
        public bool IsOpen => true;
        public bool CanSeek => false;
        public int BufferDepth => 8;
        public int BufferAvailable => 0;
        public TimeSpan Position => TimeSpan.Zero;
        public float Volume { get; set; } = 1.0f;

        public event EventHandler? EndOfStream
        {
            add { } remove { }
        }
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { } remove { }
        }

        public FakeAudioChannel(int rate, int ch) => SourceFormat = new AudioFormat(rate, ch);

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest.Clear();
            return 0;
        }

        public void Seek(TimeSpan position) { }

        public void Dispose() { }
    }

    private sealed class FakeAudioEndpoint : IAudioEndpoint
    {
        public string Name => "FakeAudioEndpoint";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts) { }
        public void Dispose() { }
    }
}
