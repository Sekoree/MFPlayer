namespace S.Media.FFmpeg;

/// <summary>
/// Internal contract shared by <see cref="FFmpegAudioChannel"/> and <see cref="FFmpegVideoChannel"/>
/// so that <see cref="FFmpegDecodeWorkers"/> can use a single generic decode loop.
/// Each member corresponds to a specific phase of the per-stream decode lifecycle.
/// </summary>
internal interface IDecodableChannel
{
    /// <summary>FFmpeg stream index this channel reads from.</summary>
    int StreamIndex { get; }

    /// <summary>
    /// The latest seek epoch acknowledged by the demux thread.
    /// Packets with a lower epoch are stale and should be discarded.
    /// </summary>
    int LatestSeekEpoch { get; }

    /// <summary>Flush codec state and reset PTS tracking for the new seek position.</summary>
    void ApplySeekEpoch(long seekPositionTicks);

    /// <summary>
    /// Decodes the packet's payload and pushes the resulting frames into the ring buffer.
    /// Returns <see langword="false"/> to abort the loop (e.g. channel disposed).
    /// </summary>
    bool DecodePacketAndEnqueue(EncodedPacket ep, CancellationToken token);


    /// <summary>Fires the channel's end-of-stream event.</summary>
    void RaiseEndOfStream();

    /// <summary>
    /// Called in the <c>finally</c> block when the decode loop exits (normally or by exception).
    /// Used to complete the output ring buffer so downstream consumers stop waiting.
    /// </summary>
    void CompleteDecodeLoop();
}

