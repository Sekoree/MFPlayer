using S.Media.Core.Errors;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Smoke tests for the Tier 2 exception hierarchy (§4.6).
/// These just confirm the inheritance and constructor shape so call sites can
/// rely on <c>catch (MediaException)</c> for broad handling and individual
/// derived types for precise handling.
/// </summary>
public sealed class ExceptionHierarchyTests
{
    [Fact]
    public void AllConcreteTypes_InheritFromMediaException()
    {
        Assert.IsAssignableFrom<MediaException>(new MediaOpenException("x"));
        Assert.IsAssignableFrom<MediaException>(new MediaDecodeException("x"));
        Assert.IsAssignableFrom<MediaException>(new MediaRoutingException("x"));
        Assert.IsAssignableFrom<MediaException>(new MediaDeviceException("x"));
        Assert.IsAssignableFrom<MediaException>(new ClockException("x"));
    }

    [Fact]
    public void MediaOpenException_CapturesResourcePath()
    {
        var ex = new MediaOpenException("bad", "file:///tmp/x.mp3");
        Assert.Equal("file:///tmp/x.mp3", ex.ResourcePath);
    }

    [Fact]
    public void MediaDecodeException_CapturesPosition()
    {
        var ex = new MediaDecodeException("boom", TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), ex.Position);
    }

    [Fact]
    public void MediaDeviceException_CapturesDeviceName()
    {
        var ex = new MediaDeviceException("closed", "Speakers (USB)");
        Assert.Equal("Speakers (USB)", ex.DeviceName);
    }

    [Fact]
    public void InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("root");
        var outer = new MediaRoutingException("routing", inner);
        Assert.Same(inner, outer.InnerException);
    }
}

