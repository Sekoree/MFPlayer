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

    public static async Task RunAudioAsync(
        FFmpegAudioChannel owner,
        ChannelReader<EncodedPacket> packetReader,
        CancellationToken token,
        ConcurrentQueue<EncodedPacket>? packetPool = null)
    {
        Log.LogDebug("Audio decode worker starting for stream {StreamIndex}", owner.StreamIndex);
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
                    if (ep.SeekEpoch < owner.LatestSeekEpoch)
                    {
                        if (Log.IsEnabled(LogLevel.Trace))
                            Log.LogTrace("Audio stream {StreamIndex}: dropping stale packet (epoch {PacketEpoch} < {CurrentEpoch})",
                                owner.StreamIndex, ep.SeekEpoch, owner.LatestSeekEpoch);
                        continue;
                    }

                    if (ep.IsFlush)
                    {
                        if (ep.SeekEpoch > currentEpoch)
                        {
                            currentEpoch = ep.SeekEpoch;
                            owner.ApplySeekEpoch(ep.SeekPositionTicks);
                            Log.LogDebug("Audio stream {StreamIndex}: flush applied, epoch={Epoch}", owner.StreamIndex, currentEpoch);
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
                        Log.LogTrace("Audio stream {StreamIndex}: decoding packet #{Count} pts={Pts} size={Size}",
                            owner.StreamIndex, frameCount, ep.Pts, ep.ActualLength);

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
                    owner.ReportDecodeLoopError(ex, currentEpoch, ep);
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
            Log.LogDebug("Audio decode worker finished for stream {StreamIndex}, decoded {FrameCount} packets", owner.StreamIndex, frameCount);
        }
    }

    public static async Task RunVideoAsync(
        FFmpegVideoChannel owner,
        ChannelReader<EncodedPacket> packetReader,
        CancellationToken token,
        ConcurrentQueue<EncodedPacket>? packetPool = null)
    {
        Log.LogDebug("Video decode worker starting for stream {StreamIndex}", owner.StreamIndex);
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
                    if (ep.SeekEpoch < owner.LatestSeekEpoch)
                    {
                        if (Log.IsEnabled(LogLevel.Trace))
                            Log.LogTrace("Video stream {StreamIndex}: dropping stale packet (epoch {PacketEpoch} < {CurrentEpoch})",
                                owner.StreamIndex, ep.SeekEpoch, owner.LatestSeekEpoch);
                        continue;
                    }

                    if (ep.IsFlush)
                    {
                        if (ep.SeekEpoch > currentEpoch)
                        {
                            currentEpoch = ep.SeekEpoch;
                            owner.ApplySeekEpoch(ep.SeekPositionTicks);
                            Log.LogDebug("Video stream {StreamIndex}: flush applied, epoch={Epoch}", owner.StreamIndex, currentEpoch);
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
                        Log.LogTrace("Video stream {StreamIndex}: decoding packet #{Count} pts={Pts} size={Size}",
                            owner.StreamIndex, frameCount, ep.Pts, ep.ActualLength);

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
                    owner.ReportDecodeLoopError(ex, currentEpoch, ep);
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
            Log.LogDebug("Video decode worker finished for stream {StreamIndex}, decoded {FrameCount} packets", owner.StreamIndex, frameCount);
        }
    }
}
