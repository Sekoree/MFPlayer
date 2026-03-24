using S.Media.Core.Errors;

namespace S.Media.Core.Video;

public sealed record VideoOutputConfig
{
    public VideoOutputBackpressureMode BackpressureMode { get; init; } = VideoOutputBackpressureMode.DropOldest;

    public int QueueCapacity { get; init; } = 2;

    public double BackpressureWaitFrameMultiplier { get; init; } = 1.0;

    public TimeSpan? BackpressureTimeout { get; init; }

    public int Validate(bool hasEffectiveFrameDuration)
    {
        if (QueueCapacity < 1)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (BackpressureWaitFrameMultiplier <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (BackpressureMode == VideoOutputBackpressureMode.Wait &&
            !BackpressureTimeout.HasValue &&
            !hasEffectiveFrameDuration)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return MediaResult.Success;
    }
}

