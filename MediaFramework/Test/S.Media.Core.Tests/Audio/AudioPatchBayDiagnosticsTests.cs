using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The bay's diagnostics snapshot. Two gaps drove this: the pump counters existed but the session-level
/// query threw most of them away, and the <b>input</b> side had no counters exposed at all - so "which
/// lease is starving the bus" could not be answered.
/// </summary>
public class AudioPatchBayDiagnosticsTests
{
    private const int Rate = 48_000;

    private static float[,] Identity() => new float[,] { { 1f, 0f }, { 0f, 1f } };

    private sealed class Sink(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
    }

    [Fact]
    public void ReportsMixTopology_AndTheClockMaster()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);
        bay.AddTerminal("stream", new Sink(new AudioFormat(Rate, 2)), Identity());

        var snapshot = bay.SnapshotDiagnostics();

        Assert.Equal(Rate, snapshot.MixSampleRate);
        Assert.Equal(2, snapshot.LogicalChannels);
        Assert.Equal("main", snapshot.ClockMasterTerminalId);
        Assert.Equal(2, snapshot.Terminals.Count);

        var master = Assert.Single(snapshot.Terminals, t => t.IsClockMaster);
        Assert.Equal("main", master.TerminalId);
        Assert.Equal(TerminalState.AdvancingMaster, master.State);

        var other = Assert.Single(snapshot.Terminals, t => !t.IsClockMaster);
        Assert.Equal(TerminalState.Open, other.State);
    }

    [Fact]
    public void ReportsTerminalFormat_IncludingAResampledLine()
    {
        using var bay = new AudioPatchBay(
            logicalChannels: 2, Rate,
            resamplerFactory: (inner, format) => new ResampleStub(inner, format));
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);
        bay.AddTerminal("record", new Sink(new AudioFormat(44_100, 2)), Identity());

        var snapshot = bay.SnapshotDiagnostics();

        // The device's OWN rate is reported, so an operator can see which lines are being resampled -
        // and therefore which can never be the clock master.
        var record = Assert.Single(snapshot.Terminals, t => t.TerminalId == "record");
        Assert.Equal(44_100, record.NativeSampleRate);
        Assert.False(record.IsClockMaster);
    }

    [Fact]
    public void ReportsEveryProducerLease_WithInputSideCounters()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);

        using var a = bay.AcquireProducer(1, [1f, 1f], label: "Q12 Preshow bed");
        using var b = bay.AcquireProducer(1, [1f, 0f], label: "Q13.1 Storm bed");
        a.Submit(new float[480]);

        var snapshot = bay.SnapshotDiagnostics();

        Assert.Equal(2, snapshot.Producers.Count);
        // The label is the whole reason this row is actionable: "a lease is starving the bus" is not a
        // statement anyone can act on without knowing which cue it is.
        var preshow = Assert.Single(snapshot.Producers, p => p.Label == "Q12 Preshow bed");
        Assert.Equal(480, preshow.BufferedFrames);
        Assert.Equal(0, preshow.OverflowFloats);
        Assert.Contains(snapshot.Producers, p => p.Label == "Q13.1 Storm bed");
    }

    [Fact]
    public void ProducerOverflow_IsCounted_SoAStarvingLeaseIsIdentifiable()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate, producerRingFrames: 480);
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);
        using var voice = bay.AcquireProducer(1, [1f, 1f], label: "greedy");

        // Submit far more than the ring holds without the bay running, so nothing drains it.
        for (var i = 0; i < 10; i++)
            voice.Submit(new float[480]);

        var producer = Assert.Single(bay.SnapshotDiagnostics().Producers);
        Assert.Equal("greedy", producer.Label);
        Assert.True(producer.OverflowFloats > 0, "an overflowing lease reported no overflow");
    }

    [Fact]
    public void CarriesTheFullPumpCounters_NotJustTwoOfThem()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);

        var terminal = Assert.Single(bay.SnapshotDiagnostics().Terminals);

        // The historical session query returned only Enqueued and Dropped, discarding the rest; the
        // diagnostics screen needs capacity and in-flight to show pressure before anything is lost.
        Assert.True(terminal.Stats.PumpCapacityChunks > 0);
        Assert.InRange(terminal.InFlight, 0, terminal.Stats.PumpCapacityChunks);
        Assert.False(terminal.Stats.IsStuck);
    }

    [Fact]
    public void IncludesProgramLevels_OnlyWhenMeteringIsEnabled()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", new NullClockedAudioOutput(new AudioFormat(Rate, 2)), Identity(),
            isClockMaster: true);

        Assert.Empty(bay.SnapshotDiagnostics().ChannelLevels);

        bay.EnableProgramMetering();

        Assert.Equal(2, bay.SnapshotDiagnostics().ChannelLevels.Count);
    }

    [Fact]
    public void WithNoClockMaster_ReportsNull_RatherThanInventingOne()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("stream", new Sink(new AudioFormat(Rate, 2)), Identity());

        var snapshot = bay.SnapshotDiagnostics();

        Assert.Null(snapshot.ClockMasterTerminalId);
        Assert.All(snapshot.Terminals, t => Assert.False(t.IsClockMaster));
    }

    private sealed class ResampleStub(IAudioOutput inner, AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format => format;
        public void Submit(ReadOnlySpan<float> samples) => inner.Submit(samples);
    }
}
