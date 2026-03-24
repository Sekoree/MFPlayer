using S.Media.Core.Errors;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFAudioChannelMapTests
{
    [Fact]
    public void Identity_ReturnsExpectedChannelMap()
    {
        var map = FFAudioChannelMap.Identity(2);

        Assert.Equal(2, map.SourceChannelCount);
        Assert.Equal(2, map.DestinationChannelCount);
        Assert.Equal([0, 1], map.SourceChannelByOutputIndex);
    }

    [Fact]
    public void Validate_ReturnsInvalidAudioChannelMap_WhenIndexOutOfRange()
    {
        var map = new FFAudioChannelMap(2, 2, [0, 2]);

        var code = map.Validate(out var message);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidAudioChannelMap, code);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}

