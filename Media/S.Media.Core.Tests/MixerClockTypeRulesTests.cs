using S.Media.Core.Clock;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Validates that <see cref="AVMixer.SetClockType"/> accepts supported clock types
/// and rejects unknown ones. (Previously tested via the removed MixerClockTypeRules class.)
/// </summary>
public sealed class MixerClockTypeRulesTests
{
    [Theory]
    [InlineData(ClockType.Hybrid)]
    [InlineData(ClockType.External)]
    public void SetClockType_ReturnsSuccess_ForSupportedClockTypes(ClockType clockType)
    {
        using var mixer = new AVMixer();

        var result = mixer.SetClockType(clockType);

        Assert.Equal(MediaResult.Success, result);
    }

    [Theory]
    [InlineData((ClockType)99)]
    public void SetClockType_ReturnsMixerClockTypeInvalid_ForUnknownClockType(ClockType clockType)
    {
        using var mixer = new AVMixer();

        var result = mixer.SetClockType(clockType);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}
