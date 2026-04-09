using System.Buffers;
using System.Threading.Channels;

namespace S.Media.FFmpeg;

/// <summary>
/// Async orchestration for demux packet routing.
/// Reads encoded packets through FFmpegDecoder unsafe helpers and awaits queue writes.
/// </summary>
internal static class FFmpegDemuxWorker
{
    public static async Task RunAsync(FFmpegDecoder owner, CancellationToken token)
    {
        nint pktHandle = owner.AllocateDemuxPacket();
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
                            if (writer != null && packet != null)
                            {
                                if (!await WritePacketAsync(writer, packet, token).ConfigureAwait(false))
                                    return;
                            }
                            break;

                        case FFmpegDecoder.DemuxReadResult.Retry:
                            continue;

                        case FFmpegDecoder.DemuxReadResult.Eof:
                        case FFmpegDecoder.DemuxReadResult.Cancelled:
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
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
        }
    }

    private static async ValueTask<bool> WritePacketAsync(
        ChannelWriter<EncodedPacket> writer,
        EncodedPacket packet,
        CancellationToken token)
    {
        try
        {
            await writer.WriteAsync(packet, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (packet.IsPooled)
                ArrayPool<byte>.Shared.Return(packet.Data);
            return false;
        }
        catch (ChannelClosedException)
        {
            if (packet.IsPooled)
                ArrayPool<byte>.Shared.Return(packet.Data);
            return true;
        }
    }
}

