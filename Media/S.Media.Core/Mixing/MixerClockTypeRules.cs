using S.Media.Core.Errors;

namespace S.Media.Core.Mixing;

public static class MixerClockTypeRules
{
    public static int ValidateClockType(ClockType clockType)
    {
        return clockType switch
        {
            ClockType.External => MediaResult.Success,
            ClockType.Hybrid => MediaResult.Success,
            _ => (int)MediaErrorCode.MixerClockTypeInvalid,
        };
    }
}
