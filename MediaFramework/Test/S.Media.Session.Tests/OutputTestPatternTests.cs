using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Per-output calibration patterns. The composition-wide pattern is a top-most canvas layer, so it lights
/// up EVERY output bound to that composition - the lobby TV and the stream included - while you align one
/// projector. This one replaces the canvas for a single output, upstream of that output's mapping stage,
/// so the grid is cut and mesh-warped exactly like programme content.
/// </summary>
public sealed class OutputTestPatternTests
{
    private static ShowDocument OneComposition() => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 64, 48, 25, 1)],
        Routes: []);

    /// <summary>Tracks whether the frame it hands out was released - VideoFrame has no IsDisposed, and
    /// disposal is only observable through the release handle it was constructed with.</summary>
    private sealed class ReleaseProbe : IDisposable
    {
        public bool Released { get; private set; }
        public void Dispose() => Released = true;

        public VideoFrame Frame(int width = 64, int height = 48) =>
            new(TimeSpan.Zero,
                new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
                new byte[width * height * 4], width * 4,
                new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied),
                release: this);
    }

    private static VideoFrame Pattern(int width = 64, int height = 48) =>
        new(TimeSpan.Zero,
            new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
            new byte[width * height * 4], width * 4,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));

    private static async Task<ShowSession> SessionWithComposition()
    {
        var session = new ShowSession(MediaRegistry.Build(_ => { }));
        session.LoadDocument(OneComposition());
        await Task.CompletedTask;
        return session;
    }

    [Fact]
    public async Task SetsAndClears_ForAKnownOutput()
    {
        await using var session = await SessionWithComposition();
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector"));

        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", Pattern()));
        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", null));
    }

    [Fact]
    public async Task AnUnknownComposition_DisposesTheFrame_RatherThanLeakingIt()
    {
        await using var session = await SessionWithComposition();
        var probe = new ReleaseProbe();

        Assert.False(await session.SetOutputTestPatternAsync("nope", "projector", probe.Frame()));

        // Ownership transferred on the call, so a rejected call must release it - otherwise every
        // mistyped id leaks a full canvas buffer.
        Assert.True(probe.Released, "a rejected pattern was leaked");
    }

    [Fact]
    public async Task AnUnknownOutput_DisposesTheFrame_RatherThanLeakingIt()
    {
        await using var session = await SessionWithComposition();
        var probe = new ReleaseProbe();

        Assert.False(await session.SetOutputTestPatternAsync("screen", "no-such-output", probe.Frame()));

        Assert.True(probe.Released, "a rejected pattern was leaked");
    }

    [Fact]
    public async Task ReplacingAPattern_DisposesThePreviousOne()
    {
        await using var session = await SessionWithComposition();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector");

        var probe = new ReleaseProbe();
        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", probe.Frame()));
        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", Pattern()));

        // Held frames are the easiest thing to leak in a pump: they outlive every frame they are shown on.
        Assert.True(probe.Released, "the replaced pattern was leaked");
    }

    [Fact]
    public async Task ClearingAPattern_DisposesIt()
    {
        await using var session = await SessionWithComposition();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector");
        var probe = new ReleaseProbe();
        await session.SetOutputTestPatternAsync("screen", "projector", probe.Frame());

        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", null));

        Assert.True(probe.Released, "the cleared pattern was leaked");
    }

    [Fact]
    public async Task OnlyTheNamedOutput_IsAffected()
    {
        await using var session = await SessionWithComposition();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector");
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "lobby-tv");

        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", Pattern()));

        // The whole reason this exists: calibrating one line must not light up the others.
        var stats = await session.GetCompositionStatsAsync("screen");
        Assert.NotNull(stats);
        Assert.Contains(stats!.Value.OutputStats, o => o.OutputId == "lobby-tv");
    }

    [Fact]
    public async Task ThePatternSurvivesRepeatedFrames_RatherThanBeingConsumedByTheFirst()
    {
        await using var session = await SessionWithComposition();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector");
        var frame = Pattern();
        await session.SetOutputTestPatternAsync("screen", "projector", frame);

        // Let the pump run over it several times. Submitting transfers ownership, so an implementation
        // that handed the stored frame straight to the sink would dispose it on the first frame and then
        // use a dead frame - this is the bug the clone-per-submit exists to prevent.
        await Task.Delay(200);

        Assert.True(await session.SetOutputTestPatternAsync("screen", "projector", null));
    }
}
