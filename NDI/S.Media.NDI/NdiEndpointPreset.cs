namespace S.Media.NDI;

/// <summary>
/// End-user profile presets for NDI endpoints.
/// </summary>
public enum NdiEndpointPreset
{
    Safe,
    Balanced,
    LowLatency
}

public readonly record struct NdiVideoPresetOptions(
    int PoolCount,
    int MaxPendingFrames)
{
    public static NdiVideoPresetOptions For(NdiEndpointPreset preset) => preset switch
    {
        NdiEndpointPreset.Safe => new NdiVideoPresetOptions(PoolCount: 8, MaxPendingFrames: 12),
        NdiEndpointPreset.Balanced => new NdiVideoPresetOptions(PoolCount: 4, MaxPendingFrames: 6),
        NdiEndpointPreset.LowLatency => new NdiVideoPresetOptions(PoolCount: 3, MaxPendingFrames: 2),
        _ => new NdiVideoPresetOptions(PoolCount: 4, MaxPendingFrames: 6)
    };
}

public readonly record struct NdiAudioPresetOptions(
    int PoolCount,
    int MaxPendingBuffers,
    int BufferHeadroomMultiplier)
{
    public static NdiAudioPresetOptions For(NdiEndpointPreset preset) => preset switch
    {
        NdiEndpointPreset.Safe => new NdiAudioPresetOptions(PoolCount: 12, MaxPendingBuffers: 16, BufferHeadroomMultiplier: 3),
        NdiEndpointPreset.Balanced => new NdiAudioPresetOptions(PoolCount: 8, MaxPendingBuffers: 8, BufferHeadroomMultiplier: 2),
        NdiEndpointPreset.LowLatency => new NdiAudioPresetOptions(PoolCount: 4, MaxPendingBuffers: 3, BufferHeadroomMultiplier: 2),
        _ => new NdiAudioPresetOptions(PoolCount: 8, MaxPendingBuffers: 8, BufferHeadroomMultiplier: 2)
    };
}

