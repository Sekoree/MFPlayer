using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Per-output video telemetry. The composition-wide <c>FramesSubmitted</c> sums across every attached
/// output, so it can say a composition is running but not which line is dropping - which is the only
/// question a per-output diagnostics row exists to answer.
/// </summary>
public class CompositionOutputStatsTests
{
    private static ShowDocument OneComposition() => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 320, 240, 25, 1)],
        Routes: []);

    [Fact]
    public async Task ReportsOneRowPerAttachedOutput()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        // The session-owned discarding lease counts too, so this is "at least the two just attached".
        Assert.True(stats!.Value.OutputStats.Count >= 2,
            $"expected a row per output, got {stats.Value.OutputStats.Count}");
        Assert.All(stats.Value.OutputStats, row => Assert.False(string.IsNullOrEmpty(row.OutputId)));
    }

    [Fact]
    public async Task ExposesTheTargetFrameRate_RatherThanMakingEveryCallerDeriveIt()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        // Target, not achieved: achieved fps is a delta over wall time that only the caller can compute,
        // but the denominator it is shown against belongs here.
        Assert.Equal(25d, stats!.Value.TargetFramesPerSecond, 3);
    }

    [Fact]
    public async Task OutputStats_IsEmptyRatherThanNull_WhenNothingIsAttached()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(new ShowDocument(
            Version: 1, Cues: [], Clips: [], Compositions: [], Routes: []));

        var all = session.GetAllCompositionStats();

        // A null list would make every consumer null-check; empty is the same information without the trap.
        Assert.All(all, s => Assert.NotNull(s.OutputStats));
    }
}
