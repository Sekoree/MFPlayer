using System.Diagnostics;
using HaViz.Core;
using S.Media.Core.Audio;
using Xunit;

namespace HaViz.Core.Tests;

/// <summary>
/// The SPSC ring that keeps <c>VizNdiEngine.SubmitPcm</c> off the SDK's blocking audio clock. The
/// interesting behaviour is all on the overflow edge: the NDI sender is clock-paced, so the consumer
/// drains at exactly the production rate and can never work off a backlog by itself.
/// </summary>
public sealed class NdiAudioSubmitRingTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public void Submit_DeliversToTheSink_OffTheProducerThread()
    {
        var sink = new CollectingSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);

        ring.Submit(Ramp(480, from: 1));

        Assert.True(sink.WaitForFloats(960, TimeSpan.FromSeconds(5)), "sender thread never submitted");
        Assert.Equal(0, ring.DroppedFrames);
        Assert.NotEqual(Environment.CurrentManagedThreadId, sink.SubmittingThreadId);
    }

    /// <summary>
    /// Parks the sender thread inside a blocked <c>Submit</c> with the ring EMPTY, so everything after
    /// this call is a pure single-threaded exercise of the producer side.
    /// <para>Without it the overflow tests race the sender's start-up: it drains up to one scratch batch
    /// (an eighth of the ring) out from under the producer, which is exactly enough headroom to make a
    /// submit sequence sized to overflow by one chunk fit instead - <c>OverflowEvents</c> then reads 0.
    /// Proven by holding the sender off deliberately: <c>Overflow_CountsOneEventPerStall</c> failed
    /// "Expected: 1, Actual: 0".</para>
    /// The priming chunk is a single frame so one <c>Read</c> always takes all of it regardless of the
    /// ring's scratch size; once <see cref="BlockedSink.WaitUntilBlocked"/> returns, that read has already
    /// happened (the sink only signals from inside Submit) and no further drain can occur.
    /// </summary>
    private static void ParkSenderInABlockedSubmit(NdiAudioSubmitRing ring, BlockedSink sink)
    {
        ring.Submit(new float[1 * 2]);
        Assert.True(sink.WaitUntilBlocked(TimeSpan.FromSeconds(5)), "sender never entered the blocking submit");
        Assert.Equal(0, ring.BufferedFrames);
        Assert.Equal(0, ring.DroppedFrames);
        Assert.Equal(0, ring.OverflowEvents);
    }

    [Fact]
    public void Overflow_TrimsBackToALowWatermark_SoTheRingRecoversItsLatency()
    {
        // The defect: trimming to "capacity minus this chunk" left the ring pinned AT capacity, so NDI
        // audio stayed a whole ring-depth (250 ms at the engine's defaults) behind the video forever and
        // every subsequent submit dropped again. After one overflow the backlog must be small.
        var sink = new BlockedSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);
        var capacityFrames = ring.CapacityFrames;
        ParkSenderInABlockedSubmit(ring, sink);

        for (var i = 0; i < capacityFrames / 480 + 4; i++)
            ring.Submit(new float[480 * 2]);

        Assert.True(ring.DroppedFrames > 0, "the ring never reported the overflow");
        Assert.True(ring.OverflowEvents >= 1);
        Assert.True(
            ring.BufferedFrames <= capacityFrames / 2,
            $"ring stayed pinned near capacity after overflow ({ring.BufferedFrames}/{capacityFrames} frames)");

        // ...and the recovered headroom is real: the next chunks fit without dropping anything more.
        var droppedAfterRecovery = ring.DroppedFrames;
        var overflowsAfterRecovery = ring.OverflowEvents;
        ring.Submit(new float[480 * 2]);
        ring.Submit(new float[480 * 2]);
        Assert.Equal(droppedAfterRecovery, ring.DroppedFrames);
        Assert.Equal(overflowsAfterRecovery, ring.OverflowEvents);

        sink.Release();
    }

    [Fact]
    public void Overflow_CountsOneEventPerStall_NotOnePerSubmit()
    {
        var sink = new BlockedSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);
        ParkSenderInABlockedSubmit(ring, sink);

        // One chunk more than the ring holds: a single trim, therefore a single overflow EVENT even
        // though the eleventh submit is what tripped it.
        for (var i = 0; i < ring.CapacityFrames / 480 + 1; i++)
            ring.Submit(new float[480 * 2]);

        Assert.Equal(1, ring.OverflowEvents);
        sink.Release();
    }

    [Fact]
    public void ChunkLargerThanTheRing_KeepsTheNewestTail_AndCountsTheHead()
    {
        var sink = new BlockedSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 1024);
        var capacityFrames = ring.CapacityFrames;
        ParkSenderInABlockedSubmit(ring, sink);

        ring.Submit(new float[(capacityFrames + 500) * 2]);

        // Exactly the head that could not fit; the newest ring-full tail is kept, and this is a
        // truncation rather than a drop-oldest overflow of already-buffered audio.
        Assert.Equal(500, ring.DroppedFrames);
        Assert.Equal(0, ring.OverflowEvents);
        sink.Release();
    }

    [Fact]
    public void Submit_RejectsAPartialFrame()
    {
        var sink = new CollectingSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);

        Assert.Throws<ArgumentException>(() => ring.Submit(new float[3]));
    }

    [Fact]
    public void StopAndJoin_TimesOutOnABlockedSender_AndReportsIt()
    {
        var sink = new BlockedSink(Stereo48k);
        var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);
        ring.Submit(new float[480 * 2]);
        Assert.True(sink.WaitUntilBlocked(TimeSpan.FromSeconds(5)), "sender never entered the blocking submit");

        var stopped = ring.StopAndJoin(TimeSpan.FromMilliseconds(100));

        Assert.False(stopped); // caller must leak the native sender rather than free it under a live thread
        Assert.True(ring.IsSenderAlive);

        // Idempotency: Dispose after a failed stop must not fault on the (still live) wait handles, and
        // once the sender finally comes off the SDK a repeat stop completes cleanly.
        sink.Release();
        Assert.True(ring.StopAndJoin(TimeSpan.FromSeconds(5)));
        ring.Dispose();
    }

    [Fact]
    public void Dispose_AfterAnExplicitStopAndJoin_IsANoOp()
    {
        // VizNdiEngine.Dispose calls StopAndJoin and then the ring's Dispose; the second teardown used to
        // hit the already-disposed CTS/event and throw ObjectDisposedException out of engine shutdown.
        var sink = new CollectingSink(Stereo48k);
        var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);

        Assert.True(ring.StopAndJoin(TimeSpan.FromSeconds(5)));
        ring.Dispose();
        ring.Dispose();
        Assert.True(ring.StopAndJoin(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Submit_AfterStop_IsIgnored()
    {
        var sink = new CollectingSink(Stereo48k);
        var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);
        Assert.True(ring.StopAndJoin(TimeSpan.FromSeconds(5)));

        ring.Submit(new float[480 * 2]); // must not touch the disposed event

        Assert.Equal(0, ring.BufferedFrames);
    }

    [Fact]
    public void FailingSink_DoesNotKillTheSenderThread()
    {
        var sink = new ThrowingSink(Stereo48k);
        using var ring = new NdiAudioSubmitRing(sink, Stereo48k, capacityFrames: 4800);

        for (var i = 0; i < 10; i++)
        {
            ring.Submit(new float[480 * 2]);
            Thread.Sleep(5);
        }

        // A faulted sender must not kill the thread: the engine keeps rendering video and the next
        // submit may succeed, so the loop has to keep pulling from the ring.
        Assert.True(sink.WaitForAttempts(3, TimeSpan.FromSeconds(5)), "sender stopped after a submit failure");
        Assert.True(ring.IsSenderAlive);
    }

    private static float[] Ramp(int frames, float from)
    {
        var buffer = new float[frames * 2];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = from + i;
        return buffer;
    }

    private sealed class CollectingSink(AudioFormat format) : IAudioOutput
    {
        private readonly List<float> _received = [];
        // Signalled on every submit so the waiters below block on the event instead of polling a
        // counter behind Thread.Sleep - no sampling interval to lose a race against.
        private readonly ManualResetEventSlim _submitted = new(false);
        private long _floats;

        public AudioFormat Format { get; } = format;
        public int SubmittingThreadId { get; private set; }

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            lock (_received)
            {
                SubmittingThreadId = Environment.CurrentManagedThreadId;
                foreach (var sample in packedSamples)
                    _received.Add(sample);
                Interlocked.Exchange(ref _floats, _received.Count);
            }

            _submitted.Set();
        }

        public bool WaitForFloats(int count, TimeSpan timeout)
        {
            var remaining = Stopwatch.StartNew();
            while (Interlocked.Read(ref _floats) < count)
            {
                _submitted.Reset();
                if (Interlocked.Read(ref _floats) >= count) // re-check: a submit may have raced the reset
                    return true;
                var left = timeout - remaining.Elapsed;
                if (left <= TimeSpan.Zero || !_submitted.Wait(left))
                    return false;
            }

            return true;
        }
    }

    /// <summary>Stands in for the clock-paced NDI sender that has stalled: the first submit blocks until
    /// released, so the ring can only overflow.</summary>
    private sealed class BlockedSink(AudioFormat format) : IAudioOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _blocked = new(false);

        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            _blocked.Set();
            _release.Wait();
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();
    }

    private sealed class ThrowingSink(AudioFormat format) : IAudioOutput
    {
        private readonly ManualResetEventSlim _attempted = new(false);
        private int _attempts;

        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            Interlocked.Increment(ref _attempts);
            _attempted.Set();
            throw new InvalidOperationException("sender is gone");
        }

        public bool WaitForAttempts(int count, TimeSpan timeout)
        {
            var remaining = Stopwatch.StartNew();
            while (Volatile.Read(ref _attempts) < count)
            {
                _attempted.Reset();
                if (Volatile.Read(ref _attempts) >= count) // re-check: an attempt may have raced the reset
                    return true;
                var left = timeout - remaining.Elapsed;
                if (left <= TimeSpan.Zero || !_attempted.Wait(left))
                    return false;
            }

            return true;
        }
    }
}
