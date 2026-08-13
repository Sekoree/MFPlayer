using System.Collections.Concurrent;
using S.Media.Compositor;
using S.Media.Core.Video;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Session.Tests;

/// <summary>Dual-voice crossfade (Ideas/Dual-Voice-Crossfade-Design.md): a crossfade fire keeps the
/// displaced clip live in the group's Outgoing slot for the window and releases it at ramp end; a stop
/// mid-fade takes BOTH clips down on one stop clock; a second replacement hard-releases the current
/// outgoing (one outgoing max); an incoming open failure leaves the old Active untouched; and the
/// null-crossfade path stays the butt splice, byte for byte. The crossfade is audio AND video: the
/// incoming clip's layer opacities ramp 0→authored while the outgoing's ramp down with its tail (the
/// opacity tests observe the per-layer opacities the compositor is actually handed each composite).
/// Loop-with-crossfade (<see cref="ShowClipBinding.LoopCrossfade"/>) re-fires the SAME binding through
/// this machinery at each loop boundary.</summary>
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

    /// <summary>
    /// A tail that has gone quiet SAYS so, on <c>ClipTailEnded</c>.
    /// </summary>
    /// <remarks>
    /// Nothing reported it. The displaced clip leaves the transport at the handoff and takes its end monitor
    /// with it, so it can never reach <c>ClipNaturallyEnded</c> - which is right, it did not end naturally -
    /// and no other signal covered it. A host tracking what is sounding therefore kept the outgoing cue
    /// forever: reported from HaCue2 as the first item of a crossfaded playlist still sitting in the Active
    /// panel with its clock counting up, long after the second item had taken the room.
    /// </remarks>
    [Fact]
    public async Task CrossfadeTail_ReportsThatItStopped_WhenItsRampEnds()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        var ended = new ConcurrentQueue<string>();
        var natural = new ConcurrentQueue<string>();
        session.ClipTailEnded += id => ended.Enqueue(id);
        session.ClipNaturallyEnded += id => natural.Enqueue(id);

        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(400), FadeCurve.EqualPower));

        // Still fading: it is audible, so it must NOT be reported as stopped yet.
        Assert.Empty(ended);

        await releases.WaitForReleaseAsync("dev-c1", TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => ended.Count > 0, TimeSpan.FromSeconds(5), "the tail's end report");

        Assert.Equal("c1", Assert.Single(ended));

        // And on the tail event only. Natural end advances follow chains and playlist runs; the fire that
        // displaced this clip has already advanced them, and a second edge would fire the successor twice.
        Assert.Empty(natural);
        Assert.False(releases.IsReleased("dev-c2"), "the incoming clip was taken down with the tail");
    }

    /// <summary>
    /// The same cue on both sides of the handoff reports NOTHING - it never stopped.
    /// </summary>
    /// <remarks>
    /// A loop-with-crossfade wrap re-fires the same binding, so the finishing pass retires as a tail while
    /// the next pass of that cue is live. Taking the tail's end at face value would have a host drop a cue
    /// that is still playing - a bed that vanished from the Active panel at its first seamless wrap while
    /// still filling the room.
    /// </remarks>
    [Fact]
    public async Task ATailWhoseCueIsSoundingAgain_ReportsNothing()
    {
        var log = new CountingLeaseLog();
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(chunks: 1_000_000),
            new RecordingAudioBackend(),
            audioOutputFactory: log.BuildLease);
        var ended = new ConcurrentQueue<string>();
        session.ClipTailEnded += id => ended.Enqueue(id);

        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c1", 1, "BED")],
            Clips:
            [
                new ShowClipBinding("c1", "fake://bed")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-bed")],
                    Loop = true,
                    EndOffset = TimeSpan.FromMilliseconds(8_500),
                    LoopCrossfade = TimeSpan.FromMilliseconds(600),
                },
            ],
            Compositions: [], Routes: []));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        // Wait past the first wrap's tail release - the moment the naive version would have reported it.
        await WaitUntilAsync(
            () => log.Released("dev-bed") >= 1, TimeSpan.FromSeconds(25), "the finishing pass's release");

        Assert.Empty(ended);
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
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

    /// <summary>
    /// A rapid GO sequence keeps its tails overlapping up to the group's cap (D7 raised it from 1 to 3).
    /// This is the case the old single-tail policy handled badly: each new crossfade cut the previous
    /// tail while it was still near full level, which clicked.
    /// </summary>
    [Fact]
    public async Task RapidCrossfadeSequence_KeepsTailsOverlapping_UpToTheCap()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2", "c3", "c4"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));

        // Three crossfades in a row: c1, c2 and c3 are all still fading, c4 is active.
        foreach (var cue in new[] { "c2", "c3", "c4" })
            Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync(cue, TimeSpan.FromSeconds(30)));

        Assert.False(releases.IsReleased("dev-c1"), "c1 was cut despite fitting inside the tail cap");
        Assert.False(releases.IsReleased("dev-c2"));
        Assert.False(releases.IsReleased("dev-c3"));
        Assert.False(releases.IsReleased("dev-c4"));
    }

    [Fact]
    public async Task ReplacementBeyondTheTailCap_HardReleasesTheOldestOutgoing()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2", "c3", "c4", "c5"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        foreach (var cue in new[] { "c2", "c3", "c4" })
            Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync(cue, TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1"));

        // The fourth replacement pushes past the cap, so the OLDEST tail (c1) is hard-released and the
        // three most recent keep fading. The bound is what stops a stuck GO exhausting decoders.
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c5", TimeSpan.FromSeconds(30)));
        Assert.True(releases.IsReleased("dev-c1"), "the oldest outgoing was not hard-released at the cap");
        Assert.False(releases.IsReleased("dev-c2"), "a tail inside the cap was cut");
        Assert.False(releases.IsReleased("dev-c3"));
        Assert.False(releases.IsReleased("dev-c4"));
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

    // ---- Video side of the cross (layer opacities) ---------------------------------------------

    /// <summary>One composited layer as the compositor was actually handed it: the opacity the crossfade
    /// ramps wrote into the slot, AND the presentation time of the frame that layer PRESENTED. Opacity
    /// alone cannot see a frozen picture - a still frame fades out exactly like a moving one - so the
    /// frame's PTS is the only observable that distinguishes "the tail is playing" from "the tail is a
    /// held still".</summary>
    private readonly record struct LayerSample(float Opacity, TimeSpan FramePts);

    /// <summary>Observation seam for the video leg: the canvas compositor is wrapped so every composite
    /// records what each layer contributed. Each test composition gets a distinct canvas width, so the
    /// factory can key the recording queue per composition.</summary>
    private sealed class LayerRecordingCompositor(
        VideoFormat output, ConcurrentQueue<LayerSample[]> samples) : IVideoCompositor
    {
        private readonly CpuVideoCompositor _inner = new(output);

        public VideoFormat OutputFormat => _inner.OutputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => _inner.AcceptedLayerPixelFormats;
        public void Configure(VideoFormat output2) => _inner.Configure(output2);

        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
        {
            samples.Enqueue([.. layersBackToFront.Select(l => new LayerSample(l.Opacity, l.Frame.PresentationTime))]);
            return _inner.Composite(layersBackToFront, presentationTime);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>One composition per voice (distinct canvas widths), so samples attribute to the
    /// outgoing/incoming clip unambiguously. Video-only clips - the pure video leg, no audio riding along.
    /// <paramref name="outgoingStartOffset"/> starts the OUTGOING clip deep into its media, which is what
    /// makes the tail's own source time far away from the incoming clip's (the playlist-crossfade shape).</summary>
    private static ShowDocument VideoCues(double incomingOpacity = 1, TimeSpan outgoingStartOffset = default) => new(
        Version: 1,
        Cues: [new CueDefinition("c1", 1, "ONE"), new CueDefinition("c2", 2, "TWO")],
        Clips:
        [
            new ShowClipBinding("c1", "fake://c1", CompositionId: "out-comp")
            {
                StartOffset = outgoingStartOffset,
            },
            new ShowClipBinding("c2", "fake://c2", CompositionId: "in-comp")
            {
                Placement = new ShowVideoPlacement(Opacity: incomingOpacity),
            },
        ],
        Compositions:
        [
            new ShowComposition("out-comp", "Out", 64, 48, 30, 1),
            new ShowComposition("in-comp", "In", 96, 48, 30, 1),
        ],
        Routes: []);

    private static ShowSession BuildVideoSession(
        ConcurrentQueue<LayerSample[]> outSamples, ConcurrentQueue<LayerSample[]> inSamples) => new(
        FakeVideoDecoderProvider.Registry(frameCount: 3_000),
        compositorFactory: fmt => new ClipCompositionCompositor(
            new LayerRecordingCompositor(fmt, fmt.Width == 64 ? outSamples : inSamples),
            RequiresBgraLayerConversion: true, "TEST-LAYER-RECORDER"));

    /// <summary>The composition pump only composites while an output lease is attached.</summary>
    private static async Task AttachRecordingOutputsAsync(ShowSession session)
    {
        Assert.True(await session.AttachCompositionOutputAsync("out-comp", new DiscardingVideoOutput()));
        Assert.True(await session.AttachCompositionOutputAsync("in-comp", new DiscardingVideoOutput()));
    }

    /// <summary>Dequeues until a composite sample satisfies <paramref name="predicate"/> (consuming the
    /// backlog, so successive waits observe strictly later composites).</summary>
    private static async Task<LayerSample[]> WaitForSampleAsync(
        ConcurrentQueue<LayerSample[]> samples, Func<LayerSample[], bool> predicate, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            while (samples.TryDequeue(out var sample))
                if (predicate(sample))
                    return sample;
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Crossfade_RampsVideoOpacity_IncomingUpToAuthored_OutgoingDown()
    {
        var outSamples = new ConcurrentQueue<LayerSample[]>();
        var inSamples = new ConcurrentQueue<LayerSample[]>();
        await using var session = BuildVideoSession(outSamples, inSamples);
        await session.LoadDocumentAsync(VideoCues(incomingOpacity: 0.8));
        await AttachRecordingOutputsAsync(session);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        await WaitForSampleAsync(
            outSamples, s => s.Length == 1 && s[0].Opacity >= 0.99f, TimeSpan.FromSeconds(15),
            "the outgoing clip's full-opacity composite before the crossfade");

        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromMilliseconds(2_500), FadeCurve.EqualPower));

        // Incoming: attaches BLACK and ramps up - an intermediate sample proves the ramp (a pop to
        // full would jump 0→0.8 with nothing in between at the 30 fps composite rate vs 25 ms steps).
        await WaitForSampleAsync(
            inSamples, s => s.Length == 1 && s[0].Opacity is > 0.05f and < 0.7f, TimeSpan.FromSeconds(10),
            "an intermediate incoming opacity (fade-in mid-ramp)");
        // Outgoing: was at 1.0 - an intermediate sample proves its tail ramps down rather than vanishing.
        await WaitForSampleAsync(
            outSamples, s => s.Length == 1 && s[0].Opacity is > 0.05f and < 0.95f, TimeSpan.FromSeconds(10),
            "an intermediate outgoing opacity (tail mid-ramp)");
        // The incoming ramp tops out at the AUTHORED opacity (0.8), not a hardcoded 1.0.
        await WaitForSampleAsync(
            inSamples, s => s.Length == 1 && s[0].Opacity is >= 0.79f and <= 0.81f, TimeSpan.FromSeconds(10),
            "the incoming clip settling at its authored opacity");
    }

    [Fact]
    public async Task Crossfade_OutgoingVideoTail_KeepsPlaying_InsteadOfFreezingOnAStill()
    {
        // Regression (post-implementation review of the dual-voice round): the outgoing clip keeps its
        // layers AND its transport-timeline claim, but ReplaceAsync re-binds that SAME timeline to the
        // incoming clip's playhead - and the composition pump feeds that source time to every
        // master-aligned slot. The tail's own frames then look far in the FUTURE, master alignment
        // rejects them all and keeps re-presenting the frame it already held, so the tail faded out as a
        // frozen still. Opacity cannot see this (a still fades exactly like a moving picture), so this
        // asserts on the PTS of the frame each composite actually presented.
        var outSamples = new ConcurrentQueue<LayerSample[]>();
        var inSamples = new ConcurrentQueue<LayerSample[]>();
        await using var session = BuildVideoSession(outSamples, inSamples);
        // The outgoing clip starts 40 s into its media (the "A at 3:12" shape) while the incoming starts
        // at 0, so the incoming playhead cannot possibly catch up to the tail's frames during the window.
        await session.LoadDocumentAsync(VideoCues(outgoingStartOffset: TimeSpan.FromSeconds(40)));
        await AttachRecordingOutputsAsync(session);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        await WaitForSampleAsync(
            outSamples,
            s => s.Length == 1 && s[0].FramePts >= TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(20),
            "the outgoing clip presenting frames from its trimmed-in position");

        // A 30 s window keeps the tail alive for the whole measurement.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(30), FadeCurve.EqualPower));

        // Drain, then take the first POST-handoff composite as the baseline…
        while (outSamples.TryDequeue(out _)) { }
        var baseline = await WaitForSampleAsync(
            outSamples, s => s.Length == 1, TimeSpan.FromSeconds(10),
            "the first outgoing composite after the handoff");

        // …and require the tail's presented frame to move on. Frozen ⇒ every later sample repeats the
        // baseline PTS forever and this times out.
        var advanced = await WaitForSampleAsync(
            outSamples,
            s => s.Length == 1 && s[0].FramePts >= baseline[0].FramePts + TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(10),
            "the outgoing tail's presented frame to keep advancing (it froze on a still)");
        // Still the tail, not the incoming voice: it is fading, and it is on the outgoing composition.
        Assert.True(advanced[0].Opacity < 1f, "the outgoing tail was not ramping down");
    }

    [Fact]
    public async Task StopMidCrossfade_RampsBothVoicesOpacitiesDown_OnTheStopClock()
    {
        var outSamples = new ConcurrentQueue<LayerSample[]>();
        var inSamples = new ConcurrentQueue<LayerSample[]>();
        await using var session = BuildVideoSession(outSamples, inSamples);
        await session.LoadDocumentAsync(VideoCues());
        await AttachRecordingOutputsAsync(session);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        await WaitForSampleAsync(
            outSamples, s => s.Length == 1 && s[0].Opacity >= 0.99f, TimeSpan.FromSeconds(15),
            "the outgoing clip's full-opacity composite before the crossfade");

        // A LONG window, so neither voice could reach a low opacity through the crossfade itself
        // within this test: the incoming rises slowly, the outgoing falls slowly.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(20), FadeCurve.EqualPower));
        await WaitForSampleAsync(
            inSamples, s => s.Length == 1 && s[0].Opacity >= 0.15f, TimeSpan.FromSeconds(15),
            "the incoming clip part-way up before the stop");

        // Drain both queues so every sample below only proves POST-stop ramping (the incoming's own
        // early fade-in backlog contains low values that would satisfy the predicates vacuously).
        while (outSamples.TryDequeue(out _)) { }
        while (inSamples.TryDequeue(out _)) { }
        var stop = session.StopAsync(fadeDuration: TimeSpan.FromSeconds(1));

        // Both voices reach near-black within the ~1 s stop clock - impossible on the 20 s crossfade
        // clock (the outgoing would still be ≥0.9, and the incoming only ever RISES on it).
        await WaitForSampleAsync(
            inSamples, s => s.Length == 1 && s[0].Opacity <= 0.05f, TimeSpan.FromSeconds(10),
            "the incoming clip ramping down to black on the stop clock");
        await WaitForSampleAsync(
            outSamples, s => s.Length == 1 && s[0].Opacity <= 0.1f, TimeSpan.FromSeconds(10),
            "the outgoing tail ramping down to black on the stop clock");

        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(stop, winner);
        await stop;
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
    }

    // ---- Audio leg of the outgoing ramp --------------------------------------------------------

    [Fact]
    public async Task Crossfade_OutgoingAudioTail_ActuallyRampsToSilence()
    {
        // The opacity tests cover the video leg; this is the audio one, measured at the device. A
        // constant-amplitude tone means the output peak IS the installed route gain, so a tail that was
        // hard-cut at level (rather than ramped) never shows a quiet-but-still-flowing window before its
        // release - which is exactly what this asserts.
        var outputs = new ConcurrentDictionary<string, PeakAudioOutput>(StringComparer.Ordinal);
        var released = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (deviceId, format) => new ClipAudioOutputLease(
                outputs.GetOrAdd(deviceId, _ => new PeakAudioOutput(format)),
                DisposeOutputOnRuntimeDispose: false,
                Release: () => released.TryAdd(deviceId, 0)));
        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c1", 1, "One"), new CueDefinition("c2", 2, "Two")],
            Clips:
            [
                new ShowClipBinding("c1", "tone://1") { AudioRoutes = [new ShowClipAudioRoute("dev-c1")] },
                new ShowClipBinding("c2", "tone://2") { AudioRoutes = [new ShowClipAudioRoute("dev-c2")] },
            ],
            Compositions: [], Routes: []));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        await WaitUntilAsync(
            () => outputs.TryGetValue("dev-c1", out var o) && o.Peak > ToneAudioDecoderProvider.Amplitude * 0.5f,
            TimeSpan.FromSeconds(10), "the outgoing clip playing at full level");

        // A 3 s linear window: the tail spends its last ~450 ms below 15 % of full, i.e. several of the
        // 100 ms sampling windows below land there before the ramp's completion releases the clip.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(3), FadeCurve.Linear));

        var sawSilentTail = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!released.ContainsKey("dev-c1"))
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the outgoing tail's ramp");
            outputs["dev-c1"].Reset();
            await Task.Delay(100);
            var peak = outputs["dev-c1"].Peak;
            // > 0 excludes the post-release windows (no submits at all), which would pass vacuously.
            if (peak > 0f && peak < ToneAudioDecoderProvider.Amplitude * 0.15f)
            {
                sawSilentTail = true;
                break;
            }
        }

        Assert.True(sawSilentTail, "the outgoing tail was cut at level instead of ramped to silence");
    }

    // ---- Teardown while a crossfade is in flight ------------------------------------------------

    [Fact]
    public async Task DisposeMidCrossfade_ReleasesBothVoices()
    {
        // The tail is fire-and-forget: nothing but the group's own teardown owns it. Session disposal
        // must therefore reach it too, or a crossfaded shutdown leaks the outgoing clip's device lease
        // (and its player) for the rest of the process.
        var releases = new ReleaseLog();
        var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1")); // mid-crossfade

        await session.DisposeAsync();

        Assert.True(releases.IsReleased("dev-c1"), "the outgoing tail's lease survived disposal");
        Assert.True(releases.IsReleased("dev-c2"), "the active clip's lease survived disposal");
    }

    [Fact]
    public async Task DocumentReloadMidCrossfade_ReleasesBothVoices()
    {
        // Same contract on the other teardown path: a reload retires every group, so the still-fading
        // outgoing clip must go with it rather than playing on under the new document.
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Cues("c1", "c2"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        Assert.False(releases.IsReleased("dev-c1"));

        await session.LoadDocumentAsync(Cues("c1", "c2"));

        Assert.True(releases.IsReleased("dev-c1"), "the outgoing tail survived the reload");
        Assert.True(releases.IsReleased("dev-c2"), "the active clip survived the reload");
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
    }

    // ---- Loop-with-crossfade -------------------------------------------------------------------

    /// <summary>Counting variant of <see cref="ReleaseLog"/>: a loop-crossfade re-fires the SAME cue on
    /// the SAME device, so overlap is proven by counts - a second lease created while zero are released.</summary>
    private sealed class CountingLeaseLog
    {
        private readonly ConcurrentDictionary<string, int> _created = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _released = new(StringComparer.Ordinal);

        public int Created(string deviceId) => _created.GetValueOrDefault(deviceId);
        public int Released(string deviceId) => _released.GetValueOrDefault(deviceId);

        public ClipAudioOutputLease? BuildLease(string deviceId, S.Media.Core.Audio.AudioFormat format)
        {
            _created.AddOrUpdate(deviceId, 1, static (_, n) => n + 1);
            return new(new SinkAudioOutput(format),
                DisposeOutputOnRuntimeDispose: false,
                Release: () => _released.AddOrUpdate(deviceId, 1, static (_, n) => n + 1));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task LoopWithCrossfade_OverlapsTheTwoPasses_AndKeepsLooping()
    {
        var log = new CountingLeaseLog();
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(chunks: 1_000_000),
            new RecordingAudioBackend(),
            audioOutputFactory: log.BuildLease);
        var naturalEnds = 0;
        session.ClipNaturallyEnded += _ => Interlocked.Increment(ref naturalEnds);
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c1", 1, "BED")],
            Clips:
            [
                new ShowClipBinding("c1", "fake://bed")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-bed")],
                    Loop = true,
                    // The fake source reports 10 s; trim the pass to ~1.5 s so several wraps fit the test.
                    EndOffset = TimeSpan.FromMilliseconds(8_500),
                    LoopCrossfade = TimeSpan.FromMilliseconds(600),
                },
            ],
            Compositions: [], Routes: []);
        await session.LoadDocumentAsync(doc);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        Assert.Equal(1, log.Created("dev-bed"));

        // First boundary: the SAME binding re-fires as the incoming voice - a second lease exists while
        // the first is still unreleased. That simultaneous pair IS the overlap window.
        await WaitUntilAsync(() => log.Created("dev-bed") >= 2, TimeSpan.FromSeconds(15), "the loop-crossfade re-fire");
        Assert.Equal(0, log.Released("dev-bed"));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);

        // The finishing pass releases at ramp end; the new pass plays on as the one Active.
        await WaitUntilAsync(() => log.Released("dev-bed") == 1, TimeSpan.FromSeconds(10), "the outgoing pass's release");
        Assert.True(Assert.Single(session.Snapshot()).IsActive);

        // A third instance can only appear if the second pass restarted at the trim-in and reached the
        // NEXT boundary - position restarts and the loop keeps going, crossfaded every wrap.
        await WaitUntilAsync(() => log.Created("dev-bed") >= 3, TimeSpan.FromSeconds(15), "the second crossfaded wrap");

        // A crossfaded wrap is still one loop pass: the clip never "naturally ended".
        Assert.Equal(0, Volatile.Read(ref naturalEnds));
    }
}
