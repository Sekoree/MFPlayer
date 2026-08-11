using HaPlay.Playback;
using HaPlay.ViewModels;
using S.Media.Time;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueCompositionRuntimeTests
{
    [Fact]
    public void AcquireTransportTimeline_ThenEnsurePumpStarted_StartsExactlyOnce()
    {
        // Regression for the Phase 5.4 double-start bug - when the engine mastered the composition
        // (which started a slaved MediaClock) and then AddLayer (which called EnsurePumpStarted), a
        // second MediaClock + driver thread would spawn because EnsurePumpStarted only checked the
        // (always-null) Stopwatch _pumpTask field. This test ensures one and only one pump start
        // happens across the typical engine call sequence.
        using var runtime = NewRuntime();

        using var claim = runtime.AcquireTransportTimeline(NewTimeline());
        runtime.EnsurePumpStarted();
        runtime.EnsurePumpStarted();
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
    }

    [Fact]
    public void EnsurePumpStarted_BeforeAcquiringATimeline_StaysSingleStart()
    {
        // The "no master yet" path also has to stay single-shot - the runtime creates one MediaClock
        // with master=null and later swaps the master in via MediaClock.SetMaster (same driver thread,
        // same GL context).
        using var runtime = NewRuntime();

        runtime.EnsurePumpStarted();
        using var claim = runtime.AcquireTransportTimeline(NewTimeline());
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
        Assert.True(runtime.GetStats().ClockMastered);
    }

    [Fact]
    public void SecondTimelineWaitsBehindTheFirst_AndTakesOverWhenItIsReleased()
    {
        // A composition is ONE clock domain: two cues firing into it must not fight over the slave
        // clock's master assignment, so the first claim owns it. Unlike the old first-wins
        // SetClockMaster, releasing that claim hands the domain to the waiting one rather than
        // stranding the composition on a stopped timeline forever.
        using var runtime = NewRuntime();

        var first = NewTimeline();
        var second = NewTimeline();
        var firstClaim = runtime.AcquireTransportTimeline(first);
        using var secondClaim = runtime.AcquireTransportTimeline(second);
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
        Assert.True(runtime.GetStats().ClockMastered);

        firstClaim.Dispose();
        Assert.True(runtime.GetStats().ClockMastered); // handed to `second`, not released
    }

    [Fact]
    public void ReleasingTheLastClaimReturnsTheCompositionToFreerun()
    {
        using var runtime = NewRuntime();

        var claim = runtime.AcquireTransportTimeline(NewTimeline());
        runtime.EnsurePumpStarted();
        Assert.True(runtime.GetStats().ClockMastered);

        claim.Dispose();
        Assert.False(runtime.GetStats().ClockMastered);
    }

    [Fact]
    public void LeasedLineCount_StaysZeroWhenNDICarrierCannotBeAcquired()
    {
        var outputs = new OutputManagementViewModel();
        var line = new OutputLineViewModel(
            new NDIOutputDefinition(
                Guid.NewGuid(),
                "NDI",
                "ndi",
                null,
                NDIOutputStreamMode.VideoAndAudio,
                AudioChannelCount: 2,
                AudioSampleRate: 48000),
            _ => { },
            outputs);
        var composition = new CueComposition { Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1 };
        using var runtime = new CueCompositionRuntime(composition, [line], outputs);

        Assert.Equal(0, runtime.LeasedLineCount);
        Assert.False(runtime.DrivesLine(line.Definition.Id));
    }

    private static CueCompositionRuntime NewRuntime()
    {
        var outputs = new OutputManagementViewModel();
        var composition = new CueComposition
        {
            Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1,
        };
        return new CueCompositionRuntime(composition, [], outputs);
    }

    // A real timeline rather than a stub: the runtime only ever stores and forwards it, so the genuine
    // article costs nothing and cannot drift from the contract.
    private static TransportTimeline NewTimeline() => new(SessionClock.LiveWallClock());
}
