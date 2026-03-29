namespace S.Media.NDI.Config;

/// <summary>
/// Engine-wide default limits for NDI sources and outputs.
/// Individual sources can override <see cref="VideoJitterBufferFrames"/>,
/// <see cref="AudioJitterBufferMs"/>, <see cref="QueueOverflowPolicy"/>, and
/// <see cref="VideoFallbackMode"/> via per-source <see cref="NDISourceOptions"/>.
/// </summary>
public sealed record NDILimitsOptions
{
    public int MaxChildrenPerParent { get; init; } = 4;

    public int MaxPendingAudioFrames { get; init; } = 8;

    public int MaxPendingVideoFrames { get; init; } = 8;

    /// <summary>
    /// Engine-wide default: video jitter buffer depth in frames.
    /// Overridden per-source by <see cref="NDISourceOptions.VideoJitterBufferFrames"/>.
    /// </summary>
    public int VideoJitterBufferFrames { get; init; } = 3;

    /// <summary>
    /// Engine-wide default: audio jitter buffer depth in milliseconds.
    /// Overridden per-source by <see cref="NDISourceOptions.AudioJitterBufferMs"/>.
    /// </summary>
    public int AudioJitterBufferMs { get; init; } = 80;

    /// <summary>
    /// Engine-wide default: queue behaviour when jitter buffers are full.
    /// Overridden per-source by <see cref="NDISourceOptions.QueueOverflowPolicy"/>.
    /// </summary>
    public NDIQueueOverflowPolicy QueueOverflowPolicy { get; init; } = NDIQueueOverflowPolicy.DropOldest;

    /// <summary>
    /// Engine-wide default: video fallback when no new frame is available.
    /// Overridden per-source by <see cref="NDISourceOptions.VideoFallbackMode"/>.
    /// </summary>
    public NDIVideoFallbackMode VideoFallbackMode { get; init; } = NDIVideoFallbackMode.NoFrame;

    /// <summary>
    /// Minimal buffering for lowest latency. May drop frames on jittery networks.
    /// </summary>
    public static NDILimitsOptions LowLatency => new()
    {
        VideoJitterBufferFrames = 1,
        AudioJitterBufferMs = 20,
        MaxPendingAudioFrames = 4,
        MaxPendingVideoFrames = 4,
    };

    /// <summary>
    /// Good trade-off between latency and resilience. Matches the default constructor values.
    /// </summary>
    public static NDILimitsOptions Balanced => new();

    /// <summary>
    /// Deep buffers for unreliable or high-jitter networks. Adds latency but avoids drops.
    /// </summary>
    public static NDILimitsOptions Safe => new()
    {
        VideoJitterBufferFrames = 6,
        AudioJitterBufferMs = 150,
        MaxPendingAudioFrames = 16,
        MaxPendingVideoFrames = 16,
    };

    public NDILimitsOptions Normalize()
    {
        return this with
        {
            MaxChildrenPerParent = Math.Max(1, MaxChildrenPerParent),
            MaxPendingAudioFrames = Math.Max(1, MaxPendingAudioFrames),
            MaxPendingVideoFrames = Math.Max(1, MaxPendingVideoFrames),
            VideoJitterBufferFrames = Math.Max(1, VideoJitterBufferFrames),
            AudioJitterBufferMs = Math.Max(1, AudioJitterBufferMs),
        };
    }
}
