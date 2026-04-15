namespace S.Media.NDI;

/// <summary>
/// User-facing receive latency preset for NDI source buffering.
/// Wraps a queue depth value and provides common defaults.
/// </summary>
public readonly record struct NDILatencyPreset(int QueueDepth)
{
    /// <summary>Highest jitter tolerance, largest queue depth.</summary>
    public static NDILatencyPreset Safe => new(12);

    /// <summary>Default profile balancing latency and stability.</summary>
    public static NDILatencyPreset Balanced => new(8);

    /// <summary>Lowest queue depth for minimum latency.</summary>
    public static NDILatencyPreset LowLatency => new(4);

    /// <summary>
    /// Creates a preset from a raw queue depth value.
    /// Invalid values are clamped by <see cref="ResolveQueueDepth"/>.
    /// </summary>
    public static NDILatencyPreset FromQueueDepth(int queueDepth) => new(queueDepth);

    public int ResolveQueueDepth() => QueueDepth > 0 ? QueueDepth : Balanced.QueueDepth;

    public static NDILatencyPreset FromEndpointPreset(NDIEndpointPreset preset) => preset switch
    {
        NDIEndpointPreset.Safe => Safe,
        NDIEndpointPreset.LowLatency => LowLatency,
        _ => Balanced
    };
}

