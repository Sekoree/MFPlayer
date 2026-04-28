namespace S.Media.Core.Video;

/// <summary>
/// Slow-consumer / full-queue policy for an <see cref="IVideoSubscription"/>.
/// </summary>
public enum VideoOverflowPolicy
{
    /// <summary>
    /// Publisher blocks until space is available. Correct for vsync-paced
    /// pull endpoints when the consumer is reliably faster than realtime —
    /// guarantees no frames are skipped. <b>Risk:</b> a slow consumer (or
    /// below-realtime decode) propagates back to the demuxer and can prevent
    /// EOF detection. See <see cref="DropOldestUnderStall"/> for a safer
    /// pull-endpoint default.
    /// </summary>
    Wait,

    /// <summary>On publish-to-full, evict the oldest queued frame (release it) and insert the new one. Correct for push endpoints where stale content is useless.</summary>
    DropOldest,

    /// <summary>On publish-to-full, drop the new frame (release it) and keep what's queued.</summary>
    DropNewest,

    /// <summary>
    /// §heavy-media-fixes phase 4 — hybrid of <see cref="Wait"/> and
    /// <see cref="DropOldest"/>. The publisher first tries to wait up to
    /// <see cref="VideoSubscriptionOptions.StallTimeoutMs"/> for a slot;
    /// only if that times out does it evict the oldest frame and retry.
    /// Tolerates short jitter without dropping (preserving completeness)
    /// while preventing sustained backpressure from blocking the decode /
    /// demux chain (preserving liveness, EOF detection and PTS-clock
    /// freshness). Recommended default for pull video endpoints.
    /// </summary>
    DropOldestUnderStall,
}

/// <summary>
/// Configuration for a single <see cref="IVideoSubscription"/>.
/// </summary>
public sealed record VideoSubscriptionOptions(
    int Capacity = 4,
    VideoOverflowPolicy OverflowPolicy = VideoOverflowPolicy.DropOldest,
    string? DebugName = null)
{
    /// <summary>
    /// Maximum time (ms) the publisher waits on a full queue before falling
    /// back to "drop oldest" under <see cref="VideoOverflowPolicy.DropOldestUnderStall"/>.
    /// Ignored for the other policies. Defaults to 250 ms.
    /// </summary>
    public int StallTimeoutMs { get; init; } = 250;
}

