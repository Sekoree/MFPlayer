using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The program-sum bus (HaCue plan, resolved decision 1): P producers × N×V sends summed into one
/// V-wide source per chunk. Covers the plan's routing-test bullets that concern the bus itself:
/// several producers mixing through the same logical channels, producer release removing only that
/// producer, click-free send updates, silence (never stale audio) on underrun, and the bounded
/// drop-oldest ring on overflow.
/// </summary>
public class ProgramBusSourceTests
{
    private const int Bus = 4;      // V
    private const int Rate = 48_000;
    private const int Frames = 480;

    private static float[] Sends(params (int Src, int BusCh, float Gain)[] cells)
    {
        // Layout gains[busChannel * sourceChannels + sourceChannel]; source width inferred by caller.
        var srcChannels = cells.Max(c => c.Src) + 1;
        var gains = new float[Bus * srcChannels];
        foreach (var (src, busCh, gain) in cells)
            gains[busCh * srcChannels + src] = gain;
        return gains;
    }

    private static float[] Constant(int frames, int channels, float value)
    {
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void TwoProducers_SumThroughTheirSends_IntoSharedAndSeparateChannels()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        using var stereo = bus.AcquireProducer(2, Sends((0, 0, 1f), (1, 1, 0.5f), (0, 2, 0.25f)));
        using var mono = bus.AcquireProducer(1, Sends((0, 2, 1f), (0, 3, 2f)));

        stereo.Submit(Constant(Frames, 2, 1f));
        mono.Submit(Constant(Frames, 1, 0.5f));

        var mix = new float[Frames * Bus];
        Assert.Equal(mix.Length, bus.ReadInto(mix));

        for (var f = 0; f < Frames; f++)
        {
            Assert.Equal(1f, mix[f * Bus + 0], 1e-4f);            // stereo L
            Assert.Equal(0.5f, mix[f * Bus + 1], 1e-4f);          // stereo R at -6ish
            Assert.Equal(0.25f + 0.5f, mix[f * Bus + 2], 1e-4f);  // SUM: stereo L send + mono send
            Assert.Equal(1f, mix[f * Bus + 3], 1e-4f);            // mono boosted
        }
    }

    [Fact]
    public void Underrun_ContributesSilence_NeverStaleAudio()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        using var producer = bus.AcquireProducer(1, Sends((0, 0, 1f)));

        producer.Submit(Constant(Frames / 2, 1, 1f)); // half a chunk only

        var mix = new float[Frames * Bus];
        bus.ReadInto(mix);
        for (var f = 0; f < Frames / 2; f++)
            Assert.Equal(1f, mix[f * Bus], 1e-4f);
        for (var f = Frames / 2; f < Frames; f++)
            Assert.Equal(0f, mix[f * Bus]); // tail is silence, not repeated audio

        // Next chunk with nothing buffered: all silence, and the shortfall is counted now that the
        // producer has been observed flowing.
        bus.ReadInto(mix);
        Assert.All(mix, s => Assert.Equal(0f, s));
        Assert.True(producer.UnderrunFloats > 0);
    }

    [Fact]
    public void UpdateSends_RampsOneChunk_ThenSettlesExactly()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        using var producer = bus.AcquireProducer(1, Sends((0, 0, 1f)));

        var mix = new float[Frames * Bus];
        producer.Submit(Constant(Frames, 1, 1f));
        bus.ReadInto(mix);
        Assert.Equal(1f, mix[0], 1e-4f);

        producer.UpdateSends(Sends((0, 0, 0.25f)));
        producer.Submit(Constant(Frames, 1, 1f));
        bus.ReadInto(mix);
        // Sample-mid interpolation: first sample ≈ old gain, last ≈ new gain, strictly between.
        Assert.InRange(mix[0], 0.25f, 1f);
        Assert.InRange(mix[(Frames - 1) * Bus], 0.25f, 1f);
        Assert.True(mix[0] > mix[(Frames - 1) * Bus], "ramp must move toward the new gain");

        producer.Submit(Constant(Frames, 1, 1f));
        bus.ReadInto(mix);
        for (var f = 0; f < Frames; f++)
            Assert.Equal(0.25f, mix[f * Bus], 1e-4f); // settled exactly at the target
    }

    [Fact]
    public void DisposeProducer_RemovesOnlyThatProducer()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        var a = bus.AcquireProducer(1, Sends((0, 0, 1f)));
        using var b = bus.AcquireProducer(1, Sends((0, 1, 1f)));
        Assert.Equal(2, bus.ProducerCount);

        a.Submit(Constant(Frames, 1, 1f));
        b.Submit(Constant(Frames, 1, 1f));
        a.Dispose();
        Assert.Equal(1, bus.ProducerCount);

        var mix = new float[Frames * Bus];
        bus.ReadInto(mix);
        Assert.Equal(0f, mix[0]);           // a's channel silent - even its buffered audio is gone
        Assert.Equal(1f, mix[1], 1e-4f);    // b unaffected
    }

    [Fact]
    public void Overflow_DropsOldestFrames_AndCounts()
    {
        // The ring enforces a 1024-float minimum, so fill exactly to capacity first, then push a
        // newer submission that MUST displace the oldest audio (live drop-oldest policy).
        var bus = new ProgramBusSource(Bus, Rate, producerRingFrames: Frames);
        using var producer = bus.AcquireProducer(1, Sends((0, 0, 1f)));

        producer.Submit(Constant(1, 1, 1f));
        var capacity = 1;
        while (true)
        {
            producer.Submit(Constant(1, 1, 1f));
            if (producer.BufferedFrames == capacity)
                break; // ring refused/dropped - it is full
            capacity = producer.BufferedFrames;
        }

        var before = producer.OverflowFloats;
        producer.Submit(Constant(Frames, 1, 2f)); // newer audio must displace the oldest
        Assert.True(producer.OverflowFloats > before);
        Assert.Equal(capacity, producer.BufferedFrames); // still bounded

        // Drain everything: the NEWEST submission's samples must be present at the tail.
        var drained = new List<float>();
        var mix = new float[Frames * Bus];
        for (var i = 0; i < 16 && producer.BufferedFrames > 0; i++)
        {
            bus.ReadInto(mix);
            for (var f = 0; f < Frames; f++)
                drained.Add(mix[f * Bus]);
        }

        Assert.Contains(2f, drained.Select(v => (float)Math.Round(v, 3)));
    }

    [Fact]
    public void Validation_RejectsWrongLengthAndNonFiniteSends()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        Assert.Throws<ArgumentException>(() => bus.AcquireProducer(2, new float[3]));
        var bad = new float[Bus];
        bad[1] = float.NaN;
        Assert.Throws<ArgumentException>(() => bus.AcquireProducer(1, bad));

        using var producer = bus.AcquireProducer(1, new float[Bus]);
        Assert.Throws<ArgumentException>(() => producer.UpdateSends(new float[Bus + 1]));
    }

    [Fact]
    public void EmptyBus_ReadsFullSilence_AndNeverExhausts()
    {
        var bus = new ProgramBusSource(Bus, Rate);
        var mix = new float[Frames * Bus];
        Array.Fill(mix, 123f);
        Assert.Equal(mix.Length, bus.ReadInto(mix));
        Assert.All(mix, s => Assert.Equal(0f, s));
        Assert.False(bus.IsExhausted);
    }

    /// <summary>The whole program-sum topology through a REAL router: two producer voices sum into
    /// the V-wide bus (P send passes), and the router fans the ONE bus source to two terminals with
    /// dense V×R patch matrices (R passes) - the P + R shape the patch design is built on.</summary>
    [Fact]
    public void EndToEnd_TwoProducers_ThroughRouterPatches_ReachTwoTerminals()
    {
        using var router = new AudioRouter(Rate);
        var bus = new ProgramBusSource(busChannels: 2, Rate);
        var house = new CapturingOutput(new AudioFormat(Rate, 2));
        var stream = new CapturingOutput(new AudioFormat(Rate, 2));
        var busId = router.AddSource(bus);
        var houseId = router.AddOutput(house);
        var streamId = router.AddOutput(stream);

        // House gets the bus as-is; the stream line gets a mono-ish half-sum on both channels.
        router.ApplyMatrix(busId, houseId, new float[,] { { 1f, 0f }, { 0f, 1f } });
        router.ApplyMatrix(busId, streamId, new float[,] { { 0.5f, 0.5f }, { 0.5f, 0.5f } });

        using var voiceL = bus.AcquireProducer(1, [1f, 0f]); // mono voice → bus L
        using var voiceR = bus.AcquireProducer(1, [0f, 1f]); // mono voice → bus R

        router.Play();
        var feeding = true;
        var feeder = new Thread(() =>
        {
            var chunk = Constant(Frames, 1, 1f);
            while (Volatile.Read(ref feeding))
            {
                voiceL.Submit(chunk);
                voiceR.Submit(chunk);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            // Startup fades in from silence; steady state must reach the patched levels:
            // house ≈ 1.0 per channel, stream ≈ 0.5 + 0.5 = 1.0 (the summing patch).
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline
                   && (house.MaxSample < 0.95f || stream.MaxSample < 0.95f))
                Thread.Sleep(10);
        }
        finally
        {
            Volatile.Write(ref feeding, false);
            feeder.Join(1000);
            router.Stop();
        }

        Assert.InRange(house.MaxSample, 0.95f, 1.05f);
        Assert.InRange(stream.MaxSample, 0.95f, 1.05f);
    }

    private sealed class CapturingOutput(AudioFormat fmt) : IAudioOutput
    {
        private float _maxSample;
        public AudioFormat Format => fmt;
        public float MaxSample => Volatile.Read(ref _maxSample);
        public void Submit(ReadOnlySpan<float> samples)
        {
            var max = Volatile.Read(ref _maxSample);
            foreach (var sample in samples)
            {
                if (sample > max)
                    max = sample;
            }
            Volatile.Write(ref _maxSample, max);
        }
    }
}
