using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class LocalVideoOutputRoutingPolicyTests
{
    [Fact]
    public void SelectLeaderPixelFormat_PrefersNv12WhenSupported()
    {
        var src = new VideoFormat(1920, 1080, PixelFormat.Nv12, 60, 1);
        var pf = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(src, supportsNv12: true, supportsYuv420p: true);
        Assert.Equal(PixelFormat.Nv12, pf);
    }

    [Fact]
    public void SelectLeaderPixelFormat_PrefersYuv420pWhenSupported()
    {
        var src = new VideoFormat(1920, 1080, PixelFormat.Yuv420p, 60, 1);
        var pf = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(src, supportsNv12: true, supportsYuv420p: true);
        Assert.Equal(PixelFormat.Yuv420p, pf);
    }

    [Fact]
    public void SelectLeaderPixelFormat_FallsBackWhenUnsupported()
    {
        var src = new VideoFormat(1920, 1080, PixelFormat.Uyvy422, 60, 1);
        var pf = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(src, supportsNv12: true, supportsYuv420p: true);
        Assert.Equal(PixelFormat.Bgra32, pf);
    }

    [Fact]
    public void SelectLeaderPixelFormat_Yuv422p10_UsesFallbackWhenUnsupported()
    {
        var src = new VideoFormat(1920, 1080, PixelFormat.Yuv422p10, 60, 1);
        var pf = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(src, supportsNv12: true, supportsYuv420p: true, supportsYuv422p10: false);
        Assert.Equal(PixelFormat.Bgra32, pf);
    }

    [Fact]
    public void SelectLeaderPixelFormat_Yuv422p10_SelectedWhenSupported()
    {
        var src = new VideoFormat(1920, 1080, PixelFormat.Yuv422p10, 60, 1);
        var pf = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(src, supportsNv12: true, supportsYuv420p: true, supportsYuv422p10: true);
        Assert.Equal(PixelFormat.Yuv422p10, pf);
    }
}

