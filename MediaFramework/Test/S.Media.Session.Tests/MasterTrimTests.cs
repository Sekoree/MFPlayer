using Xunit;

namespace S.Media.Session.Tests;

/// <summary>Session-wide master trim (<see cref="ShowSession.SetMasterTrimAsync"/> - the cue
/// transport's live "Master" fader): it multiplies the per-clip fade/envelope levels instead of
/// overwriting them, newly fired clips inherit the current trim, and the value clamps to [0, 1].</summary>
public sealed class MasterTrimTests
{
    private static ShowDocument OneCue(string mediaPath) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", mediaPath)],
        Compositions: [], Routes: []);

    [Fact]
    public async Task MasterTrim_ScalesEffectiveLevel_AndComposesWithFades()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var levels = await session.GetClipAudioLevelsAsync("c");
        Assert.Equal(1f, levels!.EffectiveLevel, 3);

        // Trim scales the effective (route-written) level but never pollutes the persistent fade
        // level fades compose from.
        await session.SetMasterTrimAsync(0.5f);
        levels = await session.GetClipAudioLevelsAsync("c");
        Assert.Equal(1f, levels!.FadeLevel, 3);
        Assert.Equal(0.5f, levels.EffectiveLevel, 3);

        // A fade cue under a reduced trim composes multiplicatively: fade 0.5 × trim 0.5 = 0.25.
        await session.FadeClipAsync("c", 0.5f, TimeSpan.FromMilliseconds(150), stopWhenSilent: false);
        levels = await session.GetClipAudioLevelsAsync("c");
        Assert.Equal(0.5f, levels!.FadeLevel, 3);
        Assert.Equal(0.25f, levels.EffectiveLevel, 3);

        // Restoring unity brings back exactly the faded level - the trim never destroyed it.
        await session.SetMasterTrimAsync(1f);
        levels = await session.GetClipAudioLevelsAsync("c");
        Assert.Equal(0.5f, levels!.FadeLevel, 3);
        Assert.Equal(0.5f, levels.EffectiveLevel, 3);
    }

    [Fact]
    public async Task MasterTrim_IsInheritedByClipsFiredAfterTheChange()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));

        // Trim set BEFORE anything plays: the session-level scalar (not per-clip state) means the
        // next fire starts already trimmed.
        await session.SetMasterTrimAsync(0.25f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var levels = await session.GetClipAudioLevelsAsync("c");
        Assert.Equal(1f, levels!.FadeLevel, 3);
        Assert.Equal(0.25f, levels.EffectiveLevel, 3);
    }

    [Fact]
    public async Task MasterTrim_ClampsToUnitRange()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));

        await session.SetMasterTrimAsync(2f);
        Assert.Equal(1f, session.MasterTrim, 3);

        await session.SetMasterTrimAsync(-0.5f);
        Assert.Equal(0f, session.MasterTrim, 3);

        await session.SetMasterTrimAsync(float.NaN);
        Assert.Equal(1f, session.MasterTrim, 3);
    }
}
