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

        // A device re-negotiating a much deeper buffer would step the raw estimate by 280 ms. The lead
        // filter integrates over the RAW clock, so a re-read without clock progress cannot move it either.
        terminal.SubmitToOutputLatency = TimeSpan.FromMilliseconds(300);
        Assert.Equal(beforeChange, clock.ElapsedSinceStart);

        // Held, not frozen: the clock resumes once the raw reading passes the high-water mark. The new
        // lead is tracked over the filter's time constant rather than adopted in one step, so this
        // intermediate read sits between the old and the new steady state.
        terminal.Advance(TimeSpan.FromMilliseconds(500));
        var midway = clock.ElapsedSinceStart;
        Assert.True(midway >= beforeChange, "client clock stepped back while the lead estimate grew");
        Assert.InRange(midway, TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1480));

        // Converged: several time constants later the full new lead is subtracted.
        terminal.Advance(TimeSpan.FromSeconds(2));
        AssertMillisecondsNear(3200, clock.ElapsedSinceStart);
    }

    [Fact]
    public void FluctuatingLead_SubtractsTheMeanLead_NotTheMinimumOfTheWindow()
    {
        // The C1 defect: both lead terms are instantaneous queue depths (the pump cycles over its whole
        // capacity every chunk; the client bus sawtooths between its reservoir and empty), and feeding an
        // instantaneous depth into the monotonic high-water makes the report max(raw − lead) ≈
        // raw − MIN(lead) over a trailing window. Here the lead alternates 40/120 ms, so an unsmoothed
        // estimate reports ~raw − 40 and keeps ~40 ms of the DAC lead C1 exists to remove.
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(80) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        for (var step = 0; step < 300; step++)
        {
            terminal.SubmitToOutputLatency = TimeSpan.FromMilliseconds(step % 2 == 0 ? 40 : 120);
            terminal.Advance(TimeSpan.FromMilliseconds(10));
            _ = clock.ElapsedSinceStart;
        }

        // 3000 ms raw, mean lead 80 ms.
        AssertMillisecondsNear(2920, clock.ElapsedSinceStart, toleranceMs: 10);
    }

    [Fact]
    public void LeadThatDipsAndRecovers_KeepsTheClockAdvancing_InsteadOfFreezingForTheDip()
    {
        // Second-order form of the same defect: an under-run (or any transient dip in the measured lead)
        // ratchets the high-water forward by the size of the dip, and the clock then FREEZES until the raw
        // reading catches back up - a decoder stall that empties the client bus made the clock hold for
        // ~80 ms and then jump. A smoothed estimate cannot be yanked by one sample, so every step advances.
        var terminal = new FakeClockedTerminal(Stereo48k) { SubmitToOutputLatency = TimeSpan.FromMilliseconds(100) };
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromSeconds(1));
        var previous = clock.ElapsedSinceStart;
        AssertMillisecondsNear(900, previous);

        // The dip: the bus empties / the terminal briefly reports a shallow buffer.
        terminal.SubmitToOutputLatency = TimeSpan.FromMilliseconds(20);
        terminal.Advance(TimeSpan.FromMilliseconds(20));
        _ = clock.ElapsedSinceStart;
        terminal.SubmitToOutputLatency = TimeSpan.FromMilliseconds(100);

        for (var step = 0; step < 4; step++)
        {
            terminal.Advance(TimeSpan.FromMilliseconds(40));
            var current = clock.ElapsedSinceStart;
            Assert.True(
                current - previous >= TimeSpan.FromMilliseconds(30),
                $"clock froze after the lead dip: step {step} advanced only {(current - previous).TotalMilliseconds:F1} ms");
            previous = current;
        }
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

        // Device stop/start (or an external flush): the terminal announces a NEW EPOCH and restarts at zero.
        // The client re-derives its epoch from that id, not from the size of the regression.
        terminal.Reanchor();
        Assert.True(clock.ElapsedSinceStart >= beforeReanchor, "client clock stepped back across a terminal re-anchor");

        // Recovery is a resume, not a freeze: the new epoch's progress counts from the high-water mark.
        terminal.Advance(TimeSpan.FromMilliseconds(400));
        var afterProgress = clock.ElapsedSinceStart;
        Assert.True(afterProgress >= beforeReanchor + TimeSpan.FromMilliseconds(300),
            $"client clock froze after the terminal re-anchor: {beforeReanchor} -> {afterProgress}");
    }

    [Fact]
    public void TerminalReanchor_DoesNotChangeTheClientsOwnEpoch_ButFlushDoes()
    {
        // The client clock stays continuous across a terminal re-anchor (that is what the recovery is FOR),
        // so its own epoch must not change there. Only its own re-anchors - attach and Flush - take a new id.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromMilliseconds(500));
        var attached = clock.Read();
        Assert.Equal(attached.EpochId, clock.EpochId);

        terminal.Reanchor();
        terminal.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(attached.EpochId, clock.Read().EpochId);

        ((IFlushableOutput)lease.Output).Flush();
        var flushed = clock.Read();
        Assert.NotEqual(attached.EpochId, flushed.EpochId);
        Assert.Equal(TimeSpan.Zero, flushed.Elapsed);
    }

    [Fact]
    public void TerminalRegressingWithoutANewEpoch_HoldsInsteadOfReanchoring()
    {
        // Plan D's equality rule from the consumer side: a regression is only a re-anchor when the terminal
        // SAYS it is. A dip inside one epoch (a broken terminal, or a torn read of one mid-re-anchor) is
        // held at the high-water - the old heuristic re-derived the epoch here and the client clock then
        // gained the whole dip back as spurious forward progress when the terminal recovered.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromSeconds(2));
        var peak = clock.ElapsedSinceStart;
        AssertMillisecondsNear(2000, peak);

        terminal.RegressWithoutAnnouncingAnEpoch(TimeSpan.FromSeconds(1));
        Assert.Equal(peak, clock.ElapsedSinceStart); // held at the high-water

        // The terminal returns to where it was; nothing new was played, so nothing new is reported.
        terminal.RegressWithoutAnnouncingAnEpoch(TimeSpan.FromSeconds(2));
        Assert.Equal(peak, clock.ElapsedSinceStart);

        terminal.Advance(TimeSpan.FromMilliseconds(500));
        AssertMillisecondsNear(2500, clock.ElapsedSinceStart);
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

    [Fact]
    public void TerminalThrowsMidRead_IsAdvancingAgreesWithTheAtomicRead()
    {
        // C4: the property caught the failure and answered FALSE while ReadRaw caught the same failure and
        // fell through to the wall-clock domain, which answers TRUE. A fan-in owner reading the member and a
        // composite reading Read() then disagreed about the same instant. A terminal that throws is not a
        // stopped clock, it is one this client can no longer see - and the domain it degrades to advances.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(clock.IsAdvancing);
        Assert.True(clock.Read().IsAdvancing);

        terminal.ThrowWhenRead = true; // disposed underneath us
        Assert.Equal(clock.Read().IsAdvancing, clock.IsAdvancing);
        Assert.True(clock.IsAdvancing);

        // Still false once this client itself is gone - that branch was never in dispute.
        lease.Dispose();
        Assert.False(clock.IsAdvancing);
        Assert.False(clock.Read().IsAdvancing);
    }

    [Fact]
    public void TerminalLostAfterAMostlyIdleDevice_FallbackResumesFromTheHighWater_NotWallTimeSinceAttach()
    {
        // C4 (second half): the fallback stopwatch runs from ATTACH and counts every second the device spent
        // paused, in the SAME epoch, so switching domains outright handed the consumer a monotonic forward
        // jump - which the high-water cannot catch, because it only clamps regressions. The fallback is
        // spliced onto the raw high-water instead.
        var terminal = new FakeClockedTerminal(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var lease = shared.Acquire();
        var clock = Assert.IsAssignableFrom<IPlaybackClock>(lease.Output);

        terminal.Advance(TimeSpan.FromMilliseconds(5)); // the device played 5 ms...
        AssertMillisecondsNear(5, clock.ElapsedSinceStart);
        Thread.Sleep(120);                              // ...then sat paused for a lot longer
        AssertMillisecondsNear(5, clock.ElapsedSinceStart);

        terminal.ThrowWhenRead = true;
        var afterLoss = clock.ElapsedSinceStart;
        Assert.InRange(afterLoss.TotalMilliseconds, 5 - PumpToleranceMs, 40); // was ~125 ms: +120 ms of nothing

        Thread.Sleep(30);
        Assert.True(clock.ElapsedSinceStart > afterLoss, "the fallback domain stopped advancing altogether.");
    }

    private static void AssertMillisecondsNear(double expectedMs, TimeSpan actual, double toleranceMs = PumpToleranceMs) =>
        Assert.InRange(actual.TotalMilliseconds, expectedMs - toleranceMs, expectedMs + toleranceMs);

    // --- fakes -------------------------------------------------------------

    /// <summary>Terminal whose device clock advances only when the test says so, with a settable
    /// submit-to-speaker latency (the surface the hardware backends report). Like a real backend it
    /// ANNOUNCES its re-anchors through <see cref="ClockReading.EpochId"/> and reads atomically.</summary>
    private sealed class FakeClockedTerminal(AudioFormat format) : IAudioOutput, IPlaybackClock, IAudioOutputLatency
    {
        private readonly Lock _gate = new();
        private long _elapsedTicks;
        private long _latencyTicks;
        private long _epochId = PlaybackEpoch.Next();
        private volatile bool _advancing = true;

        /// <summary>A terminal disposed/stopped underneath the client: every surface it exposes throws, which
        /// is what pushes the client clock into its wall-clock fallback domain.</summary>
        public volatile bool ThrowWhenRead;

        public AudioFormat Format { get; } = format;

        public TimeSpan ElapsedSinceStart
        {
            get
            {
                ThrowIfLost();
                return new TimeSpan(Interlocked.Read(ref _elapsedTicks));
            }
        }

        public bool IsAdvancing
        {
            get
            {
                ThrowIfLost();
                return _advancing;
            }
        }

        public long EpochId
        {
            get
            {
                ThrowIfLost();
                lock (_gate) return _epochId;
            }
        }

        public ClockReading Read()
        {
            ThrowIfLost();
            lock (_gate) return new ClockReading(_epochId, new TimeSpan(Interlocked.Read(ref _elapsedTicks)), _advancing);
        }

        public TimeSpan SubmitToOutputLatency
        {
            get
            {
                ThrowIfLost();
                return new TimeSpan(Interlocked.Read(ref _latencyTicks));
            }
            set => Interlocked.Exchange(ref _latencyTicks, value.Ticks);
        }

        private void ThrowIfLost()
        {
            if (ThrowWhenRead)
                throw new ObjectDisposedException(nameof(FakeClockedTerminal));
        }

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _elapsedTicks, delta.Ticks);

        /// <summary>Device stop/start: a new epoch whose clock restarts at zero.</summary>
        public void Reanchor()
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _elapsedTicks, 0);
                _epochId = PlaybackEpoch.Next();
            }
        }

        /// <summary>A terminal that breaks its per-epoch monotonic contract: elapsed rewinds with no new id.</summary>
        public void RegressWithoutAnnouncingAnEpoch(TimeSpan to) => Interlocked.Exchange(ref _elapsedTicks, to.Ticks);

        public void SetAdvancing(bool advancing) => _advancing = advancing;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class PlainTerminal(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }
}
