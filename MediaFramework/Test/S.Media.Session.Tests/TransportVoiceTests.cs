using System.Collections.Concurrent;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// A transport group owns a LIST of voices (Ideas/Structural-Refactor-Plan-2026-07-29.md §A): each voice
/// carries its own state machine and its own level state, and <c>Active</c> is only "the voice transport
/// commands target". These are the five symptoms the old Active + Outgoing slot pair produced, one test
/// each - every one of them fails on the pre-refactor code.
/// <para>The crossfade behaviour itself (window, opacity crossing, one-tail policy, loop-with-crossfade)
/// stays pinned by <see cref="CrossfadeTests"/>; this file only covers what the slot shape made impossible.</para>
/// </summary>
public sealed class TransportVoiceTests
{
    /// <summary>Observation seam: each cue routes to its own device id through the session's audio-output
    /// factory, whose lease Release hook records when that clip's output was torn down - the only public
    /// signal for when a non-Active voice actually released.</summary>
    private sealed class ReleaseLog
    {
        private readonly ConcurrentDictionary<string, int> _released = new(StringComparer.Ordinal);

        public bool IsReleased(string deviceId) => _released.ContainsKey(deviceId);

        public ClipAudioOutputLease? BuildLease(string deviceId, AudioFormat format) =>
            new(new SinkAudioOutput(format),
                DisposeOutputOnRuntimeDispose: false,
                Release: () => _released.TryAdd(deviceId, 0));

        public async Task WaitForReleaseAsync(string deviceId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!IsReleased(deviceId))
            {
                Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for device '{deviceId}' to release");
                await Task.Delay(25);
            }
        }
    }

    private const string CueSourcePrefix = "cue:main:";

    private static ShowDocument Cues(params string[] cueIds) => new(
        Version: 1,
        Cues:
        [
            .. cueIds.Select((id, i) => new CueDefinition(
                id, i + 1, id.ToUpperInvariant(), FaultPolicy: CueFaultPolicy.Continue)),
        ],
        Clips:
        [
            .. cueIds.Select(id => new ShowClipBinding(id, $"fake://{id}")
            {
                AudioRoutes = [new ShowClipAudioRoute(DeviceId: $"dev-{id}")],
            }),
        ],
        Compositions: [], Routes: []);

    private static ShowSession BuildSession(ReleaseLog releases) => new(
        FakeAudioDecoderProvider.Registry(chunks: 100_000),
        new RecordingAudioBackend(),
        audioOutputFactory: releases.BuildLease);

    /// <summary>Fires <c>c1</c>, then crossfades <c>c2</c> over a window long enough that the tail is
    /// provably still live for the whole test.</summary>
    private static async Task<ShowSession> BuildMidCrossfadeAsync(ReleaseLog releases)
    {
        var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1"), "the tail released before the test began");
        return session;
    }

    private static IReadOnlyList<SoundingSourceInfo> CueSources(IReadOnlyList<SoundingSourceInfo> bus) =>
        [.. bus.Where(s => s.Label.StartsWith(CueSourcePrefix, StringComparison.Ordinal))];

    // ---- Symptom 1: a commit-path fault between the handoff and the ramp orphaned the tail -----------

    [Fact]
    public async Task CommitFaultRightAfterAHandoff_StillReleasesTheTail_InsteadOfOrphaningIt()
    {
        // The handoff and the arming of the displaced voice's release ramp used to be two statements in
        // CommitClipAsync with the rest of the commit between them. Anything throwing in that window left
        // the tail in the Outgoing slot with NO ramp: frozen at its handoff level, never released, and
        // unreachable (Active was already the incoming clip). The ramp is now armed INSIDE the handoff, so
        // the window does not exist.
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        session.PostCommitFault = _ => throw new InvalidOperationException("injected commit fault");
        Assert.Equal(
            CueExecutionStatus.Failed,
            await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(400)));

        // The tail comes down on the ramp the handoff armed…
        await releases.WaitForReleaseAsync("dev-c1", TimeSpan.FromSeconds(10));
        // …and the half-committed incoming voice is released exactly once by the failed commit, leaving
        // the group idle rather than holding a clip whose outputs were torn down under it.
        await releases.WaitForReleaseAsync("dev-c2", TimeSpan.FromSeconds(10));
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
        Assert.Empty(CueSources(await session.GetSoundingSourcesAsync()));
    }

    // ---- Symptom 2: StopCueAsync matched only the Active clip ---------------------------------------

    [Fact]
    public async Task StopCueAsync_StopsTheCue_EvenWhileItIsTheCrossfadeTail()
    {
        var releases = new ReleaseLog();
        await using var session = await BuildMidCrossfadeAsync(releases);

        // Per-cue stop used to match group.Active?.Spec.Id only, so a cue that had just been crossfaded
        // out could not be stopped at all - it played on for the rest of its window.
        await session.StopCueAsync("c1");

        Assert.True(releases.IsReleased("dev-c1"), "the cue could not be stopped while it was the tail");
        Assert.False(releases.IsReleased("dev-c2"), "stopping the tail took the incoming clip with it");
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
        Assert.Equal(CueSourcePrefix + "c2", Assert.Single(CueSources(await session.GetSoundingSourcesAsync())).Label);
    }

    [Fact]
    public async Task StopCueAsync_StillStopsTheCueWhenItIsTheActiveClip()
    {
        // The other half of the same rule: widening the match must not lose the ordinary case.
        var releases = new ReleaseLog();
        await using var session = await BuildMidCrossfadeAsync(releases);

        await session.StopCueAsync("c2");

        Assert.True(releases.IsReleased("dev-c2"));
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
        Assert.False(releases.IsReleased("dev-c1"), "stopping the active clip hard-cut the still-fading tail");
    }

    // ---- Symptoms 3 + 4: releasing the ACTIVE voice must not hard-cut a tail; Panic must reach one ----

    [Fact]
    public async Task FadeCueStopOfTheIncomingClip_LeavesTheStillFadingTailAlone_AndPanicStillReachesIt()
    {
        var releases = new ReleaseLog();
        await using var session = await BuildMidCrossfadeAsync(releases);

        // stopWhenSilent funnelled into ReplaceAsync(null), which hard-released whatever sat in the
        // Outgoing slot - the tail was cut at whatever level its ramp had reached (an audible click).
        // The incoming clip's natural end took the same path.
        await session.FadeClipAsync("c2", 0f, TimeSpan.FromMilliseconds(200));

        Assert.True(releases.IsReleased("dev-c2"));
        Assert.False(releases.IsReleased("dev-c1"), "the still-fading tail was hard-released with the active clip");
        // The group is now TAIL-ONLY: nothing is Active, and one voice is still sounding. Panic must
        // reach it (the hole the old StopAllAsync group filter left open by construction).
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
        Assert.Equal(CueSourcePrefix + "c1", Assert.Single(CueSources(await session.GetSoundingSourcesAsync())).Label);

        await session.StopAllAsync(TimeSpan.Zero);

        Assert.True(releases.IsReleased("dev-c1"), "Panic left a tail-only group sounding");
        Assert.Empty(CueSources(await session.GetSoundingSourcesAsync()));
    }

    // ---- Symptom 5: the tail was not addressable at all ---------------------------------------------

    [Fact]
    public async Task EveryVoiceRegistersOnTheLevelStopBus_SoTheTailIsAddressable()
    {
        var releases = new ReleaseLog();
        await using var session = await BuildMidCrossfadeAsync(releases);

        // One program source PER VOICE, labelled by group + cue: the whole point is that the tail is a
        // first-class entry the fader, the stops and a host status panel can all see.
        var sources = CueSources(await session.GetSoundingSourcesAsync());
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(SoundingSourceRole.Program, s.Role));
        Assert.All(sources, s => Assert.True(s.IsSounding));
        Assert.Contains(sources, s => s.Label == CueSourcePrefix + "c1");
        Assert.Contains(sources, s => s.Label == CueSourcePrefix + "c2");

        // The tail rides the fader through its own registration - exactly once (its ramp scalar is still
        // ≈1 this early in a 30 s window, so the reading isolates the trim factor).
        await session.SetMasterTrimAsync(0.5f);
        var tail = Assert.Single(
            CueSources(await session.GetSoundingSourcesAsync()), s => s.Label == CueSourcePrefix + "c1");
        Assert.InRange(tail.Level, 0.4f, 0.5f);
    }

    [Fact]
    public async Task AReleasedVoiceLeavesTheBus_SoACrossfadeSettlesBackToOneEntry()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(400)));

        await releases.WaitForReleaseAsync("dev-c1", TimeSpan.FromSeconds(15));

        // A registration that outlived its voice is a write to a dead player; the count is the assertion.
        var sources = CueSources(await session.GetSoundingSourcesAsync());
        Assert.Equal(CueSourcePrefix + "c2", Assert.Single(sources).Label);
    }
}
