namespace S.Media.NDI;

/// <summary>
/// End-user profile presets for NDI endpoints.
/// </summary>
public enum NDIEndpointPreset
{
    Safe,
    Balanced,
    LowLatency
}

public readonly record struct NDIVideoPresetOptions(
    int PoolCount,
    int MaxPendingFrames)
{
    public static NDIVideoPresetOptions For(NDIEndpointPreset preset) => preset switch
    {
        NDIEndpointPreset.Safe => new NDIVideoPresetOptions(PoolCount: 8, MaxPendingFrames: 12),
        NDIEndpointPreset.Balanced => new NDIVideoPresetOptions(PoolCount: 4, MaxPendingFrames: 6),
        NDIEndpointPreset.LowLatency => new NDIVideoPresetOptions(PoolCount: 3, MaxPendingFrames: 2),
        _ => new NDIVideoPresetOptions(PoolCount: 4, MaxPendingFrames: 6)
    };
}

public readonly record struct NDIAudioPresetOptions(
    int PoolCount,
    int MaxPendingBuffers,
    int BufferHeadroomMultiplier)
{
    public static NDIAudioPresetOptions For(NDIEndpointPreset preset) => preset switch
    {
        NDIEndpointPreset.Safe => new NDIAudioPresetOptions(PoolCount: 12, MaxPendingBuffers: 16, BufferHeadroomMultiplier: 3),
        NDIEndpointPreset.Balanced => new NDIAudioPresetOptions(PoolCount: 8, MaxPendingBuffers: 8, BufferHeadroomMultiplier: 2),
        NDIEndpointPreset.LowLatency => new NDIAudioPresetOptions(PoolCount: 4, MaxPendingBuffers: 3, BufferHeadroomMultiplier: 2),
        _ => new NDIAudioPresetOptions(PoolCount: 8, MaxPendingBuffers: 8, BufferHeadroomMultiplier: 2)
    };
}


