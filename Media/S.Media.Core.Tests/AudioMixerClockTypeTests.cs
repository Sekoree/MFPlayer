using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioMixerClockTypeTests
{
    [Fact]
    public void Constructor_UsesAudioLedByDefault()
    {
        var mixer = new AudioMixer();

        Assert.Equal(ClockType.AudioLed, mixer.ClockType);
    }

    [Fact]
    public void SetClockType_ReturnsInvalid_ForVideoLedOnAudioMixer()
    {
        var mixer = new AudioMixer();

        var result = mixer.SetClockType(ClockType.VideoLed);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}

