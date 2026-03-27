namespace S.Media.Core.Mixing;

internal readonly record struct VideoPresenterSyncPolicyOptions(
    TimeSpan StaleFrameDropThreshold,
    TimeSpan FrameEarlyTolerance,
    TimeSpan MinDelay,
    TimeSpan MaxWait)
{
    public static VideoPresenterSyncPolicyOptions Default => new(
        StaleFrameDropThreshold: TimeSpan.FromMilliseconds(200),
        FrameEarlyTolerance: TimeSpan.FromMilliseconds(2),
        MinDelay: TimeSpan.FromMilliseconds(1),
        MaxWait: TimeSpan.FromMilliseconds(2));
}
