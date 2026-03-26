namespace S.Media.Core.Mixing;

internal readonly record struct VideoPresenterSyncPolicyOptions(
    TimeSpan StaleFrameDropThreshold,
    TimeSpan FrameEarlyTolerance,
    TimeSpan MinDelay,
    TimeSpan HybridMaxWait,
    TimeSpan StrictMaxWait)
{
    public static VideoPresenterSyncPolicyOptions Default => new(
        StaleFrameDropThreshold: TimeSpan.FromMilliseconds(200),
        FrameEarlyTolerance: TimeSpan.FromMilliseconds(2),
        MinDelay: TimeSpan.FromMilliseconds(1),
        HybridMaxWait: TimeSpan.FromMilliseconds(2),
        StrictMaxWait: TimeSpan.FromMilliseconds(3));
}

