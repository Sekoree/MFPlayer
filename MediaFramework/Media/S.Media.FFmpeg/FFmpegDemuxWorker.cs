using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace S.Media.FFmpeg;

/// <summary>
/// Async orchestration for demux packet routing.
/// Reads encoded packets through FFmpegDecoder unsafe helpers and awaits queue writes.
/// </summary>
internal static class FFmpegDemuxWorker
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegDemuxWorker));

    public static async Task RunAsync(FFmpegDecoder owner, CancellationToken token)
    {
        Log.LogDebug("Demux worker starting");
        nint pktHandle = owner.AllocateDemuxPacket();
        var packetPool = owner.PacketPool;
        long packetCount = 0;
        int  consecutiveRetries = 0;
        const int maxConsecutiveRetries = 256;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var read = owner.TryReadNextPacket(pktHandle, out var writer, out var packet, token);
                    switch (read)
                    {
                        case FFmpegDecoder.DemuxReadResult.Packet:
                            consecutiveRetries = 0;
                            if (writer != null && packet != null)
                            {
                                packetCount++;
                                if (Log.IsEnabled(LogLevel.Trace))
                                    Log.LogTrace("Demux packet #{Count}: pts={Pts} size={Size} epoch={Epoch}",
                                        packetCount, packet.Pts, packet.ActualLength, packet.SeekEpoch);

                                if (!await WritePacketAsync(owner, writer, packet, packetPool, token).ConfigureAwait(false))
                                {
                                    Log.LogDebug("Demux worker stopping: write channel closed");
                                    return;
                                }
                            }
                            break;

                        case FFmpegDecoder.DemuxReadResult.Retry:
                            // Back-off + bail-out so a stuck source never spins the CPU
                            // (previously this was a tight `continue` — see §3.3 / B9).
                            if (++consecutiveRetries > maxConsecutiveRetries)
                            {
                                Log.LogWarning("Demux worker exiting after {N} consecutive Retry results", consecutiveRetries);
                                return;
                            }
                            if ((consecutiveRetries & 0xF) == 0)
                                await Task.Delay(1, token).ConfigureAwait(false);
                            else
                                await Task.Yield();
                            continue;

                        case FFmpegDecoder.DemuxReadResult.Eof:
                            Log.LogInformation("Demux worker reached EOF after {PacketCount} packets", packetCount);
                            owner.RaiseEndOfMedia();
                            return;

                        case FFmpegDecoder.DemuxReadResult.Cancelled:
                            Log.LogDebug("Demux worker cancelled after {PacketCount} packets", packetCount);
                            return;

                        case FFmpegDecoder.DemuxReadResult.Fatal:
                            Log.LogWarning("Demux worker stopping after fatal IO error (packets read: {PacketCount})", packetCount);
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.LogDebug("Demux worker cancelled (OperationCanceledException)");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    Log.LogDebug("Demux worker stopped (ObjectDisposedException)");
                    return;
                }
                catch (Exception ex)
                {
                    owner.ReportDemuxLoopError(ex);
                    return;
                }
            }
        }
        finally
        {
            owner.FreeDemuxPacket(pktHandle);
            owner.CompletePacketQueues();
            // §EOF-reliability — TODO #1: Retry-exhaustion (256 consecutive
            // retries) and Fatal-IO exits previously left the worker silent,
            // so MediaPlayer never observed the terminal event and the UI was
            // stuck in `Playing`. Always raise EndOfMedia once on any non-
            // cancellation exit; the call itself is idempotent so the clean
            // EOF path's explicit RaiseEndOfMedia above is unaffected.
            if (!token.IsCancellationRequested)
            {
                try
                {
                    owner.RaiseEndOfMedia();
                }
                catch (Exception ex)
                {
                    Log.LogDebug(ex, "Demux worker: RaiseEndOfMedia (finally fallback) threw");
                }
            }
            Log.LogDebug("Demux worker finished, total packets demuxed: {PacketCount}", packetCount);
        }
    }

    /// <summary>
    /// §heavy-media-fixes phase 4 — soft watchdog timeout. If the per-stream
    /// packet queue stays full for longer than this, <see cref="WritePacketAsync"/>
    /// drops the packet and continues so the demux loop never sits forever
    /// (which used to mask <c>AVERROR_EOF</c> behind a stuck consumer).
    /// 2 s is far longer than any sane decode hiccup and shorter than any
    /// "did the file end?" user expectation.
    /// </summary>
    private static readonly TimeSpan WriteStallBudget = TimeSpan.FromSeconds(2);
    private static long s_demuxBackpressureDrops;

    /// <summary>
    /// Total packets dropped by the demux watchdog across all running
    /// instances. Surfaces backpressure pathologies in tests / diagnostics.
    /// </summary>
    public static long DemuxBackpressureDrops => Interlocked.Read(ref s_demuxBackpressureDrops);

    private static async ValueTask<bool> WritePacketAsync(
        FFmpegDecoder owner,
        ChannelWriter<EncodedPacket> writer,
        EncodedPacket packet,
        ConcurrentQueue<EncodedPacket> packetPool,
        CancellationToken token)
    {
        try
        {
            // §8.5 — fast path: bounded channels often have immediate space.
            // Avoiding the async state machine in this case trims per-packet
            // overhead on demux-heavy streams.
            if (writer.TryWrite(packet))
                return true;

            // §heavy-media-fixes phase 4 — bound the wait. A linked CTS lets
            // us tell apart "user cancellation" (return false to stop the
            // loop) from "decoder stall" (drop the packet and keep going so
            // EOF can still surface).
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            stallCts.CancelAfter(WriteStallBudget);
            try
            {
                await writer.WriteAsync(packet, stallCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Stall budget exhausted — log once per N drops and discard
                // the packet. The decoder will resync on the next keyframe.
                long drops = Interlocked.Increment(ref s_demuxBackpressureDrops);
                if (drops <= 3 || drops % 100 == 0)
                {
                    Log.LogWarning(
                        "Demux watchdog: dropped packet pts={Pts} after {BudgetMs}ms write stall (total={Total})",
                        packet.Pts, (long)WriteStallBudget.TotalMilliseconds, drops);
                }
                if (packet.IsPooled)
                    ArrayPool<byte>.Shared.Return(packet.Data);
                packetPool.Enqueue(packet);
                // §heavy-media-fixes phase 7 — surface the stall to MediaPlayer
                // / SPlayer so the UI can flash a "buffering" badge. The
                // event itself is queued onto the thread pool by the
                // decoder so we don't slow the demux loop.
                owner.RaiseBufferStalled(drops);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            if (packet.IsPooled)
                ArrayPool<byte>.Shared.Return(packet.Data);
            packetPool.Enqueue(packet);
            return false;
        }
        catch (ChannelClosedException)
        {
            if (packet.IsPooled)
                ArrayPool<byte>.Shared.Return(packet.Data);
            packetPool.Enqueue(packet);
            return false;
        }
    }
}
