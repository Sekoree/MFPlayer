using S.Media.Core.Registry;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Regression probe for the 2026-08-10 field incident: panic (StopAll with a short fade), then
/// re-fire a group containing the clip that had been sounding - the clip's video layer froze on its
/// first frame while its player poured frames into the slot (SamplingRepeatedFrames and
/// OverflowFrames both climbing at pump rate, every other layer fine).
/// </summary>
public sealed class RefireAfterStopAllTests
{
    private static ShowDocument TwoVideoCues() => new(
        Version: 1,
        Cues: [new CueDefinition("bg", 1, "BG"), new CueDefinition("ia", 2, "IA")],
        Clips:
        [
            new ShowClipBinding("bg", "fake://bg", CompositionId: "screen", LayerIndex: 0),
            new ShowClipBinding("ia", "fake://ia", CompositionId: "screen", LayerIndex: 2),
        ],
        Compositions: [new ShowComposition("screen", "Screen", 64, 48, 30, 1)],
        Routes: []);

    [Fact]
    public async Task Refire_after_short_fade_stop_advances_every_video_layer()
    {
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry(frameCount: 30_000));
        await session.LoadDocumentAsync(TwoVideoCues());
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));

        // The incident shape: one clip sounding alone, panic with a short fade, then a group fire
        // that contains the same clip.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("bg"));
        await Task.Delay(500);
        await session.StopAllAsync(TimeSpan.FromMilliseconds(250), default);

        var statuses = await session.FireCuesAsync(["bg", "ia"]);
        Assert.All(statuses, s => Assert.Equal(CueExecutionStatus.Fired, s));

        // Let the pump run, then sample the alignment counters over a one-second window. A frozen
        // layer repeats (and overflows) at pump rate; healthy layers stay near zero.
        await Task.Delay(1000);
        var before = await session.GetCompositionStatsAsync("screen");
        await Task.Delay(1000);
        var after = await session.GetCompositionStatsAsync("screen");
        Assert.NotNull(before);
        Assert.NotNull(after);

        var repeatedPerSecond = after!.Value.SourceSamplesRepeated - before!.Value.SourceSamplesRepeated;
        var overflowPerSecond = after.Value.SlotOverflowFrames - before.Value.SlotOverflowFrames;

        Assert.True(repeatedPerSecond < 10,
            $"a video layer is frozen: {repeatedPerSecond} repeated samples in 1s (frozen ≈ pump rate 30)");
        Assert.True(overflowPerSecond < 10,
            $"a video layer is not consuming frames: {overflowPerSecond} slot overflows in 1s");
    }
}
