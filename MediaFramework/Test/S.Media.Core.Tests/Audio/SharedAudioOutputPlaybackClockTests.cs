using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The client lease's <see cref="IPlaybackClock"/>: terminal device clock minus a per-client epoch
/// captured at attach and on flush. The fake terminal advances only when told to, so every
/// expectation here is deterministic (no wall-clock tolerance except the fallback test).
/// </summary>
public sealed class SharedAudioOutputPlaybackClockTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public void ClientClock_StartsAtZero_AndAdvancesWithTerminalConsumption()
    {
        var terminal = new FakeClockedTerminal(Stereo48k);
        terminal.Advance(TimeSpan.FromSeconds(1)); // device already ran before this client attached
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        Assert.Equal(TimeSpan.Zero, clock.ElapsedSinceStart);
        Assert.True(clock.IsAdvancing);

        terminal.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.ElapsedSinceStart);

        terminal.SetAdvancing(false);
        Assert.False(clock.IsAdvancing);
    }

    [Fact]
    public void SecondClient_AttachedLater_StillStartsAtZero()
    {
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var first = shared.Acquire();

        terminal.Advance(TimeSpan.FromMilliseconds(300));
        using var second = shared.Acquire();

        Assert.Equal(TimeSpan.FromMilliseconds(300), ((IPlaybackClock)first.Output).ElapsedSinceStart);
        Assert.Equal(TimeSpan.Zero, ((IPlaybackClock)second.Output).ElapsedSinceStart);
    }

    [Fact]
    public void Flush_ReanchorsTheClientClock()
    {
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.FromMilliseconds(200), clock.ElapsedSinceStart);

        ((IFlushableOutput)lease.Output).Flush();
        Assert.Equal(TimeSpan.Zero, clock.ElapsedSinceStart);

        terminal.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.ElapsedSinceStart);
    }

    [Fact]
    public void TerminalWithoutPlaybackClock_FallsBackToWallClock()
    {
        var terminal = new PlainTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        Assert.True(clock.IsAdvancing);
        Thread.Sleep(30);
        var beforeFlush = clock.ElapsedSinceStart;
        Assert.True(beforeFlush > TimeSpan.Zero, "fallback clock did not advance with wall time");

        ((IFlushableOutput)lease.Output).Flush();
        Assert.True(clock.ElapsedSinceStart < beforeFlush, "flush did not re-anchor the fallback clock");
    }

    [Fact]
    public void AudioRouter_PromotesClientLease_ToPrimaryAndMasterClock()
    {
        // The §1 finding: leases used to be IClockedOutput-only, so decks routed through a shared
        // output never mastered the MediaClock and playheads ran on Stopwatch. A client attached to
        // a STOPPED router must now be promoted with its playback clock.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();

        using var mediaClock = new MediaClock();
        using var router = new AudioRouter(Stereo48k.SampleRate, chunkSamples: 480);
        router.AttachMasterClock(mediaClock);
        router.AddOutput(lease.Output, "client");

        Assert.Equal("client", router.PrimaryOutputId);
        Assert.Same(lease.Output, mediaClock.Master);
    }

    // --- fakes -------------------------------------------------------------

    /// <summary>Terminal whose device clock advances only when the test says so.</summary>
    private sealed class FakeClockedTerminal(AudioFormat format) : IAudioOutput, IPlaybackClock
    {
        private long _elapsedTicks;
        private volatile bool _advancing = true;

        public AudioFormat Format { get; } = format;
        public TimeSpan ElapsedSinceStart => new(Interlocked.Read(ref _elapsedTicks));
        public bool IsAdvancing => _advancing;

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
        public void SetAdvancing(bool advancing) => _advancing = advancing;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class PlainTerminal(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }
}
