using System.Collections.Concurrent;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>Dual-voice crossfade (Ideas/Dual-Voice-Crossfade-Design.md): a crossfade fire keeps the
/// displaced clip live in the group's Outgoing slot for the window and releases it at ramp end; a stop
/// mid-fade takes BOTH clips down on one stop clock; a second replacement hard-releases the current
/// outgoing (one outgoing max); an incoming open failure leaves the old Active untouched; and the
/// null-crossfade path stays the butt splice, byte for byte.</summary>
public sealed class CrossfadeTests
{
    /// <summary>Observation seam: each cue routes to its own device id through the session's
    /// audio-output factory, whose lease Release hook records when that clip's output was torn down -
    /// the only public signal for when a (non-Active) outgoing clip actually released.</summary>
    private sealed class ReleaseLog
    {
        private readonly ConcurrentDictionary<string, int> _released = new(StringComparer.Ordinal);

        public bool IsReleased(string deviceId) => _released.ContainsKey(deviceId);

        public ClipAudioOutputLease? BuildLease(string deviceId, S.Media.Core.Audio.AudioFormat format) =>
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

    private static ShowDocument Cues(params string[] cueIds) => new(
        Version: 1,
        Cues: [.. cueIds.Select((id, i) => new CueDefinition(id, i + 1, id.ToUpperInvariant()))],
        Clips:
        [
            .. cueIds.Select(id => new ShowClipBinding(id, $"fake://{id}")
            {
                AudioRoutes = [new ShowClipAudioRoute(DeviceId: $"dev-{id}")],
            }),
        ],
        Compositions: [], Routes: []);

    private static ShowSession BuildSession(ReleaseLog releases, IMediaRegistry? registry = null) => new(
        registry ?? FakeAudioDecoderProvider.Registry(chunks: 100_000),
        new RecordingAudioBackend(),
        audioOutputFactory: releases.BuildLease);

    [Fact]
    public async Task FireWithCrossfade_KeepsTheOutgoingClipLive_AndReleasesItAtRampEnd()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(600), FadeCurve.EqualPower));

        // Both clips are up: c2 is the Active (transport target), c1's outputs are NOT released yet -
        // it keeps playing as the outgoing tail for the window.
        Assert.False(releases.IsReleased("dev-c1"), "the outgoing clip released before its ramp ended");
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
        Assert.NotNull(await session.GetClipFadeLevelAsync("c2")); // c2 IS the active clip

        // Ramp end: the outgoing releases through the normal teardown; the incoming is untouched.
        await releases.WaitForReleaseAsync("dev-c1", TimeSpan.FromSeconds(20));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
        Assert.False(releases.IsReleased("dev-c2"));
    }

    [Fact]
    public async Task FireWithNullCrossfade_IsTheButtSplice_OldClipReleasedBeforeTheFireReturns()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        // The overload with a null window must be exactly the historical replacement: the displaced
        // clip is released synchronously inside the commit, before the fire's task completes.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", crossfade: null));
        Assert.True(releases.IsReleased("dev-c1"));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
    }

    [Fact]
    public async Task CrossfadeImpliesAFadeIn_OnTheIncomingClip()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(1f, (await session.GetClipFadeLevelAsync("c1"))!.Value, 3);

        // A long window so the fade-in is provably mid-ramp right after the fire returns: the binding
        // has no FadeIn of its own, so the crossfade implies one over the same window.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        var level = (await session.GetClipFadeLevelAsync("c2"))!.Value;
        Assert.True(level < 0.9f, $"the incoming clip should still be fading in (level {level})");
    }

    [Fact]
    public async Task StopMidCrossfade_TakesBothClipsDown_OnOneStopClock()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1")); // mid-crossfade

        // The stop claims BOTH clips: the active fades on the explicit stop clock and the outgoing tail
        // rides the same clock (not its remaining 30 s crossfade), so the whole group is silent and
        // released when the stop's task completes.
        var stop = session.StopAsync(fadeDuration: TimeSpan.FromMilliseconds(300));
        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(stop, winner);
        await stop;

        Assert.True(releases.IsReleased("dev-c1"), "the outgoing tail survived the stop");
        Assert.True(releases.IsReleased("dev-c2"), "the active clip survived the stop");
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
    }

    [Fact]
    public async Task HardCutStopMidCrossfade_ReleasesBothClipsImmediately()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));

        var stop = session.StopAllAsync(TimeSpan.Zero); // Panic with a 0 ms setting - no ramp at all
        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(stop, winner);
        await stop;

        Assert.True(releases.IsReleased("dev-c1"));
        Assert.True(releases.IsReleased("dev-c2"));
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
    }

    [Fact]
    public async Task SecondReplaceMidCrossfade_HardReleasesTheFirstOutgoing()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2", "c3"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1")); // c1 is the outgoing tail

        // One outgoing max: a second crossfade replacement hard-releases the still-fading c1 before c2
        // becomes the new outgoing (a triple overlap is deliberately out of scope).
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c3", TimeSpan.FromSeconds(30)));
        Assert.True(releases.IsReleased("dev-c1"), "the first outgoing was not hard-released");
        Assert.False(releases.IsReleased("dev-c2")); // c2 is the new outgoing tail
        Assert.False(releases.IsReleased("dev-c3"));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
    }

    [Fact]
    public async Task CrossfadeFire_WhoseOpenFails_LeavesTheActiveClipUntouched()
    {
        var releases = new ReleaseLog();
        // The failing provider is registered FIRST (ties go to the earliest-registered provider, and the
        // fake probes every audio URI); it owns only the fail:// scheme.
        var registry = MediaRegistry.Build(b => b
            .AddDecoder(new FailingOpenProvider())
            .AddDecoder(new FakeAudioDecoderProvider(chunks: 100_000)));
        var doc = new ShowDocument(
            Version: 1,
            Cues:
            [
                new CueDefinition("c1", 1, "One"),
                new CueDefinition("c2", 2, "Two", FaultPolicy: CueFaultPolicy.Continue),
            ],
            Clips:
            [
                new ShowClipBinding("c1", "fake://1")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-c1")],
                },
                new ShowClipBinding("c2", "fail://2"),
            ],
            Compositions: [], Routes: []);
        await using var session = BuildSession(releases, registry);
        await session.LoadDocumentAsync(doc);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        // The incoming open fails before any commit: no fade began, no handoff happened - the old
        // Active keeps playing at full level (fail loud via the fire status).
        Assert.Equal(CueExecutionStatus.Failed,
            await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(500)));
        Assert.False(releases.IsReleased("dev-c1"));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
        Assert.Equal(1f, (await session.GetClipFadeLevelAsync("c1"))!.Value, 3);
    }

    [Fact]
    public async Task CrossfadeOntoAnIdleGroup_IsAPlainFire()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1"));

        // Nothing to displace: the window is ignored and the clip fires at full level immediately.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1", TimeSpan.FromSeconds(30)));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
        Assert.Equal(1f, (await session.GetClipFadeLevelAsync("c1"))!.Value, 3);
    }
}
