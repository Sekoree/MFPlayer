using System.Diagnostics;
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

    /// <summary>The bus entries belonging to cue <paramref name="cueId"/>'s voices. A transport label carries
    /// a per-VOICE uniquifier ("cue:main:c1#3"): a cue can legitimately have two voices on the bus at once (a
    /// loop crossfade overlaps a cue with itself), and two identical labels would defeat the duplicate check
    /// that makes a lingering registration fail loudly - so a cue is matched by prefix, never by exact
    /// label.</summary>
    private static IReadOnlyList<SoundingSourceInfo> VoicesOfCue(
        IReadOnlyList<SoundingSourceInfo> bus, string cueId) =>
        [.. bus.Where(s => s.Label.StartsWith(CueSourcePrefix + cueId, StringComparison.Ordinal))];

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
        var remaining = CueSources(await session.GetSoundingSourcesAsync());
        Assert.Single(remaining); // one voice left on the bus…
        Assert.Single(VoicesOfCue(remaining, "c2")); // …and it is the incoming cue's
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
        var tailOnly = CueSources(await session.GetSoundingSourcesAsync());
        Assert.Single(tailOnly);
        Assert.Single(VoicesOfCue(tailOnly, "c1"));

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

        // One program source PER VOICE, labelled by group + cue + a per-voice uniquifier: the whole point is
        // that the tail is a first-class entry the fader, the stops and a host status panel can all see.
        var sources = CueSources(await session.GetSoundingSourcesAsync());
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(SoundingSourceRole.Program, s.Role));
        Assert.All(sources, s => Assert.True(s.IsSounding));
        Assert.Single(VoicesOfCue(sources, "c1"));
        Assert.Single(VoicesOfCue(sources, "c2"));

        // The tail rides the fader through its own registration - exactly once (its ramp scalar is still
        // ≈1 this early in a 30 s window, so the reading isolates the trim factor).
        await session.SetMasterTrimAsync(0.5f);
        var tail = Assert.Single(VoicesOfCue(await session.GetSoundingSourcesAsync(), "c1"));
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
        Assert.Single(sources);
        Assert.Single(VoicesOfCue(sources, "c2"));
    }

    // ---- Concurrent stops on ONE voice: the loser must not cut the winner's ramp --------------------

    /// <summary>Traces a voice's composed bus level until it leaves the bus, so a release can be judged by
    /// the level the voice was AT when it happened. A hard cut and a completed ramp both end with the voice
    /// gone; only the level on the way out tells them apart.</summary>
    private sealed class LevelTrace : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<float> _levels = new();
        private readonly Task _poller;

        public LevelTrace(ShowSession session, string cueId)
        {
            _poller = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var voices = VoicesOfCue(await session.GetSoundingSourcesAsync().ConfigureAwait(false), cueId);
                    if (voices.Count > 0)
                        _levels.Enqueue(voices[0].Level);
                    await Task.Delay(15, _stop.Token).ConfigureAwait(false);
                }
            }, _stop.Token);
        }

        public float LowestLevelSeen => _levels.IsEmpty ? float.NaN : _levels.Min();

        public async Task WaitUntilBelowAsync(float level, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (LowestLevelSeen is var seen && (float.IsNaN(seen) || seen > level))
            {
                Assert.True(DateTime.UtcNow < deadline, $"the level never fell below {level} (lowest {seen})");
                await Task.Delay(15);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _poller; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task ASecondStopMidRamp_TakesOverTheFade_InsteadOfHardCuttingTheFirstStopsRamp()
    {
        // The fade claim used to be a permanent one-shot with no deadline, and StopVoicesCoreAsync released
        // every voice it selected - INCLUDING one whose claim it lost. So a second stop landing during a 5 s
        // stop fade skipped the ramp and released the voice on the spot, chopping the first stop's ramp off
        // at whatever level it had reached: an audible click on a control surface where double-tapping Stop,
        // or hitting Stop on a group and then Stop-all, is entirely ordinary. (Panic is the ONE case that
        // must still cut, and it does - it claims with an earlier deadline and owns the release.)
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        await using var trace = new LevelTrace(session, "c1");
        // A 30 s fade against a 1 s one, so the two possible correct-looking outcomes are far apart in time:
        // taking the ramp OVER finishes in ~1 s, merely waiting for the incumbent would take ~29 s. Both
        // would end in silence, so a level assertion alone cannot tell them apart - the elapsed time can.
        var slowStop = session.StopAsync(fadeDuration: TimeSpan.FromSeconds(30));

        // Let the 30 s ramp get provably under way while the voice is still well up, so a hard cut here is
        // unmistakably a cut and not a nearly-finished fade.
        await trace.WaitUntilBelowAsync(0.95f, TimeSpan.FromSeconds(10));

        var started = Stopwatch.StartNew();
        await session.StopAsync(fadeDuration: TimeSpan.FromSeconds(1));
        var quickStopTook = started.Elapsed;
        await slowStop;

        // Both stops have returned, so "stopped means stopped" still holds…
        await releases.WaitForReleaseAsync("dev-c1", TimeSpan.FromSeconds(10));
        Assert.Empty(CueSources(await session.GetSoundingSourcesAsync()));
        // …the voice rode a ramp to silence on its way out rather than being cut at ~0.95…
        Assert.InRange(trace.LowestLevelSeen, 0f, 0.05f);
        // …and the SHORT stop was actually short: it took the ramp over from where the long one had reached
        // instead of inheriting its remaining ~29 s.
        Assert.True(
            quickStopTook < TimeSpan.FromSeconds(10),
            $"the 1 s stop took {quickStopTook.TotalSeconds:F1} s - it waited for the 30 s fade "
            + "instead of superseding it");
    }

    [Fact]
    public async Task PanicDuringAStopFade_StillCutsTheVoice_AndItIsGoneWhenPanicReturns()
    {
        // The other side of the same claim: an EARLIER deadline supersedes, so the safety button keeps its
        // reach even though a longer stop already owns the voice. This is the transport twin of
        // SoundingBusTests.PanicAfterAStopFade_CutsASoundboardVoiceToo_AndItIsGoneWhenPanicReturns.
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        await using var trace = new LevelTrace(session, "c1");
        var slowStop = session.StopAsync(fadeDuration: TimeSpan.FromSeconds(30));
        await trace.WaitUntilBelowAsync(0.99f, TimeSpan.FromSeconds(10));

        await session.StopAllAsync(TimeSpan.Zero); // Panic
        // Gone the moment Panic returns - not 30 s later.
        Assert.Empty(CueSources(await session.GetSoundingSourcesAsync()));
        Assert.True(releases.IsReleased("dev-c1"), "Panic returned while the voice was still sounding");
        await slowStop;
    }
}
