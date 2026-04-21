using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace S.Media.FFmpeg;

/// <summary>
/// Async orchestration helpers for decode worker loops.
/// Keeps channel read/control flow asynchronous while unsafe FFmpeg work
/// stays in the channel classes.
/// </summary>
internal static class FFmpegDecodeWorkers
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegDecodeWorkers));

    /// <summary>
    /// Runs the generic decode loop for any <see cref="IDecodableChannel"/> implementation.
    /// <para><b>Seek-epoch protocol:</b> Each packet carries a monotonically-increasing
    /// <see cref="EncodedPacket.SeekEpoch"/>. When the user seeks, the demux thread bumps
    /// the epoch and sends a <see cref="EncodedPacket.IsFlush"/> sentinel. The decode loop
    /// drops packets with stale epochs, applies the flush (codec reset + PTS re-origin)
    /// when the sentinel arrives, then resumes decoding at the new epoch.</para>
    /// <para><b>Packet pool lifecycle:</b> The demux thread rents <see cref="EncodedPacket"/>
    /// wrappers from <paramref name="packetPool"/>; this method returns them after decoding.
    /// The <em>payload</em> (<see cref="EncodedPacket.Data"/>) is separately pooled via
    /// <see cref="System.Buffers.ArrayPool{T}"/> — returned in the <c>finally</c> block
    /// when <see cref="EncodedPacket.IsPooled"/> is <see langword="true"/>.</para>
    /// </summary>
    public static async Task RunAsync<TChannel>(
        TChannel owner,
        ChannelReader<EncodedPacket> packetReader,
        CancellationToken token,
        ConcurrentQueue<EncodedPacket>? packetPool = null)
        where TChannel : IDecodableChannel
    {
        string kind = typeof(TChannel).Name.Contains("Audio") ? "Audio" : "Video";
        Log.LogDebug("{Kind} decode worker starting for stream {StreamIndex}", kind, owner.StreamIndex);
        int currentEpoch = 0;
        long frameCount = 0;
        bool reachedEof = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                EncodedPacket ep;
                try { ep = await packetReader.ReadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException)
                {
                    reachedEof = !token.IsCancellationRequested;
                    break;
                }

                try
                {
                    // ── Seek-epoch filtering ──────────────────────────────────
                    // Drop packets produced before the latest seek; they belong to
                    // the old playback position and would decode stale frames.
                    if (ep.SeekEpoch < owner.LatestSeekEpoch)
                    {
                        if (Log.IsEnabled(LogLevel.Trace))
                            Log.LogTrace("{Kind} stream {StreamIndex}: dropping stale packet (epoch {PacketEpoch} < {CurrentEpoch})",
                                kind, owner.StreamIndex, ep.SeekEpoch, owner.LatestSeekEpoch);
                        continue;
                    }

                    // ── Flush sentinel ─────────────────────────────────────
                    // The demux thread sends a zero-length IsFlush packet after
                    // av_seek_frame.  Apply it once per epoch to reset the codec's
                    // internal state and re-origin PTS tracking.
                    if (ep.IsFlush)
                    {
                        if (ep.SeekEpoch > currentEpoch)
                        {
                            currentEpoch = ep.SeekEpoch;
                            owner.ApplySeekEpoch(ep.SeekPositionTicks);
                            Log.LogDebug("{Kind} stream {StreamIndex}: flush applied, epoch={Epoch}", kind, owner.StreamIndex, currentEpoch);
                        }
                        continue;
                    }

                    if (ep.SeekEpoch > currentEpoch)
                    {
                        currentEpoch = ep.SeekEpoch;
                        owner.ApplySeekEpoch(ep.SeekPositionTicks);
                    }

                    frameCount++;
                    if (Log.IsEnabled(LogLevel.Trace))
                        Log.LogTrace("{Kind} stream {StreamIndex}: decoding packet #{Count} pts={Pts} size={Size}",
                            kind, owner.StreamIndex, frameCount, ep.Pts, ep.ActualLength);

                    if (!owner.DecodePacketAndEnqueue(ep, token))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "{Kind} stream={StreamIndex} decode-loop error: epoch={Epoch} packetEpoch={PacketEpoch} packetBytes={PacketBytes}",
                        kind, owner.StreamIndex, currentEpoch, ep.SeekEpoch, ep.ActualLength);
                    break;
                }
                finally
                {
                    if (ep.IsPooled)
                        ArrayPool<byte>.Shared.Return(ep.Data);
                    if (!ep.IsFlush)
                        packetPool?.Enqueue(ep);
                }
            }
        }
        finally
        {
            if (reachedEof) owner.RaiseEndOfStream();
            owner.CompleteDecodeLoop();
            Log.LogDebug("{Kind} decode worker finished for stream {StreamIndex}, decoded {FrameCount} packets", kind, owner.StreamIndex, frameCount);
        }
    }
}
