using System.Collections.Concurrent;
using System.Threading.Channels;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegDecodeWorkersTests
{
    [Fact]
    public async Task RunAsync_SeekEpochFilter_DropsStalePackets_AndAppliesFlushOncePerEpoch()
    {
        var owner = new FakeDecodableChannel { LatestSeekEpoch = 2 };
        var packetPool = new ConcurrentQueue<EncodedPacket>();
        var channel = Channel.CreateUnbounded<EncodedPacket>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // Stale pre-seek packet: must be dropped.
        channel.Writer.TryWrite(new EncodedPacket([1, 2, 3], 3, 10, 0, 0, 0, isPooled: false, seekEpoch: 1, seekPositionTicks: 1_000));
        // Flush sentinel for epoch 2: must apply seek exactly once.
        channel.Writer.TryWrite(EncodedPacket.Flush(2, 2_000));
        // Fresh epoch-2 packet: decodes after flush.
        channel.Writer.TryWrite(new EncodedPacket([4], 1, 20, 0, 0, 0, isPooled: false, seekEpoch: 2, seekPositionTicks: 2_000));
        // Duplicate flush in same epoch: no second ApplySeekEpoch call.
        channel.Writer.TryWrite(EncodedPacket.Flush(2, 2_500));
        // New epoch transition via flush + payload.
        channel.Writer.TryWrite(EncodedPacket.Flush(3, 3_000));
        channel.Writer.TryWrite(new EncodedPacket([5], 1, 30, 0, 0, 0, isPooled: false, seekEpoch: 3, seekPositionTicks: 3_000));
        channel.Writer.TryComplete();

        await FFmpegDecodeWorkers.RunAsync(owner, channel.Reader, CancellationToken.None, packetPool);

        Assert.Equal([2, 3], owner.DecodedPacketEpochs);
        Assert.Equal([2_000L, 3_000L], owner.AppliedSeekPositions);
        Assert.Equal(1, owner.EndOfStreamCount);
        Assert.Equal(1, owner.CompleteDecodeLoopCount);
        Assert.Equal(3, packetPool.Count); // stale + epoch-2 + epoch-3 packets (flush packets are excluded)
    }

    private sealed class FakeDecodableChannel : IDecodableChannel
    {
        public int StreamIndex => 0;
        public int LatestSeekEpoch { get; set; }
        public List<int> DecodedPacketEpochs { get; } = [];
        public List<long> AppliedSeekPositions { get; } = [];
        public int EndOfStreamCount { get; private set; }
        public int CompleteDecodeLoopCount { get; private set; }

        public void ApplySeekEpoch(long seekPositionTicks)
            => AppliedSeekPositions.Add(seekPositionTicks);

        public bool DecodePacketAndEnqueue(EncodedPacket ep, CancellationToken token)
        {
            DecodedPacketEpochs.Add(ep.SeekEpoch);
            return true;
        }

        public void RaiseEndOfStream() => EndOfStreamCount++;
        public void CompleteDecodeLoop() => CompleteDecodeLoopCount++;
    }
}
