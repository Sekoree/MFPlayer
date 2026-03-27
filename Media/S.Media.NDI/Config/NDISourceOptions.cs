using S.Media.NDI.Diagnostics;
using S.Media.Core.Errors;

namespace S.Media.NDI.Config;

public sealed record NDISourceOptions
{
    public NDIQueueOverflowPolicy QueueOverflowPolicy { get; init; } = NDIQueueOverflowPolicy.DropOldest;

    public NDIVideoFallbackMode VideoFallbackMode { get; init; } = NDIVideoFallbackMode.NoFrame;

    public TimeSpan DiagnosticsTickInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int VideoJitterBufferFrames { get; init; } = 4;

    public int AudioJitterBufferMs { get; init; } = 90;

    public int Validate()
    {
        if (!Enum.IsDefined(QueueOverflowPolicy))
        {
            return (int)MediaErrorCode.NDIInvalidQueueOverflowPolicyOverride;
        }

        if (!Enum.IsDefined(VideoFallbackMode))
        {
            return (int)MediaErrorCode.NDIInvalidVideoFallbackOverride;
        }

        if (DiagnosticsTickInterval < TimeSpan.Zero)
        {
            return (int)MediaErrorCode.NDIInvalidDiagnosticsTickOverride;
        }

        if (VideoJitterBufferFrames <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (AudioJitterBufferMs <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return MediaResult.Success;
    }

    public NDISourceOptions Normalize()
    {
        var tick = DiagnosticsTickInterval;
        if (tick < TimeSpan.Zero)
        {
            tick = TimeSpan.Zero;
        }

        if (tick < TimeSpan.FromMilliseconds(16))
        {
            tick = TimeSpan.FromMilliseconds(16);
        }

        return this with
        {
            DiagnosticsTickInterval = tick,
            VideoJitterBufferFrames = Math.Max(1, VideoJitterBufferFrames),
            AudioJitterBufferMs = Math.Max(1, AudioJitterBufferMs),
        };
    }
}
