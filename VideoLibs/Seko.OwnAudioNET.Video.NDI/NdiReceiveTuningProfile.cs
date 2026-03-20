namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Preset trade-offs for live NDI receive playback.
/// </summary>
public enum NdiReceiveTuningProfile
{
    /// <summary>Prioritizes smooth playback and jitter tolerance over lowest latency.</summary>
    Stable = 0,

    /// <summary>Balanced compromise between latency and stutter resilience.</summary>
    Balanced = 1,

    /// <summary>Prioritizes lower latency and faster response to drift changes.</summary>
    LowLatency = 2
}

public static class NdiReceiveTuningPresets
{
    public static NdiAudioStreamSourceOptions CreateAudioOptions(NdiReceiveTuningProfile profile)
    {
        return profile switch
        {
            NdiReceiveTuningProfile.Stable => new NdiAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 12,
                CaptureHighWatermarkRatio = 0.60,
                CaptureSleepMilliseconds = 2,
                MinimumCaptureFrames = 96,
                CaptureFrameTargetDivisor = 2
            },
            NdiReceiveTuningProfile.LowLatency => new NdiAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 6,
                CaptureHighWatermarkRatio = 0.30,
                CaptureSleepMilliseconds = 1,
                MinimumCaptureFrames = 48,
                CaptureFrameTargetDivisor = 2
            },
            _ => new NdiAudioStreamSourceOptions
            {
                RingCapacityMultiplier = 8,
                CaptureHighWatermarkRatio = 0.40,
                CaptureSleepMilliseconds = 2,
                MinimumCaptureFrames = 64,
                CaptureFrameTargetDivisor = 2
            }
        };
    }

    public static NdiExternalTimelineClockOptions CreateClockOptions(NdiReceiveTuningProfile profile)
    {
        return profile switch
        {
            NdiReceiveTuningProfile.Stable => new NdiExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.06,
                MinVideoAdvanceFrameRatio = 0.20
            },
            NdiReceiveTuningProfile.LowLatency => new NdiExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.20,
                MinVideoAdvanceFrameRatio = 0.30
            },
            _ => new NdiExternalTimelineClockOptions
            {
                DefaultFrameDurationSeconds = 1.0 / 30.0,
                PipelineLatencySmoothingFactor = 0.10,
                MinVideoAdvanceFrameRatio = 0.25
            }
        };
    }
}

