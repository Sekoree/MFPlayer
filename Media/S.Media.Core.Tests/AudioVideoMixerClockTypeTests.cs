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

    [Fact]
    public void SetClockType_ReturnsInvalid_ForUnknownClockType()
    {
        var mixer = new AudioVideoMixer();

        var result = mixer.SetClockType((ClockType)99);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}
