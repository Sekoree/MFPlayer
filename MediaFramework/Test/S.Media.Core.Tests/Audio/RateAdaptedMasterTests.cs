using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// A rate-adapted output must never become the router's pacing master.
/// </summary>
/// <remarks>
/// The bay already refuses a foreign-rate clock master explicitly. This closes the other door: a router
/// AUTO-PROMOTES the first clocked output it sees, and a resampling wrapper implements the clock
/// interfaces faithfully - so it looks like a perfectly good candidate while reporting the device's
/// latency rather than the converter's own buffered delay. Pacing from it times the whole programme
/// against a clock that is silently early.
/// </remarks>
public class RateAdaptedMasterTests
{
    private const int Rate = 48_000;

    private sealed class ClockedSink(AudioFormat fmt) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => true;
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => true;
        public long EpochId => 1;
        public ClockReading Read() => new(EpochId, TimeSpan.Zero, IsAdvancing);
    }

    /// <summary>Models the shape of a resampling wrapper: clocked, plausible, and rate-adapted.</summary>
    private sealed class RateAdaptedSink(AudioFormat fmt)
        : IAudioOutput, IClockedOutput, IPlaybackClock, IRateAdaptedOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => true;
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => true;
        public long EpochId => 1;
        public ClockReading Read() => new(EpochId, TimeSpan.Zero, IsAdvancing);
    }

    [Fact]
    public void APlainClockedOutput_IsPromoted()
    {
        using var router = new AudioRouter(Rate);

        router.AddOutput(new ClockedSink(new AudioFormat(Rate, 2)), "device");

        Assert.Equal("device", router.PrimaryOutputId);
    }

    [Fact]
    public void ARateAdaptedOutput_IsNeverPromoted()
    {
        using var router = new AudioRouter(Rate);

        router.AddOutput(new RateAdaptedSink(new AudioFormat(Rate, 2)), "resampled");

        // It stays attached and audible - it simply does not get to define time.
        Assert.Null(router.PrimaryOutputId);
    }

    [Fact]
    public void APlainOutput_IsStillPromoted_WhenARateAdaptedOneWasAddedFirst()
    {
        using var router = new AudioRouter(Rate);

        router.AddOutput(new RateAdaptedSink(new AudioFormat(Rate, 2)), "resampled");
        router.AddOutput(new ClockedSink(new AudioFormat(Rate, 2)), "device");

        // The resampled line must not "use up" the primary slot and leave the router wall-clock paced
        // when a perfectly good native device is present.
        Assert.Equal("device", router.PrimaryOutputId);
    }
}
