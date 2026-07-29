using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// The client lease's <see cref="IPlaybackClock"/>: terminal device clock minus a per-client epoch
/// captured at attach and minus the client's DAC lead (bus backlog + mixer pump + terminal
/// submit-to-speaker latency). The fake terminal advances only when told to, so the terminal side is
/// deterministic; the small tolerance absorbs the live mixer's own pump occupancy (at most
/// <c>pumpCapacityChunks × chunkSamples</c>, ~2.7 ms with the sizes used here).
/// </summary>
public sealed class SharedAudioOutputPlaybackClockTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);
    /// <summary>Pump occupancy is the only nondeterministic term at these sizes (2 chunks of 64 @48k).</summary>
    private const double PumpToleranceMs = 5;

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
        AssertMillisecondsNear(500, clock.ElapsedSinceStart);

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

        AssertMillisecondsNear(300, ((IPlaybackClock)first.Output).ElapsedSinceStart);
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
        AssertMillisecondsNear(200, clock.ElapsedSinceStart);

        ((IFlushableOutput)lease.Output).Flush();
        Assert.Equal(TimeSpan.Zero, clock.ElapsedSinceStart);

        terminal.Advance(TimeSpan.FromMilliseconds(100));
        AssertMillisecondsNear(100, clock.ElapsedSinceStart);
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

    // --- DAC-lead compensation ---------------------------------------------

    [Fact]
    public void SteadyState_SubtractsTheTerminalsReportedLatency()
    {
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(100) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        // Past the 2×latency startup window, so the full subtraction applies.
        terminal.Advance(TimeSpan.FromSeconds(1));
        AssertMillisecondsNear(900, clock.ElapsedSinceStart);
    }

    [Fact]
    public void StartupWindow_EasesTheSubtractionIn_MonotonicallyFromZero()
    {
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(100) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        // elapsed²/(4×latency): 0.1² / 0.4 = 25 ms at the window's midpoint...
        terminal.Advance(TimeSpan.FromMilliseconds(100));
        var quarterWindow = clock.ElapsedSinceStart;
        AssertMillisecondsNear(25, quarterWindow);

        // ...and elapsed − latency at the window edge (2×latency), where the two branches meet.
        terminal.Advance(TimeSpan.FromMilliseconds(100));
        var windowEdge = clock.ElapsedSinceStart;
        AssertMillisecondsNear(100, windowEdge);
        Assert.True(windowEdge > quarterWindow, "ease-in must be strictly increasing");

        // The eased value never overstates the audible position (it stays under the raw reading).
        Assert.True(windowEdge < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void GrowingLatencyEstimate_HoldsTheClock_InsteadOfSteppingBack()
    {
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(20) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromSeconds(1));
        var beforeChange = clock.ElapsedSinceStart;
        AssertMillisecondsNear(980, beforeChange);

        // A device re-negotiating a much deeper buffer would step the raw estimate by 280 ms.
        terminal.SubmitToOutputLatency = TimeSpan.FromMilliseconds(300);
        Assert.Equal(beforeChange, clock.ElapsedSinceStart);

        // Held, not frozen: the clock resumes once the raw reading passes the high-water mark.
        terminal.Advance(TimeSpan.FromMilliseconds(500));
        AssertMillisecondsNear(1200, clock.ElapsedSinceStart);
    }

    [Fact]
    public void TerminalReanchor_ResumesFromHighWater_WithCompensationStillMonotonic()
    {
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(50) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromSeconds(2));
        var beforeReanchor = clock.ElapsedSinceStart;
        AssertMillisecondsNear(1950, beforeReanchor);

        // Device stop/start (or an external flush) restarts the terminal clock at zero.
        terminal.Reanchor();
        Assert.True(clock.ElapsedSinceStart >= beforeReanchor, "client clock stepped back across a terminal re-anchor");

        terminal.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(clock.ElapsedSinceStart >= beforeReanchor);
    }

    [Fact]
    public void UnconsumedClientBacklog_CountsTowardTheLead()
    {
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2,
            clientBufferDuration: TimeSpan.FromSeconds(2), clientTargetQueueChunks: 4);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        // 400 ms of audio the mixer has not consumed yet: the client produced that far ahead of the
        // speaker, so its clock must report that much less than the raw terminal elapsed. The mixer
        // drains in real time, so only the lower bound of the subtraction is asserted precisely.
        terminal.Advance(TimeSpan.FromSeconds(4));
        lease.Output.Submit(new float[48_000 * 2 * 400 / 1000]);
        var elapsed = clock.ElapsedSinceStart;

        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(3550), TimeSpan.FromMilliseconds(3950));
    }

    [Fact]
    public void TerminalWithoutLatencyReport_StillReportsTheRawTerminalElapsed()
    {
        // No IAudioOutputLatency (e.g. an NDI terminal): only the measurable in-process depths count,
        // which are ~0 for an idle client - the clock must not invent a lead.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromMilliseconds(750));
        AssertMillisecondsNear(750, clock.ElapsedSinceStart);
    }

    private static void AssertMillisecondsNear(double expectedMs, TimeSpan actual) =>
        Assert.InRange(actual.TotalMilliseconds, expectedMs - PumpToleranceMs, expectedMs + PumpToleranceMs);

    // --- fakes -------------------------------------------------------------

    /// <summary>Terminal whose device clock advances only when the test says so, with a settable
    /// submit-to-speaker latency (the surface the hardware backends report).</summary>
    private sealed class FakeClockedTerminal(AudioFormat format) : IAudioOutput, IPlaybackClock, IAudioOutputLatency
    {
        private long _elapsedTicks;
        private long _latencyTicks;
        private volatile bool _advancing = true;

        public AudioFormat Format { get; } = format;
        public TimeSpan ElapsedSinceStart => new(Interlocked.Read(ref _elapsedTicks));
        public bool IsAdvancing => _advancing;

        public TimeSpan SubmitToOutputLatency
        {
            get => new(Interlocked.Read(ref _latencyTicks));
            set => Interlocked.Exchange(ref _latencyTicks, value.Ticks);
        }

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);
        /// <summary>Device stop/start: the terminal clock restarts at zero under the client's epoch.</summary>
        public void Reanchor() => Interlocked.Exchange(ref _elapsedTicks, 0);
        public void SetAdvancing(bool advancing) => _advancing = advancing;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class PlainTerminal(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }
}
