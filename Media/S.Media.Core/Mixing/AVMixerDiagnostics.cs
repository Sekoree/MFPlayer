namespace S.Media.Core.Mixing;

/// <summary>
/// Diagnostic snapshot of the audio/video mixer runtime state.
/// Obtain via <see cref="IAVMixer.GetDebugInfo"/>.
/// </summary>
public readonly record struct AVMixerDiagnostics(
    long VideoPushed,
    long VideoPushFailures,
    long VideoNoFrame,
    long VideoLateDrops,
    long VideoQueueTrimDrops,
    long VideoCoalescedDrops,
    int  VideoQueueDepth,
    long AudioPushFailures,
    long AudioReadFailures,
    long AudioEmptyReads,
    long AudioPushedFrames,
    long VideoWorkerEnqueueDrops,
    long VideoWorkerStaleDrops,
    long VideoWorkerPushFailures,
    int  VideoWorkerQueueDepth,
    int  VideoWorkerMaxQueueDepth);
