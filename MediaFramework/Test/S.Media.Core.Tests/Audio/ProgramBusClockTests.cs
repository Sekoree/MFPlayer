using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Producer-lease clocks on the program bus (HaCue plan, "Clock policy"): a lease implements
/// IClockedOutput/IPlaybackClock/IAudioOutputLatency, its clock is the MASTER terminal's audible
/// clock rebased to the producer (zero at acquire, zero again at Flush with a fresh epoch id, never
/// backwards), reduced by the lead still between the producer and the speaker; with no master the
/// clock degrades to the advancing wall-clock fallback - all behaviors inherited from the
/// AudibleClientClock extracted out of SharedAudioOutput.ClientInput and covered in depth by
/// SharedAudioOutputPlaybackClockTests. These tests pin the bus/bay WIRING of that machinery.
/// </summary>
public class ProgramBusClockTests
{
    private const int Rate = 48_000;
    private const int Frames = 480;

    private static float[] Chunk(int channels, float value)
    {
        var samples = new float[Frames * channels];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void ProducerClock_StartsAtZero_AndAdvancesWithTheMasterTerminalClock()
    {
        var master = new FakeTerminalClock();
        var bus = new ProgramBusSource(2, Rate, clockContext: new ProgramBusClockContext(master, static () => 0));
        using var producer = bus.AcquireProducer(1, [1f, 0f]);

        Assert.Equal(TimeSpan.Zero, producer.ElapsedSinceStart);

        master.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1.0, producer.ElapsedSinceStart.TotalSeconds, 2);
        Assert.True(producer.IsAdvancing);
    }

    [Fact]
    public void SecondProducer_AcquiredLater_StillStartsAtZero()
    {
        var master = new FakeTerminalClock();
        var bus = new ProgramBusSource(2, Rate, clockContext: new ProgramBusClockContext(master, static () => 0));
        using var first = bus.AcquireProducer(1, [1f, 0f]);
        master.Advance(TimeSpan.FromSeconds(5));

        using var second = bus.AcquireProducer(1, [0f, 1f]);
        Assert.Equal(TimeSpan.Zero, second.ElapsedSinceStart);
        Assert.Equal(5.0, first.ElapsedSinceStart.TotalSeconds, 2);

        master.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1.0, second.ElapsedSinceStart.TotalSeconds, 2);
    }

    [Fact]
    public void Flush_ReanchorsTheProducerClock_WithAFreshEpoch_AndNoBackwardsRead()
    {
        var master = new FakeTerminalClock();
        var bus = new ProgramBusSource(2, Rate, clockContext: new ProgramBusClockContext(master, static () => 0));
        using var producer = bus.AcquireProducer(1, [1f, 0f]);

        master.Advance(TimeSpan.FromSeconds(3));
        var beforeFlush = producer.Read();
        Assert.Equal(3.0, beforeFlush.Elapsed.TotalSeconds, 2);

        producer.Flush();
        var afterFlush = producer.Read();
        Assert.NotEqual(beforeFlush.EpochId, afterFlush.EpochId); // a NEW epoch, not a backwards read
        Assert.Equal(TimeSpan.Zero, afterFlush.Elapsed);

        master.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(0.5, producer.ElapsedSinceStart.TotalSeconds, 2);
    }

    [Fact]
    public void ProducerLead_CountsRingBacklogAndTheBaysDownstreamMeasurement()
    {
        long downstream = 0;
        var master = new FakeTerminalClock();
        var bus = new ProgramBusSource(
            2, Rate, clockContext: new ProgramBusClockContext(master, () => Volatile.Read(ref downstream)));
        using var producer = bus.AcquireProducer(1, [1f, 0f]);

        producer.Submit(Chunk(1, 1f)); // 480 frames = 10 ms of ring backlog
        Volatile.Write(ref downstream, TimeSpan.FromMilliseconds(20).Ticks);

        Assert.Equal(30.0, producer.SubmitToOutputLatency.TotalMilliseconds, 1);
    }

    [Fact]
    public void WithoutAClockContext_TheFallbackClockStillAdvances()
    {
        var bus = new ProgramBusSource(2, Rate);
        using var producer = bus.AcquireProducer(1, [1f, 0f]);

        var first = producer.ElapsedSinceStart;
        Thread.Sleep(50);
        Assert.True(producer.ElapsedSinceStart > first, "headless producer clock must advance");
        Assert.True(producer.IsAdvancing);
    }

    [Fact]
    public async Task WaitForCapacity_BlocksWhenFull_AndWakesOnBusConsumption()
    {
        var bus = new ProgramBusSource(1, Rate, producerRingFrames: Frames * 2);
        using var producer = bus.AcquireProducer(1, [1f]);

        // Fill the ring to capacity: buffered frames plateau once drop-oldest kicks in.
        producer.Submit(Chunk(1, 1f));
        var buffered = producer.BufferedFrames;
        while (true)
        {
            producer.Submit(Chunk(1, 1f));
            if (producer.BufferedFrames == buffered)
                break;
            buffered = producer.BufferedFrames;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiter = Task.Run(() => producer.WaitForCapacity(Frames, cts.Token));
        Assert.NotSame(waiter, await Task.WhenAny(waiter, Task.Delay(100))); // full - must block

        // Consumption is what opens capacity. It takes more than one chunk here because pacing targets
        // HALF the ring, keeping the rest as headroom (see WaitForCapacity) - a producer let go the
        // instant a single chunk drains would sit pinned at capacity, one hiccup from dropping audio.
        var mix = new float[Frames * 1];
        for (var reads = 0; reads < 8 && !waiter.IsCompleted; reads++)
        {
            bus.ReadInto(mix);
            await Task.WhenAny(waiter, Task.Delay(20));
        }
        Assert.True(await waiter.WaitAsync(TimeSpan.FromSeconds(5)));

        producer.Dispose();
        Assert.False(producer.WaitForCapacity(Frames, CancellationToken.None)); // disposed = never ready
    }

    /// <summary>The whole wiring through a REAL bay: the headless clocked discard output is the
    /// clock master (the plan's video-only-project path), a fed producer's clock advances with
    /// actually-consumed audio, and removing the master degrades the clock to the advancing
    /// fallback instead of faulting the producer.</summary>
    [Fact]
    public void EndToEnd_BayProducerClock_FollowsTheMaster_AndSurvivesMasterRemoval()
    {
        using var bay = new AudioPatchBay(2, Rate);
        var master = new NullClockedAudioOutput(new AudioFormat(Rate, 2));
        master.Start(); // bay terminals are BORROWED, already-running devices (the host lease starts them)
        bay.AddTerminal("null-master", master, new float[,] { { 1f, 0f }, { 0f, 1f } }, isClockMaster: true);

        using var producer = bay.AcquireProducer(1, [1f, 0f]);
        Assert.Equal(TimeSpan.Zero, producer.ElapsedSinceStart);

        bay.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Chunk(1, 0.5f);
            while (Volatile.Read(ref feeding))
            {
                producer.Submit(chunk);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline
                   && producer.ElapsedSinceStart < TimeSpan.FromMilliseconds(200))
                Thread.Sleep(10);
            Assert.True(producer.ElapsedSinceStart >= TimeSpan.FromMilliseconds(200),
                "producer clock never advanced against the bay's master");

            // Master loss: visible at the bay (router fault path) but the producer CLOCK degrades
            // to the wall-clock fallback and keeps advancing - readers never throw, never regress.
            var beforeRemoval = producer.ElapsedSinceStart;
            Assert.True(bay.RemoveTerminal("null-master"));
            Thread.Sleep(100);
            var afterRemoval = producer.ElapsedSinceStart;
            Assert.True(afterRemoval >= beforeRemoval, "clock must never step backwards on master loss");
            Assert.True(producer.IsAdvancing);
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            bay.Stop();
        }
    }

    /// <summary>
    /// A pre-roll released before the ring reached its pacing target must not carry the shortfall
    /// as a readout offset: the ring keeps topping up to the target after release, and a baseline
    /// taken from the instantaneous fill would count that top-up as lead forever (one stem of a
    /// batch read −95 ms beside its siblings). The baseline is normalized to the pacing target, so
    /// once the ring gets there the clock reads exactly the master's advance.
    /// </summary>
    [Fact]
    public void PreRollReleasedMidFill_ReadsNoPermanentOffset_OnceTheRingTopsUp()
    {
        var master = new FakeTerminalClock();
        var bus = new ProgramBusSource(
            1, Rate,
            clockContext: new ProgramBusClockContext(master, static () => 0),
            pacingTargetFrames: 4800);
        using var producer = bus.AcquireProducer(1, [1f]);

        producer.BeginPreRoll();
        producer.Submit(new float[480]); // the edge arrives with only a tenth of the target filled
        producer.EndPreRoll();

        producer.Submit(new float[4320]); // production tops the ring up to the target post-release

        master.Advance(TimeSpan.FromSeconds(1));
        // Old baseline (the 480-frame fill at release) turned the 4320-frame top-up into 90 ms of
        // permanent lead: this read 0.91 s.
        Assert.Equal(1.0, producer.ElapsedSinceStart.TotalSeconds, 2);
    }

    private sealed class FakeTerminalClock : IPlaybackClock
    {
        private long _elapsedTicks;
        private long _epochId = 1;

        public TimeSpan ElapsedSinceStart => new(Volatile.Read(ref _elapsedTicks));
        public long EpochId => Volatile.Read(ref _epochId);
        public bool IsAdvancing => true;
        public ClockReading Read() => new(EpochId, ElapsedSinceStart, IsAdvancing);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
    }
}
