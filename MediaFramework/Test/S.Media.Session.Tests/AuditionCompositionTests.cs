using S.Media.Core.Registry;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// The audition rig's video half: a hidden composition a previewed cue is placed onto, so the monitor shows
/// the clip composited rather than as a bare source-resolution picture.
/// </summary>
/// <remarks>
/// The rig is deliberately NOT part of the show. Everything below turns on that one decision: it is opt-in
/// (it costs a driver thread), it survives document loads, and it never appears among the show's own
/// compositions.
/// </remarks>
public sealed class AuditionCompositionTests
{
    private static ShowDocument DocWith(params string[] compositionIds) => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [.. compositionIds.Select(id => new ShowComposition(id, id, 64, 48, 25, 1))],
        Routes: []);

    private static ShowSession Session() => new(MediaRegistry.Build(_ => { }));

    [Fact]
    public async Task TheRigIsOffUntilAskedFor()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));

        // It costs a GL thread for as long as it is up, so a show that never auditions must never pay.
        Assert.False(session.IsAuditionCompositionEnabled);
        Assert.Null(await session.GetAuditionCompositionStatsAsync());
        Assert.False(await session.AttachAuditionOutputAsync(new DiscardingVideoOutput()));
    }

    [Fact]
    public async Task EnablingBringsUpACanvasThatAcceptsASurface()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));

        await session.EnableAuditionCompositionAsync(new AuditionCompositionSpec(320, 240, 25, 1));

        Assert.True(session.IsAuditionCompositionEnabled);
        Assert.NotNull(await session.GetAuditionCompositionStatsAsync());
        Assert.True(await session.AttachAuditionOutputAsync(new DiscardingVideoOutput(), "monitor"));
        Assert.True(await session.DetachAuditionOutputAsync("monitor"));
    }

    [Fact]
    public async Task TheRigSurvivesADocumentLoad()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));
        await session.EnableAuditionCompositionAsync();
        Assert.True(await session.AttachAuditionOutputAsync(new DiscardingVideoOutput(), "monitor"));

        await session.LoadDocumentAsync(DocWith("other"));

        // Living in the document's composition dictionary would black the monitor out on every reload -
        // the rig is the operator's setup, not the show's content.
        Assert.True(session.IsAuditionCompositionEnabled);
        Assert.True(await session.DetachAuditionOutputAsync("monitor"));
    }

    [Fact]
    public async Task TheAuditionCanvasIsNotOneOfTheShowsCompositions()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));
        await session.EnableAuditionCompositionAsync();

        var compositions = session.GetAllCompositionStats();

        Assert.DoesNotContain(compositions, c => c.CompositionId == ShowSession.AuditionCompositionId);
        Assert.Contains(compositions, c => c.CompositionId == "screen");
        Assert.Null(await session.GetCompositionStatsAsync(ShowSession.AuditionCompositionId));
    }

    [Fact]
    public async Task ReEnablingWithTheSameSpec_KeepsTheCanvasAndItsOutputs()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));
        var spec = new AuditionCompositionSpec(320, 240, 25, 1);
        await session.EnableAuditionCompositionAsync(spec);
        await session.AttachAuditionOutputAsync(new DiscardingVideoOutput(), "monitor");

        await session.EnableAuditionCompositionAsync(spec);

        // A host that re-asserts its settings on every settings-save must not flicker the monitor.
        Assert.True(await session.DetachAuditionOutputAsync("monitor"));
    }

    [Fact]
    public async Task ReEnablingWithAChangedSpec_RebuildsTheCanvas()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));
        await session.EnableAuditionCompositionAsync(new AuditionCompositionSpec(320, 240, 25, 1));
        await session.AttachAuditionOutputAsync(new DiscardingVideoOutput(), "monitor");

        await session.EnableAuditionCompositionAsync(new AuditionCompositionSpec(640, 480, 25, 1));

        Assert.True(session.IsAuditionCompositionEnabled);
        // Outputs do not survive a rebuild, and saying so is better than silently re-attaching to a canvas
        // of a different size: the host re-attaches, exactly as it did the first time.
        Assert.False(await session.DetachAuditionOutputAsync("monitor"));
        Assert.True(await session.AttachAuditionOutputAsync(new DiscardingVideoOutput(), "monitor"));
    }

    [Fact]
    public async Task DisablingGivesTheThreadBack_AndIsIdempotent()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));
        await session.EnableAuditionCompositionAsync();

        await session.DisableAuditionCompositionAsync();
        await session.DisableAuditionCompositionAsync();

        Assert.False(session.IsAuditionCompositionEnabled);
        Assert.Null(await session.GetAuditionCompositionStatsAsync());
    }

    [Fact]
    public async Task ADegenerateCanvasIsRefused()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(DocWith("screen"));

        // A zero-sized canvas would build a pump that can never composite anything - fail where the mistake
        // was made rather than leaving a monitor that is simply, inexplicably black.
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.EnableAuditionCompositionAsync(new AuditionCompositionSpec(0, 240)));
        Assert.False(session.IsAuditionCompositionEnabled);
    }
}
