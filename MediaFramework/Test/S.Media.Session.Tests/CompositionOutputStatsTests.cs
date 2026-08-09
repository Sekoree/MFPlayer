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

    [Fact]
    public async Task SurfacesDropsFromTheDeviceItself_WhichNoUpstreamCounterCanSee()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        // A vsynced window drops at the vblank, and its Submit returns without blocking - so the pump in
        // front of it never backs up and never counts anything. Before this row existed, a composition
        // feeding a stuttering display reported perfect health.
        var device = new DroppingDiagnosticOutput { DroppedFrames = 7, RepeatedFrames = 3 };
        Assert.True(await session.AttachCompositionOutputAsync("screen", device));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        var row = Assert.Single(stats!.Value.OutputStats, r => r.PresentDropped == 7);
        Assert.Equal(3, row.PresentRepeated);
    }

    [Fact]
    public async Task LeavesPresentCountersAtZero_ForASinkThatDoesNotReportThem()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        // A sink with no device cadence has nothing to report - it must read 0, not a stale or invented
        // number that would make an innocent output look like it was dropping.
        Assert.All(stats!.Value.OutputStats, r =>
        {
            Assert.Equal(0, r.PresentDropped);
            Assert.Equal(0, r.PresentRepeated);
        });
    }

    [Fact]
    public async Task ReportsTheWorstPresentLatencyAcrossOutputs_BecauseTheSlowestScreenIsTheOneThatLags()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        Assert.True(await session.AttachCompositionOutputAsync(
            "screen", new DroppingDiagnosticOutput { PresentLatency = TimeSpan.FromMilliseconds(16) }));
        Assert.True(await session.AttachCompositionOutputAsync(
            "screen", new DroppingDiagnosticOutput { PresentLatency = TimeSpan.FromMilliseconds(40) }));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        Assert.Equal(TimeSpan.FromMilliseconds(40), stats!.Value.MeasuredPresentLatency);
    }

    [Fact]
    public async Task ClampsAnAbsurdPresentLatency_SoOneBadOutputCannotDesyncTheCanvas()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        Assert.True(await session.AttachCompositionOutputAsync(
            "screen", new DroppingDiagnosticOutput { PresentLatency = TimeSpan.FromSeconds(5) }));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        Assert.Equal(TimeSpan.FromMilliseconds(250), stats!.Value.MeasuredPresentLatency);
    }

    [Fact]
    public async Task ASinkWithNoDeviceCadenceContributesNoLatency_SoNdiAndRecordersCannotPullTheCanvasEarly()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        session.LoadDocument(OneComposition());

        // A discarding sink consumes as fast as it is fed; it has no vblank to wait for and does not
        // implement the diagnostics interface at all. Compositing ahead for its benefit would put the
        // actual screens out of step with the sound.
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        var stats = await session.GetCompositionStatsAsync("screen");

        Assert.NotNull(stats);
        Assert.Equal(TimeSpan.Zero, stats!.Value.MeasuredPresentLatency);
    }

    private sealed class DroppingDiagnosticOutput : IVideoOutput, IVideoOutputPresentDiagnostics
    {
        private VideoFormat _format;

        public long PresentedFrames { get; set; }
        public long DroppedFrames { get; set; }
        public long RepeatedFrames { get; set; }
        public int QueuedFrames => 0;
        public int PresentQueueDepth => 2;
        public TimeSpan PresentLatency { get; set; }

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];

        public void Configure(VideoFormat format) => _format = format;

        public void Submit(VideoFrame frame)
        {
            PresentedFrames++;
            frame.Dispose();
        }
    }
}
