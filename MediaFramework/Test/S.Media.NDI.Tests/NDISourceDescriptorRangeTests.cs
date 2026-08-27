using S.Media.Core.Video;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>The `range=` descriptor option (receive-side color-range override).</summary>
public sealed class NDISourceDescriptorRangeTests
{
    [Fact]
    public void NoRangeOption_LeavesTheOverrideUnset()
    {
        var descriptor = NDIDecoderProvider.ParseSourceUri("ndi://CAM-1");

        Assert.Null(descriptor.ColorRangeOverride);
    }

    [Theory]
    [InlineData("full", VideoColorRange.Full)]
    [InlineData("limited", VideoColorRange.Limited)]
    [InlineData("Limited", VideoColorRange.Limited)]
    public void RangeOption_ParsesCaseInsensitively(string text, VideoColorRange expected)
    {
        var descriptor = NDIDecoderProvider.ParseSourceUri($"ndi://CAM-1?range={text}");

        Assert.Equal(expected, descriptor.ColorRangeOverride);
    }

    [Fact]
    public void BogusRange_IsRefusedRatherThanIgnored()
    {
        Assert.Throws<ArgumentException>(() => NDIDecoderProvider.ParseSourceUri("ndi://CAM-1?range=hdr"));
    }
}
