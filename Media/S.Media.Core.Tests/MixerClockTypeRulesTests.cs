using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class MixerClockTypeRulesTests
{
    [Theory]
    [InlineData(ClockType.Hybrid)]
    [InlineData(ClockType.External)]
    public void ValidateClockType_ReturnsSuccess_ForSupportedClockTypes(ClockType clockType)
    {
        var result = MixerClockTypeRules.ValidateClockType(clockType);

        Assert.Equal(MediaResult.Success, result);
    }

    [Theory]
    [InlineData((ClockType)99)]
    public void ValidateClockType_ReturnsMixerClockTypeInvalid_ForUnknownClockType(ClockType clockType)
    {
        var result = MixerClockTypeRules.ValidateClockType(clockType);

        Assert.Equal((int)MediaErrorCode.MixerClockTypeInvalid, result);
    }
}
