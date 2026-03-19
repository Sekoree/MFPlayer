namespace Seko.OwnAudioNET.Video.Decoders;

/// <summary>Configuration for <see cref="FFSharedDemuxSession"/>.</summary>
public sealed class FFSharedDemuxSessionOptions
{
    /// <summary>
    /// Optional list of stream indices to route.
    /// When empty, audio/video streams can be registered lazily by decoders.
    /// </summary>
    public IReadOnlyList<int> InitialStreamIndices { get; init; } = [];

    /// <summary>
    /// Max queued compressed packets per stream.
    /// Higher values smooth over decode spikes but increase memory usage.
    /// </summary>
    public int PacketQueueCapacityPerStream { get; init; } = 160;

    internal int NormalizedPacketQueueCapacity => Math.Max(16, PacketQueueCapacityPerStream);
}

