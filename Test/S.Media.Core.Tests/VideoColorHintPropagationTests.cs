using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// §5.3 — at route creation time, if the source channel implements
/// <see cref="IVideoColorMatrixHint"/> and the endpoint implements
/// <see cref="IVideoColorMatrixReceiver"/>, the router calls
/// <c>ApplyColorMatrixHint</c> once so the endpoint picks a matching shader
/// path without the host application having to pump the hint by hand.
/// </summary>
public sealed class VideoColorHintPropagationTests
{
    [Fact]
    public void CreateRoute_PropagatesHintFromSourceToEndpoint()
    {
        using var router = new AVRouter();
        var channel = new HintingVideoChannel(YuvColorMatrix.Bt709, YuvColorRange.Limited);
        using var ep = new RecordingColorReceiverEndpoint();

        var input = router.RegisterVideoInput(channel);
        var eid   = router.RegisterEndpoint(ep);
        router.CreateRoute(input, eid);

        Assert.Single(ep.Calls);
        Assert.Equal((YuvColorMatrix.Bt709, YuvColorRange.Limited), ep.Calls[0]);
    }

    [Fact]
    public void CreateRoute_AutoHint_StillForwarded_ReceiverMayIgnore()
    {
        using var router = new AVRouter();
        var channel = new HintingVideoChannel(YuvColorMatrix.Auto, YuvColorRange.Auto);
        using var ep = new RecordingColorReceiverEndpoint();

        var input = router.RegisterVideoInput(channel);
        var eid   = router.RegisterEndpoint(ep);
        router.CreateRoute(input, eid);

        // Router delivers whatever the hint says — Auto/Auto is a valid signal
        // that the receiver should keep its own defaults. We record the call
        // here only to prove the wiring fires.
        Assert.Single(ep.Calls);
        Assert.Equal((YuvColorMatrix.Auto, YuvColorRange.Auto), ep.Calls[0]);
    }

    [Fact]
    public void CreateRoute_NoReceiverOnEndpoint_NoCallsMade()
    {
        using var router = new AVRouter();
        var channel = new HintingVideoChannel(YuvColorMatrix.Bt2020, YuvColorRange.Full);
        using var ep = new NonReceivingVideoEndpoint();

        var input = router.RegisterVideoInput(channel);
        var eid   = router.RegisterEndpoint(ep);
        router.CreateRoute(input, eid);

        // No assertions fail — endpoint has no ApplyColorMatrixHint, so the
        // router's try/catch path is never entered. Presence of this test
        // guards against a regression where the feature code does a blind cast.
    }

    [Fact]
    public void CreateRoute_ReceiverThrows_RouteStillCreated()
    {
        using var router = new AVRouter();
        var channel = new HintingVideoChannel(YuvColorMatrix.Bt601, YuvColorRange.Limited);
        using var ep = new ThrowingColorReceiverEndpoint();

        var input = router.RegisterVideoInput(channel);
        var eid   = router.RegisterEndpoint(ep);
        var routeId = router.CreateRoute(input, eid);

        Assert.NotEqual(default, routeId);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class HintingVideoChannel : IVideoChannel, IVideoColorMatrixHint
    {
        public HintingVideoChannel(YuvColorMatrix m, YuvColorRange r)
        {
            SuggestedYuvColorMatrix = m;
            SuggestedYuvColorRange  = r;
        }
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public void Seek(TimeSpan position) { }
        public VideoFormat SourceFormat { get; } = new(1920, 1080, PixelFormat.Yuv420p, 30, 1);
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }
        public YuvColorMatrix SuggestedYuvColorMatrix { get; }
        public YuvColorRange  SuggestedYuvColorRange  { get; }
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Dispose() { }
    }

    private sealed class RecordingColorReceiverEndpoint : IVideoEndpoint, IVideoColorMatrixReceiver
    {
        public List<(YuvColorMatrix m, YuvColorRange r)> Calls { get; } = new();
        public string Name => "record-color-recv";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public void ApplyColorMatrixHint(YuvColorMatrix matrix, YuvColorRange range) => Calls.Add((matrix, range));
        public void Dispose() { }
    }

    private sealed class NonReceivingVideoEndpoint : IVideoEndpoint
    {
        public string Name => "no-color-recv";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public void Dispose() { }
    }

    private sealed class ThrowingColorReceiverEndpoint : IVideoEndpoint, IVideoColorMatrixReceiver
    {
        public string Name => "throw-color-recv";
        public bool IsRunning => false;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public void ApplyColorMatrixHint(YuvColorMatrix matrix, YuvColorRange range)
            => throw new InvalidOperationException("boom");
        public void Dispose() { }
    }
}

