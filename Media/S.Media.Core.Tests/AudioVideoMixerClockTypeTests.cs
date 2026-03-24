using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioVideoMixerClockTypeTests
{
    [Fact]
    public void Constructor_UsesHybridByDefault()
    {
        var mixer = new AudioVideoMixer();

        Assert.Equal(ClockType.Hybrid, mixer.ClockType);
    }

    [Theory]
    [InlineData(ClockType.AudioLed)]
    [InlineData(ClockType.VideoLed)]
    public void SetClockType_ReturnsInvalid_ForSingleDomainTypesOnAudioVideoMixer(ClockType clockType)
    {
        var mixer = new AudioVideoMixer();

        var result = mixer.SetClockType(clockType);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}

