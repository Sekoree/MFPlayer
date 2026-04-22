namespace S.Media.Core.Video;

/// <summary>
/// Slow-consumer / full-queue policy for an <see cref="IVideoSubscription"/>.
/// </summary>
public enum VideoOverflowPolicy
{
    /// <summary>Publisher blocks until space is available. Correct for pace-setters (pull endpoints).</summary>
    Wait,

    /// <summary>On publish-to-full, evict the oldest queued frame (release it) and insert the new one. Correct for push endpoints where stale content is useless.</summary>
    DropOldest,

    /// <summary>On publish-to-full, drop the new frame (release it) and keep what's queued.</summary>
    DropNewest,
}

/// <summary>
/// Configuration for a single <see cref="IVideoSubscription"/>.
/// </summary>
public sealed record VideoSubscriptionOptions(
    int Capacity = 4,
    VideoOverflowPolicy OverflowPolicy = VideoOverflowPolicy.DropOldest,
    string? DebugName = null);

