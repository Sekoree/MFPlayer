using S.Media.Core.Errors;

namespace S.Media.Core.Mixing;

public static class MixerClockTypeRules
{
    public static int Validate(MixerKind mixerKind, ClockType clockType)
    {
        if (clockType == ClockType.External)
        {
            return MediaResult.Success;
        }

        return (mixerKind, clockType) switch
        {
            (MixerKind.Audio, ClockType.AudioLed) => MediaResult.Success,
            (MixerKind.Video, ClockType.VideoLed) => MediaResult.Success,
            (MixerKind.AudioVideo, ClockType.Hybrid) => MediaResult.Success,
            _ => (int)MediaErrorCode.MixerClockTypeInvalid,
        };
    }
}

