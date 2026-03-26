using S.Media.NDI.Diagnostics;
using S.Media.Core.Errors;

namespace S.Media.NDI.Config;

public sealed record NDISourceOptions
{
    public NDIQueueOverflowPolicy? QueueOverflowPolicyOverride { get; init; }

    public NDIVideoFallbackMode? VideoFallbackModeOverride { get; init; }

    public TimeSpan? DiagnosticsTickIntervalOverride { get; init; }

    public int? VideoJitterBufferFramesOverride { get; init; }

    public int? AudioJitterBufferMsOverride { get; init; }

    public int Validate()
    {
        if (QueueOverflowPolicyOverride.HasValue && !Enum.IsDefined(QueueOverflowPolicyOverride.Value))
        {
            return (int)MediaErrorCode.NDIInvalidQueueOverflowPolicyOverride;
        }

        if (VideoFallbackModeOverride.HasValue && !Enum.IsDefined(VideoFallbackModeOverride.Value))
        {
            return (int)MediaErrorCode.NDIInvalidVideoFallbackOverride;
        }

        if (DiagnosticsTickIntervalOverride.HasValue && DiagnosticsTickIntervalOverride.Value < TimeSpan.Zero)
        {
            return (int)MediaErrorCode.NDIInvalidDiagnosticsTickOverride;
        }

        if (VideoJitterBufferFramesOverride.HasValue && VideoJitterBufferFramesOverride.Value <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (AudioJitterBufferMsOverride.HasValue && AudioJitterBufferMsOverride.Value <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return MediaResult.Success;
    }

    public NDISourceOptions Normalize()
    {
        var tick = DiagnosticsTickIntervalOverride;
        if (tick.HasValue)
        {
            var value = tick.Value;
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            if (value < TimeSpan.FromMilliseconds(16))
            {
                value = TimeSpan.FromMilliseconds(16);
            }

            tick = value;
        }

        return this with
        {
            DiagnosticsTickIntervalOverride = tick,
            VideoJitterBufferFramesOverride = VideoJitterBufferFramesOverride.HasValue
                ? Math.Max(1, VideoJitterBufferFramesOverride.Value)
                : null,
            AudioJitterBufferMsOverride = AudioJitterBufferMsOverride.HasValue
                ? Math.Max(1, AudioJitterBufferMsOverride.Value)
                : null,
        };
    }

    public NDIQueueOverflowPolicy ResolveQueueOverflowPolicy(NDILimitsOptions limits)
    {
        return QueueOverflowPolicyOverride ?? limits.QueueOverflowPolicy;
    }

    public NDIVideoFallbackMode ResolveVideoFallbackMode(NDILimitsOptions limits)
    {
        return VideoFallbackModeOverride ?? limits.VideoFallbackMode;
    }

    public TimeSpan ResolveDiagnosticsTick(NDIDiagnosticsOptions diagnosticsOptions)
    {
        var baseline = diagnosticsOptions.Normalize().DiagnosticsTickInterval;
        var candidate = DiagnosticsTickIntervalOverride ?? baseline;
        if (candidate < TimeSpan.FromMilliseconds(16))
        {
            return TimeSpan.FromMilliseconds(16);
        }

        return candidate;
    }

    public int ResolveVideoJitterBufferFrames(NDILimitsOptions limits)
    {
        return VideoJitterBufferFramesOverride ?? limits.VideoJitterBufferFrames;
    }

    public int ResolveAudioJitterBufferMs(NDILimitsOptions limits)
    {
        return AudioJitterBufferMsOverride ?? limits.AudioJitterBufferMs;
    }
}

