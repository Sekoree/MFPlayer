using S.Media.Core.Audio.Routing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for <see cref="ChannelRouteMap"/>: static factories, fluent builder, and
/// <see cref="ChannelRouteMap.BakeRoutes"/>.
/// </summary>
public sealed class ChannelRouteMapTests
{
    // ── Identity ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void Identity_CreatesOneRoutePerChannel(int channelCount)
    {
        var map = ChannelRouteMap.Identity(channelCount);

        Assert.Equal(channelCount, map.Routes.Count);
        for (int i = 0; i < channelCount; i++)
        {
            Assert.Equal(i, map.Routes[i].SrcChannel);
            Assert.Equal(i, map.Routes[i].DstChannel);
            Assert.Equal(1.0f, map.Routes[i].Gain);
        }
    }

    [Fact]
    public void Identity_BakeRoutes_EachSrcMapsToSameDst()
    {
        var map   = ChannelRouteMap.Identity(2);
        var baked = map.BakeRoutes(2);

        Assert.Equal(2, baked.Length);
        Assert.Single(baked[0]);
        Assert.Equal(0, baked[0][0].dstCh);
        Assert.Equal(1.0f, baked[0][0].gain);
        Assert.Single(baked[1]);
        Assert.Equal(1, baked[1][0].dstCh);
    }

    // ── StereoFanTo ───────────────────────────────────────────────────────

    [Fact]
    public void StereoFanTo_Creates4Routes()
    {
        var map = ChannelRouteMap.StereoFanTo(0, 2, 1, 3);

        Assert.Equal(4, map.Routes.Count);
    }

    [Fact]
    public void StereoFanTo_LeftChannelFansToTwoDsts()
    {
        var map   = ChannelRouteMap.StereoFanTo(0, 2, 1, 3);
        var baked = map.BakeRoutes(2);

        // src[0] (L) → dst 0 and dst 2
        var leftRoutes = baked[0].OrderBy(r => r.dstCh).ToArray();
        Assert.Equal(2, leftRoutes.Length);
        Assert.Equal(0, leftRoutes[0].dstCh);
        Assert.Equal(2, leftRoutes[1].dstCh);
    }

    [Fact]
    public void StereoFanTo_RightChannelFansToTwoDsts()
    {
        var map   = ChannelRouteMap.StereoFanTo(0, 2, 1, 3);
        var baked = map.BakeRoutes(2);

        // src[1] (R) → dst 1 and dst 3
        var rightRoutes = baked[1].OrderBy(r => r.dstCh).ToArray();
        Assert.Equal(2, rightRoutes.Length);
        Assert.Equal(1, rightRoutes[0].dstCh);
        Assert.Equal(3, rightRoutes[1].dstCh);
    }

    // ── StereoExpandTo ────────────────────────────────────────────────────

    [Fact]
    public void StereoExpandTo_Creates4Routes()
    {
        var map = ChannelRouteMap.StereoExpandTo(0);
        Assert.Equal(4, map.Routes.Count);
    }

    [Fact]
    public void StereoExpandTo_LeftMapsToBase0And1()
    {
        var map   = ChannelRouteMap.StereoExpandTo(0);
        var baked = map.BakeRoutes(2);

        var leftDsts = baked[0].Select(r => r.dstCh).OrderBy(x => x).ToArray();
        Assert.Equal([0, 1], leftDsts);
    }

    [Fact]
    public void StereoExpandTo_RightMapsToBase2And3()
    {
        var map   = ChannelRouteMap.StereoExpandTo(0);
        var baked = map.BakeRoutes(2);

        var rightDsts = baked[1].Select(r => r.dstCh).OrderBy(x => x).ToArray();
        Assert.Equal([2, 3], rightDsts);
    }

    [Fact]
    public void StereoExpandTo_BaseChannel2_LeftMapsTo2And3()
    {
        var map   = ChannelRouteMap.StereoExpandTo(2);
        var baked = map.BakeRoutes(2);

        var leftDsts = baked[0].Select(r => r.dstCh).OrderBy(x => x).ToArray();
        Assert.Equal([2, 3], leftDsts);
    }

    // ── DownmixToMono ────────────────────────────────────────────────────

    [Fact]
    public void DownmixToMono_CreatesOneRoutePerSrcChannel()
    {
        var map = ChannelRouteMap.DownmixToMono(4, dstChannel: 0);
        Assert.Equal(4, map.Routes.Count);
        Assert.All(map.Routes, r => Assert.Equal(0, r.DstChannel));
    }

    [Fact]
    public void DownmixToMono_GainAppliedToAllRoutes()
    {
        var map = ChannelRouteMap.DownmixToMono(2, dstChannel: 0, gainPerChannel: 0.5f);
        Assert.All(map.Routes, r => Assert.Equal(0.5f, r.Gain));
    }

    // ── Builder ───────────────────────────────────────────────────────────

    [Fact]
    public void Builder_AddsRoutesInOrder()
    {
        var map = new ChannelRouteMap.Builder()
            .Route(0, 0, 1.0f)
            .Route(1, 1, 0.8f)
            .Route(0, 2, 0.5f)
            .Build();

        Assert.Equal(3, map.Routes.Count);
        Assert.Equal(0,    map.Routes[0].SrcChannel);
        Assert.Equal(0,    map.Routes[0].DstChannel);
        Assert.Equal(1.0f, map.Routes[0].Gain);
        Assert.Equal(1,    map.Routes[1].SrcChannel);
        Assert.Equal(0.8f, map.Routes[1].Gain);
    }

    [Fact]
    public void Builder_DefaultGainIsOne()
    {
        var map = new ChannelRouteMap.Builder().Route(0, 0).Build();
        Assert.Equal(1.0f, map.Routes[0].Gain);
    }

    // ── BakeRoutes ────────────────────────────────────────────────────────

    [Fact]
    public void BakeRoutes_IgnoresOutOfRangeSrcChannel()
    {
        // Route references src=5 but srcChannels=2
        var map = new ChannelRouteMap.Builder()
            .Route(0, 0)
            .Route(5, 1) // out of range
            .Build();

        var baked = map.BakeRoutes(2);

        Assert.Single(baked[0]);           // src=0 has one route
        Assert.Empty(baked[1]);            // src=1 has no routes (route with src=5 is ignored)
    }

    [Fact]
    public void BakeRoutes_FanIn_MultipleSrcsToSameDst()
    {
        var map = new ChannelRouteMap.Builder()
            .Route(0, 0)
            .Route(1, 0) // both sources → same destination
            .Build();

        var baked = map.BakeRoutes(2);

        Assert.Single(baked[0]);
        Assert.Single(baked[1]);
        Assert.Equal(0, baked[0][0].dstCh);
        Assert.Equal(0, baked[1][0].dstCh);
    }

    [Fact]
    public void BakeRoutes_FanOut_OneSrcToMultipleDsts()
    {
        var map = new ChannelRouteMap.Builder()
            .Route(0, 0)
            .Route(0, 1)
            .Route(0, 2)
            .Build();

        var baked = map.BakeRoutes(1);

        Assert.Equal(3, baked[0].Length);
    }
}

