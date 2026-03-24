using S.Media.Core.Errors;

namespace S.Media.FFmpeg.Audio;

public readonly record struct FFAudioChannelMap
{
    public FFAudioChannelMap(int sourceChannelCount, int destinationChannelCount, IReadOnlyList<int> sourceChannelByOutputIndex)
    {
        SourceChannelCount = sourceChannelCount;
        DestinationChannelCount = destinationChannelCount;
        SourceChannelByOutputIndex = sourceChannelByOutputIndex;
    }

    public int SourceChannelCount { get; }

    public int DestinationChannelCount { get; }

    public IReadOnlyList<int> SourceChannelByOutputIndex { get; }

    public int Validate(out string? validationError)
    {
        if (SourceChannelCount <= 0)
        {
            validationError = "SourceChannelCount must be positive.";
            return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
        }

        if (DestinationChannelCount <= 0)
        {
            validationError = "DestinationChannelCount must be positive.";
            return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
        }

        if (SourceChannelByOutputIndex is null)
        {
            validationError = "SourceChannelByOutputIndex cannot be null.";
            return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
        }

        if (SourceChannelByOutputIndex.Count != DestinationChannelCount)
        {
            validationError = "Map length must match DestinationChannelCount.";
            return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
        }

        foreach (var sourceIndex in SourceChannelByOutputIndex)
        {
            if (sourceIndex < -1 || sourceIndex >= SourceChannelCount)
            {
                validationError = "Map contains out-of-range source index.";
                return (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
            }
        }

        validationError = null;
        return MediaResult.Success;
    }

    public static FFAudioChannelMap Identity(int channelCount)
    {
        var map = Enumerable.Range(0, channelCount).ToArray();
        return new FFAudioChannelMap(channelCount, channelCount, map);
    }
}

