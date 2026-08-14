using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// The reach of each stop form over the ORDINARY in-flight fire (a cue inside its pre-wait or media
/// open). The regression these pin: the active fire was an anonymous CancellationTokenSource and the
/// targeted stops cancelled it unconditionally - so stopping unrelated cue B while cue A sat in its
/// pre-wait silently killed A's fire. The scheduled-batch scoping next door
/// (<see cref="CueFireScopingTests"/>) was already surgical; this closes the same contract over the
/// ordinary path.
/// </summary>
public sealed class OrdinaryFireScopingTests
{
    private static ShowDocument TwoGroupDocument(TimeSpan preWaitA) => new(
        Version: 1,
        Cues:
        [
            new CueDefinition("a", 1, "A", PreWait: preWaitA, GroupId: "ga"),
            new CueDefinition("b", 2, "B", GroupId: "gb"),
        ],
        Clips:
        [
            new ShowClipBinding("a", "fake://a"),
            new ShowClipBinding("b", "fake://b"),
        ],
        Compositions: [], Routes: []);

    /// <summary>Starts cue A's fire and gives it comfortably enough time to enter its pre-wait.</summary>
    private static async Task<Task<CueExecutionStatus>> BeginPreWaitingFireAsync(ShowSession session)
    {
        var fire = session.FireCueAsync("a");
        await Task.Delay(300);
        Assert.False(fire.IsCompleted);
        return fire;
    }

    [Fact]
    public async Task StoppingAnUnrelatedCue_LeavesTheInFlightFireRunning()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoGroupDocument(TimeSpan.FromSeconds(1.5)));
        var fire = await BeginPreWaitingFireAsync(session);

        // The old blanket cancellation made THIS call kill A's pre-waiting fire.
        await session.StopCueAsync("b");

        Assert.Equal(CueExecutionStatus.Fired, await fire.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task StoppingAnUnrelatedGroup_LeavesTheInFlightFireRunning()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoGroupDocument(TimeSpan.FromSeconds(1.5)));
        var fire = await BeginPreWaitingFireAsync(session);

        await session.StopAsync("gb");

        Assert.Equal(CueExecutionStatus.Fired, await fire.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task StoppingTheFiringCueItself_CancelsItsInFlightFire()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoGroupDocument(TimeSpan.FromSeconds(30)));
        var fire = await BeginPreWaitingFireAsync(session);

        await session.StopCueAsync("a");

        // Cancellation maps to a non-advancing Failed, promptly - not after the 30 s pre-wait.
        Assert.Equal(CueExecutionStatus.Failed, await fire.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StoppingTheFiringCuesGroup_CancelsItsInFlightFire()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoGroupDocument(TimeSpan.FromSeconds(30)));
        var fire = await BeginPreWaitingFireAsync(session);

        await session.StopAsync("ga");

        Assert.Equal(CueExecutionStatus.Failed, await fire.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopAll_StillCancelsTheInFlightFire()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoGroupDocument(TimeSpan.FromSeconds(30)));
        var fire = await BeginPreWaitingFireAsync(session);

        await session.StopAllAsync(TimeSpan.Zero);

        Assert.Equal(CueExecutionStatus.Failed, await fire.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
