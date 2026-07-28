using Xunit;

namespace S.Media.Session.Tests;

/// <summary>Fade-cue framework surface (<see cref="ShowSession.FadeClipAsync"/>): ramps to arbitrary
/// levels compose across consecutive fades (the persistent per-clip level), a silent fade with
/// stopWhenSilent releases the clip via the stop path, and an operator stop preempts a running fade-cue
/// ramp without deadlocking either task.</summary>
public sealed class FadeClipTests
{
    private static ShowDocument OneCue(string mediaPath) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", mediaPath)],
        Compositions: [], Routes: []);

    [Fact]
    public async Task FadeClip_RampsToNonZeroTarget_AndConsecutiveFadesCompose()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        Assert.Equal(1f, (await session.GetClipFadeLevelAsync("c"))!.Value, 3);

        // Fade to −6 dB-ish (0.5 linear): the clip stays active at the reduced persistent level.
        await session.FadeClipAsync("c", 0.5f, TimeSpan.FromMilliseconds(200), stopWhenSilent: false);
        Assert.Equal(0.5f, (await session.GetClipFadeLevelAsync("c"))!.Value, 3);
        Assert.True(Assert.Single(session.Snapshot()).IsActive);

        // A second fade COMPOSES: a slow fade toward silence starts from 0.5, not from 1 (a
        // non-composing ramp would still be near 1.0 this early into 30 s).
        var slow = session.FadeClipAsync("c", 0f, TimeSpan.FromSeconds(30), stopWhenSilent: false);
        await Task.Delay(300);
        var midLevel = (await session.GetClipFadeLevelAsync("c"))!.Value;
        Assert.InRange(midLevel, 0.35f, 0.51f);

        // A fade UP from the reduced level works - and preempts the still-running slow fade, whose task
        // must complete (not deadlock) once displaced.
        await session.FadeClipAsync("c", 0.9f, TimeSpan.FromMilliseconds(200), stopWhenSilent: false);
        Assert.Equal(0.9f, (await session.GetClipFadeLevelAsync("c"))!.Value, 3);
        var winner = await Task.WhenAny(slow, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(slow, winner);
        await slow;
        Assert.True(Assert.Single(session.Snapshot()).IsActive); // stopWhenSilent false never released
    }

    [Fact]
    public async Task FadeClip_ToSilence_WithStopWhenSilent_ReleasesTheClipAtRampEnd()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);

        var fade = session.FadeClipAsync("c", 0f, TimeSpan.FromMilliseconds(300));
        var winner = await Task.WhenAny(fade, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(fade, winner);
        await fade;

        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive)); // released via the stop path
        Assert.Null(await session.GetClipFadeLevelAsync("c"));
    }

    [Fact]
    public async Task FadeClip_ToSilence_WithoutStopWhenSilent_KeepsTheClipRunning()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        await session.FadeClipAsync("c", 0f, TimeSpan.FromMilliseconds(200), stopWhenSilent: false);

        Assert.True(Assert.Single(session.Snapshot()).IsActive); // silent but still up
        Assert.Equal(0f, (await session.GetClipFadeLevelAsync("c"))!.Value, 3);
    }

    [Fact]
    public async Task OperatorStop_PreemptsARunningFadeCueRamp_WithoutDeadlock()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var fade = session.FadeClipAsync("c", 0.2f, TimeSpan.FromSeconds(30), stopWhenSilent: false);
        await Task.Delay(200); // let the fade-cue ramp start

        // The stop's TryBeginFadeOut claim preempts the fade cue: the stop fades and releases on its own
        // clock, and the displaced fade task completes instead of running its remaining ~30 s.
        var stop = session.StopAsync(fadeDuration: TimeSpan.FromMilliseconds(200));
        var stopWinner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(stop, stopWinner);
        await stop;
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));

        var fadeWinner = await Task.WhenAny(fade, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(fade, fadeWinner);
        await fade;
    }

    [Fact]
    public async Task FadeClip_ForACueThatIsNotPlaying_IsANoOp()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(OneCue("fake://x"));

        await session.FadeClipAsync("c", 0.5f, TimeSpan.FromMilliseconds(100)); // nothing active
        Assert.Null(await session.GetClipFadeLevelAsync("c"));
    }
}
