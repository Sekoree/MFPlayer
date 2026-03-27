using S.Media.Core.Errors;

namespace S.Media.Core.Audio;

public static class AudioRouteMapValidator
{
    public static int ValidatePushFrameMap(in AudioFrame frame, ReadOnlySpan<int> sourceChannelByOutputIndex, int sourceChannelCount)
    {
        if (sourceChannelByOutputIndex.IsEmpty)
        {
            return (int)MediaErrorCode.AudioRouteMapMissing;
        }

        if (sourceChannelCount <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (frame.SourceChannelCount > 0 && sourceChannelCount != frame.SourceChannelCount)
        {
            return (int)MediaErrorCode.AudioChannelCountMismatch;
        }

        for (var i = 0; i < sourceChannelByOutputIndex.Length; i++)
        {
            var sourceIndex = sourceChannelByOutputIndex[i];
            if (sourceIndex < -1 || sourceIndex >= sourceChannelCount)
            {
                return (int)MediaErrorCode.AudioRouteMapInvalid;
            }
        }

        return MediaResult.Success;
    }
}
