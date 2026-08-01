using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Level metering of the V-wide program bus. The behaviours worth pinning are the ones that make this
/// meter usable where the app-side one was not: reads that do not consume, a peak that decays on the
/// audio thread (so a slow observer cannot miss a transient), and per-channel isolation.
/// </summary>
public class ProgramBusMeterTests
{
    private const int SampleRate = 48_000;

    /// <summary>Fills one chunk of interleaved audio with a constant magnitude on one channel.</summary>
    private static float[] Chunk(int channels, int frames, int channel, float value)
    {
        var buffer = new float[channels * frames];
        for (var frame = 0; frame < frames; frame++)
            buffer[frame * channels + channel] = value;
        return buffer;
    }

    [Fact]
    public void FullScaleSignal_ReadsZeroDbFs_OnItsOwnChannelOnly()
    {
        var meter = new ProgramBusMeter(4, SampleRate);

        meter.Observe(Chunk(4, 480, channel: 2, value: 1f), 480);
        var levels = meter.Snapshot();

        Assert.Equal(0f, levels[2].PeakDb, 1);
        // Every other channel stays at the silence floor - a meter that leaks between channels is
        // worse than no meter, because it looks plausible.
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[0].PeakDb);
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[1].PeakDb);
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[3].PeakDb);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0.5f, -6.02f)]
    [InlineData(0.1f, -20f)]
    [InlineData(0.001f, -60f)]
    public void Peak_IsReportedInDbFs(float amplitude, float expectedDb)
    {
        var meter = new ProgramBusMeter(1, SampleRate);

        meter.Observe(Chunk(1, 64, channel: 0, value: amplitude), 64);

        Assert.Equal(expectedDb, meter.Snapshot()[0].PeakDb, 1);
    }

    [Fact]
    public void Silence_ReadsTheFloor_NotNegativeInfinity()
    {
        var meter = new ProgramBusMeter(2, SampleRate);

        meter.Observe(new float[2 * 480], 480);
        var levels = meter.Snapshot();

        // -inf would format as "-∞" in a UI and breaks any dB→bar-height arithmetic.
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[0].PeakDb);
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[0].RmsDb);
        Assert.False(float.IsNegativeInfinity(levels[0].PeakDb));
    }

    [Fact]
    public void Snapshot_IsNonDestructive_SoTwoConsumersSeeTheSameValue()
    {
        var meter = new ProgramBusMeter(1, SampleRate);
        meter.Observe(Chunk(1, 480, channel: 0, value: 0.5f), 480);

        var first = meter.Snapshot()[0];
        var second = meter.Snapshot()[0];

        // The app-side meter this replaces used read-and-reset, so whichever of the output strip and
        // the diagnostics window polled second saw silence.
        Assert.Equal(first.PeakDb, second.PeakDb, 4);
    }

    [Fact]
    public void Peak_DecaysOverTime_AtRoughlyTheStatedRate()
    {
        var meter = new ProgramBusMeter(1, SampleRate);
        meter.Observe(Chunk(1, 48, channel: 0, value: 1f), 48);
        Assert.Equal(0f, meter.Snapshot()[0].PeakDb, 1);

        // Exactly one second of silence, in chunks, should drop the peak by the stated decay rate.
        for (var i = 0; i < SampleRate / 480; i++)
            meter.Observe(new float[480], 480);

        var afterOneSecond = meter.Snapshot()[0].PeakDb;
        Assert.InRange(afterOneSecond, -ProgramBusMeter.PeakDecayDbPerSecond - 1.5f,
            -ProgramBusMeter.PeakDecayDbPerSecond + 1.5f);
    }

    [Fact]
    public void Peak_IsHeldAcrossChunks_SoASlowObserverCannotMissATransient()
    {
        var meter = new ProgramBusMeter(1, SampleRate);

        // A single loud chunk followed by quiet ones, all within one UI frame (~33 ms at 30 Hz).
        meter.Observe(Chunk(1, 480, channel: 0, value: 1f), 480);
        for (var i = 0; i < 3; i++)
            meter.Observe(Chunk(1, 480, channel: 0, value: 0.001f), 480);

        // The transient is still visible: decay over 40 ms is well under a dB, so an observer that
        // polls once per UI frame still sees essentially full scale.
        Assert.InRange(meter.Snapshot()[0].PeakDb, -1.5f, 0.01f);
    }

    [Fact]
    public void Rms_ApproachesSignalPower_AndSitsBelowPeakForATone()
    {
        var meter = new ProgramBusMeter(1, SampleRate);

        // One second of a full-scale sine: RMS of a sine is -3.01 dB relative to its peak.
        const int frames = 480;
        var chunk = new float[frames];
        var phase = 0d;
        var step = 2d * Math.PI * 1000d / SampleRate;
        for (var chunkIndex = 0; chunkIndex < SampleRate / frames; chunkIndex++)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                chunk[frame] = (float)Math.Sin(phase);
                phase += step;
            }
            meter.Observe(chunk, frames);
        }

        var level = meter.Snapshot()[0];
        Assert.InRange(level.RmsDb, -4.5f, -1.5f);
        Assert.True(level.RmsDb < level.PeakDb, "RMS of a tone must sit below its peak");
    }

    [Fact]
    public void Clip_LatchesUntilReset()
    {
        var meter = new ProgramBusMeter(2, SampleRate);

        meter.Observe(Chunk(2, 64, channel: 0, value: 1.5f), 64);
        Assert.True(meter.Snapshot()[0].Clipped);
        Assert.False(meter.Snapshot()[1].Clipped);

        // Still latched after the offending audio is long gone - that is the point of a clip light.
        for (var i = 0; i < 100; i++)
            meter.Observe(new float[2 * 480], 480);
        Assert.True(meter.Snapshot()[0].Clipped);

        meter.ResetClip();
        Assert.False(meter.Snapshot()[0].Clipped);
    }

    [Fact]
    public void Meter_MustMatchTheBusWidth()
    {
        using var bus = new ProgramBusSource(busChannels: 4, sampleRate: SampleRate);

        Assert.Throws<ArgumentException>(() => bus.Meter = new ProgramBusMeter(2, SampleRate));
    }

    [Fact]
    public void AttachedToABus_MetersTheProgramSum_OfEveryProducer()
    {
        using var bus = new ProgramBusSource(busChannels: 2, sampleRate: SampleRate);
        var meter = new ProgramBusMeter(2, SampleRate);
        bus.Meter = meter;

        // Two mono producers at half scale, both sending to logical channel 0: the SUM is what the
        // meter must show (0.5 + 0.5 = full scale), because that is what the terminals receive.
        var sendsToChannel0 = new float[] { 1f, 0f }; // gains[busChannel * sourceChannels + source]
        using var a = bus.AcquireProducer(sourceChannels: 1, sendsToChannel0);
        using var b = bus.AcquireProducer(sourceChannels: 1, sendsToChannel0);

        var half = new float[480];
        Array.Fill(half, 0.5f);
        a.Submit(half);
        b.Submit(half);

        var destination = new float[480 * 2];
        bus.ReadInto(destination);

        var levels = meter.Snapshot();
        Assert.Equal(0f, levels[0].PeakDb, 1);
        Assert.Equal(ProgramBusMeter.SilenceDb, levels[1].PeakDb);
    }

    [Fact]
    public void WithoutAMeter_TheBusStillReads_AndCostsNothing()
    {
        using var bus = new ProgramBusSource(busChannels: 2, sampleRate: SampleRate);
        Assert.Null(bus.Meter);

        var destination = new float[480 * 2];
        Assert.Equal(destination.Length, bus.ReadInto(destination));
    }
}
