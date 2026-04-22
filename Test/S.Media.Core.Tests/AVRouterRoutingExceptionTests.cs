using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Exercises <see cref="AVRouter"/>'s public API-boundary exceptions (review
/// items §3.21 / EL1). These pin the contract so future regressions that
/// revert to <see cref="InvalidOperationException"/> break the build.
/// </summary>
public sealed class AVRouterRoutingExceptionTests
{
    [Fact]
    public void CreateRoute_UnknownInput_ThrowsMediaRoutingException()
    {
        using var router = new AVRouter();
        using var ep     = new VirtualClockEndpoint();
        var epId = router.RegisterEndpoint(ep);

        var bogus = InputId.New();
        var ex = Assert.Throws<MediaRoutingException>(() => router.CreateRoute(bogus, epId));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void CreateRoute_UnknownEndpoint_ThrowsMediaRoutingException()
    {
        using var router  = new AVRouter();
        using var channel = new AudioChannel(new AudioFormat(48000, 2));
        var inputId = router.RegisterAudioInput(channel);

        var bogus = EndpointId.New();
        var ex = Assert.Throws<MediaRoutingException>(() => router.CreateRoute(inputId, bogus));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void CreateRoute_VideoOptionsOnAudioInput_ThrowsMediaRoutingException()
    {
        using var router  = new AVRouter();
        using var channel = new AudioChannel(new AudioFormat(48000, 2));
        using var ep      = new VirtualClockEndpoint();
        var inputId = router.RegisterAudioInput(channel);
        var epId    = router.RegisterEndpoint(ep);

        Assert.Throws<MediaRoutingException>(
            () => router.CreateRoute(inputId, epId, new VideoRouteOptions()));
    }

    [Fact]
    public void SetRouteEnabled_UnknownRoute_ThrowsMediaRoutingException()
    {
        using var router = new AVRouter();
        Assert.Throws<MediaRoutingException>(() => router.SetRouteEnabled(RouteId.New(), true));
    }

    [Fact]
    public void RemoveRoute_UnknownRoute_IsSilent()
    {
        // RemoveRoute is intentionally idempotent — documents the contract as of
        // today's behaviour; flip this test if/when we decide to throw.
        using var router = new AVRouter();
        var ex = Record.Exception(() => router.RemoveRoute(RouteId.New()));
        Assert.Null(ex);
    }
}

