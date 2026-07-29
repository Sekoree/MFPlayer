using S.Media.NDI.Clock;
using S.Media.Time;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// Plan D: the ingest master is the clock that historically broke <see cref="IPlaybackClock"/>'s monotonic
/// promise, so it is the one that most needs to SAY when its timeline restarts. Both of its discontinuous
/// repositions - <see cref="NDIIngestPlaybackClock.AttachReceiver"/> (relocate onto a new sender) and
/// <see cref="NDIIngestPlaybackClock.Seek"/> - take a new epoch id; ordinary frame arrival never does.
/// The injected timestamp source keeps the wall extrapolation out of the assertions.
/// </summary>
public sealed class NDIIngestPlaybackClockEpochTests
{
    private const int SampleRate = 48_000;
    private const int SamplesPerFrame = 4_800; // 100 ms

    [Fact]
    public void FrameArrival_KeepsTheEpoch_AndAdvancesWithinIt()
    {
        var now = 0L;
        var clock = new NDIIngestPlaybackClock(() => now);
        clock.AttachReceiver();
        var attached = clock.EpochId;
        Assert.NotEqual(PlaybackEpoch.Single, attached);

        NotifyAudioSecond(clock, second: 0);
        var first = clock.Read();
        Assert.Equal(attached, first.EpochId);
        Assert.True(first.IsAdvancing);

        NotifyAudioSecond(clock, second: 1);
        var second = clock.Read();
        Assert.Equal(attached, second.EpochId);
        Assert.True(second.Elapsed > first.Elapsed, "elapsed must advance within one epoch");
    }

    [Fact]
    public void AttachReceiver_TakesANewEpoch_BecauseTheTimelineRestartsAtZero()
    {
        var now = 0L;
        var clock = new NDIIngestPlaybackClock(() => now);
        clock.AttachReceiver();
        NotifyAudioSecond(clock, second: 0);
        NotifyAudioSecond(clock, second: 5);
        var before = clock.Read();
        Assert.True(before.Elapsed > TimeSpan.Zero);

        clock.AttachReceiver(); // relocate: new sender, timeline restarts

        var after = clock.Read();
        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.Equal(TimeSpan.Zero, after.Elapsed);
        Assert.False(after.IsAdvancing);
    }

    [Fact]
    public void Seek_TakesANewEpoch_BecauseTheRepositionMayGoBackwards()
    {
        var now = 0L;
        var clock = new NDIIngestPlaybackClock(() => now);
        clock.AttachReceiver();
        NotifyAudioSecond(clock, second: 0);
        NotifyAudioSecond(clock, second: 5);
        var before = clock.Read();

        clock.Seek(TimeSpan.FromSeconds(1)); // backwards - legal only across an epoch boundary

        var after = clock.Read();
        Assert.NotEqual(before.EpochId, after.EpochId);
        Assert.True(after.Elapsed < before.Elapsed);
    }

    private static void NotifyAudioSecond(NDIIngestPlaybackClock clock, int second) =>
        clock.NotifyAudioFrame(
            SampleRate,
            SamplesPerFrame,
            timecode100Ns: second * TimeSpan.TicksPerSecond,
            timestamp100Ns: second * TimeSpan.TicksPerSecond);
}
