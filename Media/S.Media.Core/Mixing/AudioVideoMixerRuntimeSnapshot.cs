namespace S.Media.Core.Mixing;

/// <summary>
/// Diagnostic snapshot of the audio/video mixer runtime state.
/// Use <see cref="IAudioVideoMixer"/> debug methods to obtain this.
/// </summary>
public readonly record struct AudioVideoMixerDebugInfo(
    long VideoPushed,
    long VideoPushFailures,
    long VideoNoFrame,
    long VideoLateDrops,
    long VideoQueueTrimDrops,
    long VideoCoalescedDrops,
    int VideoQueueDepth,
    long AudioPushFailures,
    long AudioReadFailures,
    long AudioEmptyReads,
    long AudioPushedFrames,
    double DriftMs,
    double CorrectionSignalMs,
    double CorrectionStepMs,
    double CorrectionOffsetMs,
    long CorrectionResyncCount,
    double LeadMinMs,
    double LeadAvgMs,
    double LeadMaxMs);
