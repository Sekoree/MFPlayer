using System.Buffers;
using System.Threading.Channels;

namespace S.Media.FFmpeg;

/// <summary>
/// Async orchestration helpers for decode worker loops.
/// Keeps channel read/control flow asynchronous while unsafe FFmpeg work
/// stays in the channel classes.
/// </summary>
internal static class FFmpegDecodeWorkers
{
    public static async Task RunAudioAsync(
        FFmpegAudioChannel owner,
        ChannelReader<EncodedPacket> packetReader,
        CancellationToken token)
    {
        int currentEpoch = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                EncodedPacket ep;
                try { ep = await packetReader.ReadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }

                try
                {
                    if (ep.SeekEpoch < owner.LatestSeekEpoch)
                        continue;

                    if (ep.IsFlush)
                    {
                        // Ignore delayed duplicate flush packets for the current epoch.
                        // Applying them again would rewind channel position unexpectedly.
                        if (ep.SeekEpoch > currentEpoch)
                        {
                            currentEpoch = ep.SeekEpoch;
                            owner.ApplySeekEpoch(ep.SeekPositionTicks);
                        }
                        continue;
                    }

                    if (ep.SeekEpoch > currentEpoch)
                    {
                        currentEpoch = ep.SeekEpoch;
                        owner.ApplySeekEpoch(ep.SeekPositionTicks);
                    }

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
                }
            }
        }
        finally
        {
            owner.CompleteDecodeLoop();
        }
    }

    public static async Task RunVideoAsync(
        FFmpegVideoChannel owner,
        ChannelReader<EncodedPacket> packetReader,
        CancellationToken token)
    {
        int currentEpoch = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                EncodedPacket ep;
                try { ep = await packetReader.ReadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ChannelClosedException) { break; }

                try
                {
                    if (ep.SeekEpoch < owner.LatestSeekEpoch)
                        continue;

                    if (ep.IsFlush)
                    {
                        // Ignore delayed duplicate flush packets for the current epoch.
                        // Applying them again would rewind channel position unexpectedly.
                        if (ep.SeekEpoch > currentEpoch)
                        {
                            currentEpoch = ep.SeekEpoch;
                            owner.ApplySeekEpoch(ep.SeekPositionTicks);
                        }
                        continue;
                    }

                    if (ep.SeekEpoch > currentEpoch)
                    {
                        currentEpoch = ep.SeekEpoch;
                        owner.ApplySeekEpoch(ep.SeekPositionTicks);
                    }

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
                }
            }
        }
        finally
        {
            owner.CompleteDecodeLoop();
        }
    }
}

