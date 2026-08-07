using System.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// A voice's clip router paces from its <see cref="ProgramBusProducer"/> (the bay lease is exposed
/// unwrapped precisely so it can). These cover the accounting that makes that loop closed.
/// </summary>
public sealed class ProgramBusPacingTests
{
    private const int Rate = 48000;
    private const int Chunk = 480;

    /// <summary>
    /// Regression (HaCue2 audio dropouts): the router does not submit the chunk it is granted - it
    /// mixes into an output pump and a drainer thread submits later. When the grant was answered from
    /// the ring alone, the same free space was handed out once per pump slot, so the router was waved
    /// through ~pumpCapacityChunks times per chunk the bus actually consumed. Production ran ~3.4x
    /// nominal, the ring pinned at capacity, and Submit dropped oldest frames for the rest of the run.
    /// </summary>
    [Fact]
    public void RouterPacedFromProducer_TracksTheBusInsteadOfOverflowingIt()
    {
        const int seconds = 3;
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");

        // Stands in for the bay's host router: pulls one chunk every 10 ms, i.e. exactly 48 kHz.
        using var stop = new CancellationTokenSource();
        var consumer = new Thread(() =>
        {
            var buffer = new float[Chunk * 2];
            var sw = Stopwatch.StartNew();
            var next = TimeSpan.FromMilliseconds(10);
            while (!stop.IsCancellationRequested)
            {
                var sleep = next - sw.Elapsed;
                if (sleep > TimeSpan.Zero) stop.Token.WaitHandle.WaitOne(sleep);
                next += TimeSpan.FromMilliseconds(10);
                bus.ReadInto(buffer);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };

        using var router = new AudioRouter(Rate, chunkSamples: Chunk);
        router.AddSource(new SilenceSource(new AudioFormat(Rate, 2)), "src");
        router.AddOutput(producer, "_program");
        router.AddRoute("src", "_program", ChannelMap.Identity(2));

        consumer.Start();
        router.Start();

        // The producer is the router's pacing primary - that is the whole point of handing the lease
        // over unwrapped, and every assertion below depends on it.
        Assert.Equal("_program", router.PrimaryOutputId);

        var sw = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        sw.Stop();

        var produced = router.ChunksProduced;
        var overflow = producer.OverflowFloats;
        var buffered = producer.BufferedFrames;
        router.Stop();
        stop.Cancel();
        consumer.Join(TimeSpan.FromSeconds(1));

        // Genlocked to the consumer, so production tracks elapsed wall time rather than running away.
        var expected = sw.Elapsed.TotalSeconds * Rate / Chunk;
        Assert.InRange(produced, expected * 0.75, expected * 1.25);

        // The defect was continuous, not a startup transient: any oldest-frame dropping here is the
        // open loop back.
        Assert.Equal(0, overflow);

        // Paced to half the ring, so the other half stays free as jitter headroom. Pinning at the
        // capacity (4800 frames requested rounds up to an 8192-frame ring) is the broken state.
        Assert.InRange(buffered, 1, 8192 / 2);
    }

    /// <summary>
    /// The grant must be retired by the audio arriving, not merely issued: a producer that leaked
    /// grants would starve its own router until WaitForCapacity timed out and faulted the run loop.
    /// </summary>
    [Fact]
    public void Grants_AreRetiredBySubmit_SoPacingSurvivesManyChunks()
    {
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");
        var chunk = new float[Chunk * 2];
        var drain = new float[Chunk * 2];

        // Far more chunks than the ring could ever hold at once - only correct retirement gets here.
        for (var i = 0; i < 500; i++)
        {
            Assert.True(producer.WaitForCapacity(Chunk, CancellationToken.None),
                $"capacity grant stalled at chunk {i} - grants are leaking");
            producer.Submit(chunk);
            bus.ReadInto(drain);
        }

        Assert.Equal(0, producer.OverflowFloats);
    }

    /// <summary>
    /// Flush is called by AudioRouter.FlushOutputBuffers right after it ABANDONS the output pump's
    /// queue, so chunks already granted will never arrive to retire themselves. Their credit has to go
    /// with them or every pause/seek would leak a little pacing credit permanently.
    /// </summary>
    [Fact]
    public void Flush_DropsOutstandingGrants()
    {
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");

        // Take grants until the producer is saturated, submitting nothing (the pump is holding them).
        var taken = 0;
        while (producer.WaitForCapacity(Chunk, new CancellationTokenSource(50).Token))
        {
            if (++taken > 64) break;
        }
        Assert.InRange(taken, 1, 64);

        producer.Flush();

        // A fresh grant is available immediately: the abandoned chunks took their credit with them.
        Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(50).Token),
            "Flush must release the credit of chunks the pump abandoned");
    }

    /// <summary>
    /// Regression (HaCue2 silent voices): a chunk the output pump DROPS after its grant was taken
    /// never reaches Submit, so its credit must be returned through
    /// <see cref="IGrantPacedOutput.OnGrantedChunkDropped"/>. Before that hook existed, each drop
    /// leaked its grant permanently; 8 drops exhausted the half-ring pacing target and the voice's
    /// router could never be granted again - it timed out, faulted "pacing clock failed", and the
    /// voice stayed silent for the rest of the show.
    /// </summary>
    [Fact]
    public void DroppedChunks_ReturnTheirGrants_SoTheVoiceKeepsFlowing()
    {
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");
        var needFloats = Chunk * 2;

        // The wedge sequence from the incident log: grants taken, chunks dropped by the pump.
        for (var i = 0; i < 32; i++)
        {
            Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(250).Token),
                $"grant {i} refused - dropped chunks are not returning their credit");
            ((IGrantPacedOutput)producer).OnGrantedChunkDropped(needFloats);
        }

        // And the voice still paces normally afterwards.
        Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(250).Token));
        producer.Dispose();
    }

    /// <summary>
    /// Defense in depth for the same regression: even a leak that BYPASSES the drop hook (a future
    /// unreported discard path) must not silence a voice forever. A timed-out capacity wait writes
    /// off outstanding credit once and retries; only a second timeout with no credit left to write
    /// off - the bus genuinely unconsumed - propagates the failure.
    /// </summary>
    [Fact]
    public void StaleGrantCredit_IsWrittenOffAfterOneTimeout_InsteadOfWedgingTheVoice()
    {
        // Short timeout so the starvation path runs in test time.
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800,
            capacityWaitTimeout: TimeSpan.FromMilliseconds(100));
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");

        // Leak grants with no submits and no drop reports until the producer refuses.
        var leaked = 0;
        while (producer.WaitForCapacity(Chunk, new CancellationTokenSource(30).Token))
        {
            if (++leaked > 64) break;
        }
        Assert.InRange(leaked, 1, 64);

        // Old behavior: this refused after the full timeout and the router faulted the voice.
        // Now: the first timeout reconciles the stale credit and the retry is granted.
        Assert.True(producer.WaitForCapacity(Chunk, CancellationToken.None),
            "stale grant credit must be written off after a timed-out wait, not wedge the voice");

        // A dead bus is still a visible failure: saturate the RING itself (credit is real now),
        // and with nobody consuming, the wait must ultimately refuse.
        var chunkBuf = new float[Chunk * 2];
        producer.Submit(chunkBuf); // retire the grant just taken
        while (producer.WaitForCapacity(Chunk, new CancellationTokenSource(30).Token))
            producer.Submit(chunkBuf);
        Assert.False(producer.WaitForCapacity(Chunk, new CancellationTokenSource(500).Token));

        producer.Dispose();
    }

    /// <summary>
    /// Regression (HaCue2 once-a-second ticks): granted-but-unsubmitted audio counts against the
    /// pacing target, so every chunk the pump queue holds is a chunk the RING is allowed to be
    /// short — at the pump's full depth the ring's share fell below one mix chunk and bus reads
    /// substituted silence whenever drainer scheduling lagged. Outstanding grants are therefore
    /// capped at two chunks: the ring's steady state keeps a floor of target minus two chunks.
    /// </summary>
    [Fact]
    public void OutstandingGrants_AreCapped_SoTheRingKeepsAFloor()
    {
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");
        var chunk = new float[Chunk * 2];

        Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(250).Token));
        Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(250).Token));

        // Two chunks in flight: the router may mix no further ahead until one lands.
        Assert.False(producer.WaitForCapacity(Chunk, new CancellationTokenSource(150).Token));

        // A submit retires cap credit and wakes the waiter.
        producer.Submit(chunk);
        Assert.True(producer.WaitForCapacity(Chunk, new CancellationTokenSource(250).Token));

        producer.Dispose();
    }

    /// <summary>
    /// Pre-roll (group-fire alignment): a held producer's ring accepts the clip's first samples but
    /// the bus skips it — no contribution, no underrun counting, no clock advance — and the release
    /// joins the mix with the buffered content intact from its first sample.
    /// </summary>
    [Fact]
    public void PreRoll_HoldsAudioOutOfTheMix_AndJoinsOnReleaseFromTheFirstSample()
    {
        using var bus = new ProgramBusSource(1, Rate, producerRingFrames: 4800);
        var producer = bus.AcquireProducer(1, [1f], "voice");
        var mix = new float[Chunk];

        producer.BeginPreRoll();
        var marked = new float[Chunk];
        for (var i = 0; i < marked.Length; i++)
            marked[i] = 0.5f;
        producer.Submit(marked);

        bus.ReadInto(mix);
        Assert.All(mix, sample => Assert.Equal(0f, sample));       // held: not in the mix
        Assert.Equal(0, producer.UnderrunFloats);                   // and not "underrunning"
        Assert.Equal(Chunk, producer.BufferedFrames);               // the pre-rolled audio is intact

        var epochWhileHeld = producer.EpochId;
        producer.EndPreRoll();
        // The release re-anchors: the clock advanced through the hold (it is time-based), and
        // without a fresh epoch every pre-rolled voice's position would lead its audible content
        // by the whole hold duration.
        Assert.NotEqual(epochWhileHeld, producer.EpochId);
        bus.ReadInto(mix);
        Assert.All(mix, sample => Assert.Equal(0.5f, sample));      // released: first sample onward

        producer.Dispose();
    }

    /// <summary>
    /// A held producer's pacing wait outlives the starvation timeout: the bus is deliberately not
    /// consuming it, and the start edge that releases it can sit behind a slow-arming sibling. The
    /// old behavior — timeout, write-off, then refusal — would have faulted the voice's router
    /// during a long group prepare.
    /// </summary>
    [Fact]
    public async Task WaitForCapacity_OutlivesTheStarvationTimeout_WhileHeld()
    {
        using var bus = new ProgramBusSource(2, Rate, producerRingFrames: 4800,
            capacityWaitTimeout: TimeSpan.FromMilliseconds(50));
        var producer = bus.AcquireProducer(2, [1f, 0f, 0f, 1f], "voice");
        var chunk = new float[Chunk * 2];

        producer.BeginPreRoll();
        // Fill to the cap so the waiter has to park.
        while (producer.WaitForCapacity(Chunk, new CancellationTokenSource(30).Token))
            producer.Submit(chunk);

        // Park a waiter well past several timeout periods - it must neither fault nor give up.
        using var cts = new CancellationTokenSource();
        var waiter = Task.Run(() => producer.WaitForCapacity(Chunk, cts.Token));
        Assert.NotSame(waiter, await Task.WhenAny(waiter, Task.Delay(300)));

        // The release lets the bus drain and the waiter come back with a grant.
        producer.EndPreRoll();
        var mix = new float[Chunk * 2];
        for (var reads = 0; reads < 16 && !waiter.IsCompleted; reads++)
        {
            bus.ReadInto(mix);
            await Task.WhenAny(waiter, Task.Delay(20));
        }
        Assert.True(await waiter.WaitAsync(TimeSpan.FromSeconds(5)));

        producer.Dispose();
    }

    private sealed class SilenceSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format => fmt;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> destination)
        {
            destination.Clear();
            return destination.Length;
        }
    }
}
