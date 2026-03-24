using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class MixerClockTypeRulesTests
{
    [Theory]
    [InlineData(MixerKind.Audio, ClockType.AudioLed)]
    [InlineData(MixerKind.Video, ClockType.VideoLed)]
    [InlineData(MixerKind.AudioVideo, ClockType.Hybrid)]
    [InlineData(MixerKind.Audio, ClockType.External)]
    [InlineData(MixerKind.Video, ClockType.External)]
    [InlineData(MixerKind.AudioVideo, ClockType.External)]
    public void Validate_ReturnsSuccess_ForSupportedClockTypes(MixerKind mixerKind, ClockType clockType)
    {
        var result = MixerClockTypeRules.Validate(mixerKind, clockType);

        Assert.Equal(MediaResult.Success, result);
    }

    [Theory]
    [InlineData(MixerKind.Audio, ClockType.VideoLed)]
    [InlineData(MixerKind.Audio, ClockType.Hybrid)]
    [InlineData(MixerKind.Video, ClockType.AudioLed)]
    [InlineData(MixerKind.Video, ClockType.Hybrid)]
    [InlineData(MixerKind.AudioVideo, ClockType.AudioLed)]
    [InlineData(MixerKind.AudioVideo, ClockType.VideoLed)]
    public void Validate_ReturnsMixerClockTypeInvalid_ForNonsensicalConfigs(MixerKind mixerKind, ClockType clockType)
    {
        var result = MixerClockTypeRules.Validate(mixerKind, clockType);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}

