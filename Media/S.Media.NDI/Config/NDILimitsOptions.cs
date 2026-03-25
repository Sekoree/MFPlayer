namespace S.Media.NDI.Config;

public sealed record NDILimitsOptions
{
    public int MaxChildrenPerParent { get; init; } = 4;

    public int MaxPendingAudioFrames { get; init; } = 8;

    public int MaxPendingVideoFrames { get; init; } = 8;

    public NDIQueueOverflowPolicy QueueOverflowPolicy { get; init; } = NDIQueueOverflowPolicy.DropOldest;

    public NDIVideoFallbackMode VideoFallbackMode { get; init; } = NDIVideoFallbackMode.NoFrame;

    public NDILimitsOptions Normalize()
    {
        return this with
        {
            MaxChildrenPerParent = Math.Max(1, MaxChildrenPerParent),
            MaxPendingAudioFrames = Math.Max(1, MaxPendingAudioFrames),
            MaxPendingVideoFrames = Math.Max(1, MaxPendingVideoFrames),
        };
    }
}

