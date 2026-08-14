using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// F-08 (2026-08-14 review): fires are serialized PER RUNTIME GROUP, not by one global fire lock.
/// The head-of-line regression these pin: a cold or slow open on list A used to hold the single
/// _fireLock across select → pre-wait → open → fire → advance, so an unrelated list B's GO waited
/// behind it - up to the 30 s batch bound. Same-group semantics must survive the decomposition:
/// two rapid GOs on ONE list still cannot double-fire a cue.
/// </summary>
public sealed class GoFireIndependenceTests
{
    private static IMediaRegistry TwoProviderRegistry() =>
        MediaRegistry.Build(b => b
            .AddDecoder(new BlockingOpenProvider())
            .AddDecoder(new FakeAudioDecoderProvider(chunks: 100_000)));

    private static ShowDocument TwoListDocument() => new(
        Version: 1,
        Cues:
        [
            new CueDefinition("a", 1, "A", GroupId: "ga"),
            new CueDefinition("b", 2, "B", GroupId: "gb"),
        ],
        Clips:
        [
            new ShowClipBinding("a", "blocking://a"),
            new ShowClipBinding("b", "fake://b"),
        ],
        Compositions: [], Routes: []);

    [Fact]
    public async Task ABlockedOpenOnOneList_DoesNotDelayGoOnAnUnrelatedList()
    {
        await using var session = new ShowSession(TwoProviderRegistry());
        await session.LoadDocumentAsync(TwoListDocument());

        // List A's GO enters its clip open and parks there (the provider blocks until cancelled).
        var blockedGo = session.GoAsync("ga");
        await Task.Delay(300);
        Assert.False(blockedGo.IsCompleted);

        // List B's GO must complete promptly - under the old global fire lock THIS was the wait.
        var status = await session.GoAsync("gb").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CueExecutionStatus.Fired, status);

        // Teardown: session disposal cancels the blocked fire (CancelAllFires); it must not report Fired.
        await session.StopAllAsync(TimeSpan.Zero);
        var blockedStatus = await blockedGo.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(CueExecutionStatus.Fired, blockedStatus);
    }

    [Fact]
    public async Task TwoSimultaneousGosOnTheSameList_CannotDoubleFireTheOneArmedCue()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("only", 1, "Only", GroupId: "g")],
            Clips: [new ShowClipBinding("only", "fake://only")],
            Compositions: [], Routes: []));

        // Two rapid GOs (e.g. remote + panel): per-group serialization must keep select → fire →
        // advance atomic, so exactly ONE fires the cue and the other finds nothing left after the
        // cursor advanced.
        var first = session.GoAsync("g");
        var second = session.GoAsync("g");
        var statuses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, statuses.Count(s => s == CueExecutionStatus.Fired));
        Assert.Equal(1, statuses.Count(s => s == CueExecutionStatus.NotReady));
    }
}
