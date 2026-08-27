using S.Media.Routing;
using S.Media.Time;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// A masterless bay is the normal state of every new show (a fresh project has logical outputs but
/// no audio line), so reading its clocks must be exception-free: the extracted client clock rides
/// its wall-clock fallback off a null provider answer instead of catching a throw per read - the
/// throwing MasterClockProxy put a first-chance InvalidOperationException on every Reanchor/read of
/// a brand-new project.
/// </summary>
public sealed class MasterlessBayClockTests
{
    private const int Rate = 48_000;

    [Fact]
    public void MasterlessBay_MasterClockReadsAdvanceWithoutThrowing()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);

        var first = bay.MasterClock.Read();
        Assert.True(first.IsAdvancing, "the wall-clock fallback domain always advances");

        Thread.Sleep(30);
        var second = bay.MasterClock.Read();
        Assert.Equal(first.EpochId, second.EpochId);
        Assert.True(second.Elapsed > first.Elapsed, "the fallback domain must actually advance");
    }

    [Fact]
    public void MasterlessBay_ProducerClockReadsAdvanceWithoutThrowing()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        using var producer = bay.AcquireProducer(sourceChannels: 2, [1f, 0f, 0f, 1f]);

        var first = producer.Read();
        Assert.True(first.IsAdvancing);

        Thread.Sleep(30);
        Assert.True(producer.Read().Elapsed > first.Elapsed);
    }

    [Fact]
    public void NullProvider_ClientClockRidesTheFallbackAndAdoptsALateTerminal()
    {
        ManualTerminalClock? terminal = null;
        // The provider answers null until a "master" appears - the bay's exact late-bound shape.
        var clock = new AudibleClientClock(() => (IPlaybackClock?)terminal, static () => 0);

        var fallback = clock.Read();
        Assert.True(fallback.IsAdvancing, "no terminal = wall-clock fallback, not a fault");

        terminal = new ManualTerminalClock { Elapsed = TimeSpan.FromMilliseconds(50) };
        // The first successful terminal read carries an unseen epoch id, which IS the recovery path;
        // the report stays monotonic across the adoption.
        var adopted = clock.Read();
        Assert.True(adopted.Elapsed >= fallback.Elapsed, "adoption must not step the clock backwards");
        Assert.True(clock.IsAdvancing);
    }

    private sealed class ManualTerminalClock : IPlaybackClock
    {
        public TimeSpan Elapsed { get; set; }
        public TimeSpan ElapsedSinceStart => Elapsed;
        public long EpochId => 1;
        public bool IsAdvancing => true;
        public ClockReading Read() => new(EpochId, Elapsed, IsAdvancing);
    }
}
