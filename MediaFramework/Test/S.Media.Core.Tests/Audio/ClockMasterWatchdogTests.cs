using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>
/// Surviving a stalling clock master (HaCue2 framework audit, decision D2). An ordinary terminal that
/// wedges is already survivable - the router quarantines it and the other lines keep running - but the
/// PACING master is the one line whose failure faults the router permanently, so recovery has to happen
/// before the failure rather than after it.
/// </summary>
public class ClockMasterWatchdogTests
{
    private const int Rate = 48_000;

    private static float[,] Identity() => new float[,] { { 1f, 0f }, { 0f, 1f } };

    private static NullClockedAudioOutput Clocked() => new(new AudioFormat(Rate, 2));

    // --- the handoff mechanism ----------------------------------------------------------------

    [Fact]
    public void PromoteClockMaster_MovesPacing_WithoutDetachingAnything()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);
        bay.AddTerminal("spare", Clocked(), Identity());
        Assert.Equal("main", bay.ClockMasterTerminalId);

        bay.PromoteClockMaster("spare");

        Assert.Equal("spare", bay.ClockMasterTerminalId);
        // Both lines are still attached: this is a pacing change, not a device swap. That is what
        // makes it cheap enough to do speculatively.
        Assert.Equal(2, bay.TerminalCount);
    }

    [Fact]
    public void PromoteClockMaster_ToTheCurrentMaster_IsANoOp()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);

        bay.PromoteClockMaster("main");

        Assert.Equal("main", bay.ClockMasterTerminalId);
    }

    [Fact]
    public void PromoteClockMaster_RefusesAForeignRateTerminal()
    {
        using var bay = new AudioPatchBay(
            logicalChannels: 2, Rate,
            resamplerFactory: (inner, format) => new ResampledStub(inner, format));
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);
        bay.AddTerminal("off-rate", new NullClockedAudioOutput(new AudioFormat(44_100, 2)), Identity());

        // Same rule as on attach, and the same exception: a resampled master would skew the program
        // clock silently, because the resampling wrapper does not report its own internal delay.
        var error = Assert.Throws<InvalidOperationException>(() => bay.PromoteClockMaster("off-rate"));
        Assert.Contains("mix rate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("main", bay.ClockMasterTerminalId);
    }

    [Fact]
    public void PromoteClockMaster_RefusesAnUnknownTerminal()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);

        Assert.Throws<ArgumentException>(() => bay.PromoteClockMaster("nope"));
        Assert.Equal("main", bay.ClockMasterTerminalId);
    }

    [Fact]
    public void TryPromoteHealthiest_FailsWhenThereIsNoOtherEligibleLine()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);

        // The honest unrecoverable case: nothing else can pace, so the caller reports rather than
        // pretending it recovered.
        Assert.False(bay.TryPromoteHealthiestClockMaster(out var promoted));
        Assert.Null(promoted);
        Assert.Equal("main", bay.ClockMasterTerminalId);
    }

    [Fact]
    public void TryPromoteHealthiest_SkipsLinesThatCannotPace()
    {
        using var bay = new AudioPatchBay(
            logicalChannels: 2, Rate,
            resamplerFactory: (inner, format) => new ResampledStub(inner, format));
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);
        // Neither of these may pace: one is off-rate, the other is not a clocked output at all.
        bay.AddTerminal("off-rate", new NullClockedAudioOutput(new AudioFormat(44_100, 2)), Identity());
        bay.AddTerminal("unclocked", new PlainOutput(new AudioFormat(Rate, 2)), Identity());

        Assert.False(bay.TryPromoteHealthiestClockMaster(out _));
        Assert.Equal("main", bay.ClockMasterTerminalId);

        bay.AddTerminal("spare", Clocked(), Identity());
        Assert.True(bay.TryPromoteHealthiestClockMaster(out var promoted));
        Assert.Equal("spare", promoted);
        Assert.Equal("spare", bay.ClockMasterTerminalId);
    }

    // --- the detector -------------------------------------------------------------------------

    [Fact]
    public void Evaluate_WithNoMaster_ReportsNoMaster()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("house", new PlainOutput(new AudioFormat(Rate, 2)), Identity());
        var watchdog = new ClockMasterWatchdog(bay);

        Assert.Equal(ClockMasterWatchdogOutcome.NoMaster, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void Evaluate_WhileTheMasterDrains_StaysHealthy_AndNeverPromotes()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        bay.AddTerminal("main", Clocked(), Identity(), isClockMaster: true);
        bay.AddTerminal("spare", Clocked(), Identity());
        var watchdog = new ClockMasterWatchdog(bay);

        using var voice = bay.AcquireProducer(1, [1f, 1f]);
        bay.Play();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                voice.Submit(new float[480]);
                Thread.Sleep(5);
                var step = watchdog.Evaluate();
                Assert.True(
                    step.Outcome is ClockMasterWatchdogOutcome.Healthy or ClockMasterWatchdogOutcome.Suspect,
                    $"a draining master must not trip (got {step.Outcome}: {step.Reason})");
            }

            // A healthy run must never move pacing - a watchdog that reshuffles the master under load
            // would be worse than none.
            Assert.Equal("main", bay.ClockMasterTerminalId);
        }
        finally
        {
            bay.Stop();
        }
    }

    [Fact]
    public void Evaluate_TripsOnlyAfterConsecutiveStalls_NotOnTheFirstOne()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var wedged = new WedgingClockedOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("main", wedged, Identity(), isClockMaster: true);
        bay.AddTerminal("spare", Clocked(), Identity());
        var watchdog = new ClockMasterWatchdog(bay, stallTripCount: 3);

        using var voice = bay.AcquireProducer(1, [1f, 1f]);
        bay.Play();
        try
        {
            // Drive audio until the master's pump is provably stuck in Submit and its queue has filled.
            Assert.True(FeedUntil(voice, () => wedged.Entered.IsSet), "the master never wedged");
            FeedFor(voice, TimeSpan.FromMilliseconds(300));

            // First observations may be Suspect but must not act - one stalled sample is a hiccup.
            var first = watchdog.Evaluate();
            Assert.NotEqual(ClockMasterWatchdogOutcome.Recovered, first.Outcome);
            Assert.Equal("main", bay.ClockMasterTerminalId);

            // Keep evaluating; once the run reaches the trip count it hands pacing to the spare.
            var recovered = false;
            for (var i = 0; i < 10 && !recovered; i++)
            {
                var step = watchdog.Evaluate();
                recovered = step.Outcome == ClockMasterWatchdogOutcome.Recovered;
                if (recovered)
                    Assert.Equal("spare", step.PromotedTerminalId);
            }

            Assert.True(recovered, "the watchdog never recovered from a wedged master");
            Assert.Equal("spare", bay.ClockMasterTerminalId);
        }
        finally
        {
            wedged.Release();
            bay.Stop();
        }
    }

    [Fact]
    public void Evaluate_WithNoSpare_ReportsUnrecoverable_RatherThanPretending()
    {
        using var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var wedged = new WedgingClockedOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("main", wedged, Identity(), isClockMaster: true);
        var watchdog = new ClockMasterWatchdog(bay, stallTripCount: 2);

        using var voice = bay.AcquireProducer(1, [1f, 1f]);
        bay.Play();
        try
        {
            Assert.True(FeedUntil(voice, () => wedged.Entered.IsSet), "the master never wedged");
            FeedFor(voice, TimeSpan.FromMilliseconds(300));

            var unrecoverable = false;
            for (var i = 0; i < 10 && !unrecoverable; i++)
                unrecoverable = watchdog.Evaluate().Outcome == ClockMasterWatchdogOutcome.Unrecoverable;

            Assert.True(unrecoverable, "a stalled master with nowhere to go must report Unrecoverable");
            Assert.Equal("main", bay.ClockMasterTerminalId);
        }
        finally
        {
            wedged.Release();
            bay.Stop();
        }
    }

    // --- helpers ------------------------------------------------------------------------------

    private static bool FeedUntil(ProgramBusProducer voice, Func<bool> condition, int timeoutMs = 5000)
    {
        var chunk = new float[480];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;
            voice.Submit(chunk);
            Thread.Sleep(5);
        }
        return condition();
    }

    private static void FeedFor(ProgramBusProducer voice, TimeSpan duration)
    {
        var chunk = new float[480];
        var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            voice.Submit(chunk);
            Thread.Sleep(5);
        }
    }

    /// <summary>A clocked device whose native Submit wedges on first use - a pacing master that stops
    /// draining, which is the failure this watchdog exists for.</summary>
    private sealed class WedgingClockedOutput(AudioFormat fmt) : IAudioOutput, IClockedOutput
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public AudioFormat Format => fmt;

        public void Submit(ReadOnlySpan<float> samples)
        {
            Entered.Set();
            _release.Wait();
        }

        public bool WaitForCapacity(int samples, CancellationToken token) => true;

        public void Release() => _release.Set();
    }

    /// <summary>An output with no clock at all - ineligible to pace.</summary>
    private sealed class PlainOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
    }

    /// <summary>Minimal rate adapter so an off-rate terminal can attach at all.</summary>
    private sealed class ResampledStub(IAudioOutput inner, AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format => format;
        public void Submit(ReadOnlySpan<float> samples) => inner.Submit(samples);
    }
}
