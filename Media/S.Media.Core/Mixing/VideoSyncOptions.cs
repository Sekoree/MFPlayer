namespace S.Media.Core.Mixing;

/// <summary>
/// Tunes the video sync policy used by <see cref="AVMixer"/>.
/// Pass an instance via <see cref="AVMixerConfig.PresenterSyncOptions"/> to override the built-in defaults.
/// </summary>
public readonly record struct VideoSyncOptions(
    TimeSpan StaleFrameDropThreshold,
    TimeSpan FrameEarlyTolerance,
    TimeSpan MinDelay,
    TimeSpan MaxWait)
{
    public static VideoSyncOptions Default => new(
        StaleFrameDropThreshold: TimeSpan.FromMilliseconds(200),
        FrameEarlyTolerance: TimeSpan.FromMilliseconds(2),
        MinDelay: TimeSpan.FromMilliseconds(1),
        MaxWait: TimeSpan.FromMilliseconds(2));
}
