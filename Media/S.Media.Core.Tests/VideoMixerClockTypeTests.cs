using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoMixerClockTypeTests
{
    [Fact]
    public void Constructor_UsesVideoLedByDefault()
    {
        var mixer = new VideoMixer();

        Assert.Equal(ClockType.VideoLed, mixer.ClockType);
    }

    [Fact]
    public void SetClockType_ReturnsInvalid_ForAudioLedOnVideoMixer()
    {
        var mixer = new VideoMixer();

        var result = mixer.SetClockType(ClockType.AudioLed);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}

