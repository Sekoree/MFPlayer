namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Preset trade-offs for live NDI receive playback.
/// </summary>
public enum NDIReceiveTuningProfile
{
    /// <summary>Prioritizes smooth playback and jitter tolerance over lowest latency.</summary>
    Stable = 0,

    /// <summary>Balanced compromise between latency and stutter resilience.</summary>
    Balanced = 1,

    /// <summary>Prioritizes lower latency and faster response to drift changes.</summary>
    LowLatency = 2
}

public static class NDIReceiveTuningPresets
{
    public static NDIAudioStreamSourceOptions CreateAudioOptions(NDIReceiveTuningProfile profile)
    {
        return profile switch
        {
            NDIReceiveTuningProfile.Stable => new NDIAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 12,
                CaptureHighWatermarkRatio = 0.60,
                CaptureSleepMilliseconds = 2,
                MinimumCaptureFrames = 96,
                CaptureFrameTargetDivisor = 2
            },
            NDIReceiveTuningProfile.LowLatency => new NDIAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 6,
                CaptureHighWatermarkRatio = 0.30,
                CaptureSleepMilliseconds = 1,
                MinimumCaptureFrames = 48,
                CaptureFrameTargetDivisor = 2
            },
            _ => new NDIAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 8,
                CaptureHighWatermarkRatio = 0.40,
                CaptureSleepMilliseconds = 2,
                MinimumCaptureFrames = 64,
                CaptureFrameTargetDivisor = 2
            }
        };
    }

    public static NDIExternalTimelineClockOptions CreateClockOptions(NDIReceiveTuningProfile profile)
    {
        return profile switch
        {
            NDIReceiveTuningProfile.Stable => new NDIExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.06,
                MinVideoAdvanceFrameRatio = 0.20,
                MaxLatencyCompensationSeconds = 0.40,
                MaxTimestampJumpSeconds = 0.80
            },
            NDIReceiveTuningProfile.LowLatency => new NDIExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.20,
                MinVideoAdvanceFrameRatio = 0.30,
                MaxLatencyCompensationSeconds = 0.12,
                MaxTimestampJumpSeconds = 0.30
            },
            _ => new NDIExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.10,
                MinVideoAdvanceFrameRatio = 0.25,
                MaxLatencyCompensationSeconds = 0.25,
                MaxTimestampJumpSeconds = 0.50
            }
        };
    }
}

