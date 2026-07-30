using System.Collections.Concurrent;
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

    /// <summary>A two-tone-cue session, each cue on its own peak-reading device - the crossfade tests read
    /// the outgoing tail's gain on <c>dev-c1</c> while <c>c2</c> fades in on <c>dev-c2</c>.</summary>
    private static (ShowSession Session, ConcurrentDictionary<string, PeakAudioOutput> Outputs)
        BuildCrossfadeToneSession()
    {
        var outputs = new ConcurrentDictionary<string, PeakAudioOutput>(StringComparer.Ordinal);
        var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (deviceId, format) => new ClipAudioOutputLease(
                outputs.GetOrAdd(deviceId, _ => new PeakAudioOutput(format))));
        return (session, outputs);
    }

    private static ShowDocument TwoToneCues() => new(
        Version: 1,
        Cues: [new CueDefinition("c1", 1, "One"), new CueDefinition("c2", 2, "Two")],
        Clips:
        [
            new ShowClipBinding("c1", "tone://1")
            {
                AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-c1")],
            },
            new ShowClipBinding("c2", "tone://2")
            {
                AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-c2")],
            },
        ],
        Compositions: [], Routes: []);

    [Fact]
    public async Task MasterTrim_AppliesExactlyOnce_ToTheOutgoingCrossfadeTail()
    {
        // Regression (post-implementation review of the dual-voice round): the crossfade handoff froze
        // the outgoing tail's level as EffectiveAudioLevel - which already CONTAINS the master trim -
        // and the outgoing ramp then multiplied the live trim again, so a 0.5 fader halved the tail
        // twice (0.25 instead of 0.5). Measured at the output: a constant-amplitude tone clip's peak
        // must sit at tone × trim both while ACTIVE and as the outgoing tail of a long crossfade.
        var (session, outputs) = BuildCrossfadeToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(TwoToneCues());

        var expected = ToneAudioDecoderProvider.Amplitude * 0.5f; // tone × trim
        await session.SetMasterTrimAsync(0.5f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        _ = await WaitForPeakAsync(outputs, "dev-c1");
        Assert.Equal(0.5f, (await session.GetClipAudioLevelsAsync("c1"))!.EffectiveLevel, 3);
        // The routes ATTACH at the composed level, so there is no untrimmed buffer to drain and no need to
        // wait for a window to settle: the max-EVER peak (never reset) is both simpler and stricter - it
        // covers every buffer the device has received since it was created.
        await AssertPeakRisesToAsync(outputs["dev-c1"], expected - 0.1f, expected + 0.05f, "ACTIVE");

        // A 30 s window keeps the outgoing ramp scalar ≈1 for the whole measurement, so the tail's
        // peak isolates the trim factor: ≈0.4 correct, ≈0.2 with the trim applied twice. The tail's ramp
        // only ever LOWERS the level from here, so a fresh window is what shows it is still at the trim
        // (the max-ever ceiling above already covers the "never louder" half).
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        await Task.Delay(250); // several 25 ms ramp steps have rewritten the tail's route gains
        await AssertSettledPeakAsync(outputs["dev-c1"], expected - 0.1f, expected + 0.05f, "TAIL");
    }

    [Fact]
    public async Task MasterTrim_MovedDuringACrossfade_RidesTheTail_WithoutFightingItsRamp()
    {
        // The companion case to the freeze test above: there the fader was already down when the crossfade
        // started; here it MOVES mid-crossfade. The tail's frozen handoff level (fade × envelope, NEVER the
        // trimmed product) must pick the new trim up exactly once while its own ramp keeps advancing - the
        // fader and the ramp compose rather than one overwriting the other.
        var (session, outputs) = BuildCrossfadeToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(TwoToneCues());

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        _ = await WaitForPeakAsync(outputs, "dev-c1");
        await AssertSettledPeakAsync(
            outputs["dev-c1"], ToneAudioDecoderProvider.Amplitude - 0.1f, 1f, "ACTIVE at unity");

        // A 30 s window keeps the tail's ramp scalar ≈1 across the measurement, so the peak isolates the
        // trim factor: ≈0.4 correct, ≈0.8 if the fader never reached an already-handed-off tail (the
        // pre-bus behaviour of any source the trim enumeration missed).
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        await Task.Delay(250); // the tail is provably ON its ramp before the fader moves
        await session.SetMasterTrimAsync(0.5f);

        var expected = ToneAudioDecoderProvider.Amplitude * 0.5f;
        await AssertSettledPeakAsync(
            outputs["dev-c1"], expected - 0.1f, expected + 0.05f, "TAIL under a fader moved mid-crossfade");
    }

    private static async Task<float> WaitForPeakAsync(
        ConcurrentDictionary<string, PeakAudioOutput> outputs, string deviceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(outputs.TryGetValue(deviceId, out var output) && output.Peak > 0.05f))
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for audio on '{deviceId}'");
            await Task.Delay(25);
        }

        return outputs[deviceId].Peak;
    }

    /// <summary>
    /// Asserts the device's STEADY-STATE peak sits inside <paramref name="min"/>..<paramref name="max"/>.
    /// <para>Reads fresh max-ever windows until one lands in range, then requires a second window to hold
    /// it - so the level must actually be the installed gain, not a value the signal swept through. A
    /// wrong gain that is stable (the ≈0.2 double-trim this test guards, or the ≈0.8 untrimmed tone) never
    /// settles and fails on the deadline with the last reading.</para>
    /// The single fixed <c>Reset(); Delay(300)</c> window this replaces assumed the pipeline had already
    /// flushed every buffer queued at the pre-trim gain; under CPU contention it had not, and the max-ever
    /// peak read the untrimmed 0.7996 (seen twice in an 8× contended solution run).
    /// </summary>
    private static async Task<float> AssertSettledPeakAsync(
        PeakAudioOutput output, float min, float max, string what, int windowMs = 300)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        float settled;
        while (true)
        {
            output.Reset();
            await Task.Delay(windowMs);
            settled = output.Peak;
            if (settled >= min && settled <= max)
                break;
            Assert.True(
                DateTime.UtcNow < deadline,
                $"{what} peak never settled into {min}..{max} (last reading {settled})");
        }

        output.Reset();
        await Task.Delay(windowMs);
        var held = output.Peak;
        Assert.True(
            held >= min && held <= max,
            $"{what} peak left {min}..{max} again (settled at {settled}, then {held})");
        return held;
    }

    /// <summary>Waits for the output's max-EVER peak (never reset) to reach <paramref name="min"/> while
    /// asserting it never passes <paramref name="max"/> - i.e. everything the device has received SINCE IT WAS
    /// CREATED, so no over-level burst can hide between two reset windows.
    /// <para>This is the right assertion wherever the level was composed BEFORE the source attached (a GO
    /// under a lowered fader). <see cref="AssertSettledPeakAsync"/> stays for levels changed on an
    /// ALREADY-PLAYING source, where buffers queued at the old gain legitimately drain through afterwards.</para></summary>
    private static async Task AssertPeakRisesToAsync(
        PeakAudioOutput output, float min, float max, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        float peak;
        while ((peak = output.Peak) < min)
        {
            Assert.True(peak <= max, $"{what} peaked at {peak} - above the {max} ceiling");
            Assert.True(DateTime.UtcNow < deadline, $"{what} peak never reached {min} (last {peak})");
            await Task.Delay(25);
        }

        Assert.True(peak <= max, $"{what} peaked at {peak} - above the {max} ceiling");
    }

    // ---- The trim must survive every path that REWRITES a live clip's route gains -----------------
    //
    // TransportGroup.ApplyAudioScale is the one place a route gain is computed
    // (fade × envelope × MasterTrim). Anything that re-installs a route on a PLAYING clip has to end up
    // at that same product; the two live-edit entry points below each used to write the fade level only,
    // which silently un-trimmed (and un-enveloped) the cue for the rest of its life.

    private const string TrimDevice = "dev-c";

    /// <summary>One tone-cue session plus the peak-reading output map (keyed by device id). The tone's
    /// constant amplitude turns "what gain is actually installed?" into a measurable output peak.</summary>
    private static (ShowSession Session, ConcurrentDictionary<string, PeakAudioOutput> Outputs) BuildToneSession()
    {
        var outputs = new ConcurrentDictionary<string, PeakAudioOutput>(StringComparer.Ordinal);
        var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (deviceId, format) => new ClipAudioOutputLease(
                outputs.GetOrAdd(deviceId, _ => new PeakAudioOutput(format))));
        return (session, outputs);
    }

    private static ShowDocument OneToneCue(params ShowClipAudioRoute[] routes) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", "tone://1") { AudioRoutes = routes }],
        Compositions: [], Routes: []);

    /// <summary>One tone cue whose volume automation sits FLAT at <paramref name="level"/> (a single keyframe
    /// samples to the same value everywhere), so the envelope's contribution is a constant the output peak can
    /// be compared against directly.</summary>
    private static ShowDocument OneToneCueWithEnvelope(float level, params ShowClipAudioRoute[] routes) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips:
        [
            new ShowClipBinding("c", "tone://1")
            {
                AudioRoutes = routes,
                VolumeEnvelope = [new ShowEnvelopePoint(TimeSpan.Zero, level)],
            },
        ],
        Compositions: [], Routes: []);

    /// <summary>Resets the max-ever peak, lets the output run, and returns what it saw - the gain
    /// currently installed on the route, measured at the device.</summary>
    private static async Task<float> SamplePeakAsync(PeakAudioOutput output, int settleMs = 300)
    {
        output.Reset();
        await Task.Delay(settleMs);
        return output.Peak;
    }

    [Fact]
    public async Task MasterTrim_SurvivesALivePerCueRouteReApply()
    {
        // Regression: ApplyActiveAudioRoutesAsync rewrote each route with `route.Gain × ActiveAudioScale`
        // (the FADE level alone), so nudging a playing cue's level under a 0.2 master fader re-installed
        // the route 5× louder than the fader said - permanently, since nothing rewrites a clip with no
        // fade or envelope running.
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        var route = new ShowClipAudioRoute(TrimDevice, [0, 1]);
        await session.LoadDocumentAsync(OneToneCue(route));
        await session.SetMasterTrimAsync(0.2f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        _ = await WaitForPeakAsync(outputs, TrimDevice);

        // The operator drags the cue's level to 0.5 while the master fader sits at 0.2.
        Assert.True(await session.ApplyActiveAudioRoutesAsync("c", [route with { Gain = 0.5f }]));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.5f * 0.2f; // tone × cue gain × trim
        var peak = await SamplePeakAsync(outputs[TrimDevice]);
        Assert.InRange(peak, expected * 0.7f, expected * 1.3f);
        // …and the session agrees the fade level was untouched (the trim multiplies, never overwrites).
        Assert.Equal(1f, (await session.GetClipAudioLevelsAsync("c"))!.FadeLevel, 3);
    }

    [Fact]
    public async Task MasterTrim_SurvivesAHotAudioOutputRebuild()
    {
        // Regression: RebuildActiveClipAudioOutputsAsync re-attached at `route.Gain` and never ran a
        // level-composition pass, so adding/removing an output line under a 0.1 master fader snapped the
        // cue to unity and left it there (and popped if the rebuild landed mid fade).
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        var route = new ShowClipAudioRoute(TrimDevice, [0, 1]);
        await session.LoadDocumentAsync(OneToneCue(route));
        await session.SetMasterTrimAsync(0.1f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        _ = await WaitForPeakAsync(outputs, TrimDevice);

        // The deck hot-adds a second output line - the count change forces the full rebuild path.
        Assert.True(await session.RebuildActiveClipAudioOutputsAsync(
            "c", [route, new ShowClipAudioRoute("dev-extra", [0, 1])]));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.1f; // tone × trim, still
        var peak = await SamplePeakAsync(outputs[TrimDevice]);
        Assert.InRange(peak, expected * 0.6f, expected * 1.4f);
        Assert.InRange(await SamplePeakAsync(outputs["dev-extra"]), expected * 0.6f, expected * 1.4f);
    }

    [Fact]
    public async Task GoUnderALoweredFader_NeverPutsAFullLevelBufferOnTheDevice()
    {
        // Regression: the fire path was the ONE route-gain write that skipped the level composition - it
        // attached each route at the AUTHORED gain and folded the trim in only after the commit, which awaits
        // the displaced clip's teardown. A GO with no fade-in under a 0.25 fader therefore pushed FULL-LEVEL
        // program audio to the device for that whole window. The max-ever peak is what catches it: a settled
        // window read (what the older assertions did) cannot see a burst that has already drained.
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue(new ShowClipAudioRoute(TrimDevice, [0, 1])));
        await session.SetMasterTrimAsync(0.25f);

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.25f; // tone × trim
        await AssertPeakRisesToAsync(outputs[TrimDevice], expected - 0.06f, expected + 0.03f, "GO under a 0.25 fader");
        Assert.Equal(0.25f, (await session.GetClipAudioLevelsAsync("c"))!.EffectiveLevel, 3);

        // …and it stays there: the reconciling pass after the commit writes the same value it attached at.
        await Task.Delay(300);
        Assert.True(
            outputs[TrimDevice].Peak <= expected + 0.03f,
            $"the clip rose above tone × trim after the commit (peak {outputs[TrimDevice].Peak})");
    }

    [Fact]
    public async Task GoWithAnEnvelopeStartingBelowUnity_NeverPutsAFullLevelBufferOnTheDevice()
    {
        // The same defect as GoUnderALoweredFader above, one component along. The envelope runner is started
        // at the END of the commit, so a clip whose automation begins at 0.2 attached its routes at unity and
        // played a FULL-LEVEL burst until the runner's first tick - on a cue authored quiet precisely because
        // it must not be loud. Sampling the envelope at the clip's start offset before the routes attach is
        // what closes it: the level composition has to be authoritative AT attach, not just after it.
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(
            OneToneCueWithEnvelope(0.2f, new ShowClipAudioRoute(TrimDevice, [0, 1])));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.2f; // tone × envelope
        await AssertPeakRisesToAsync(
            outputs[TrimDevice], expected - 0.05f, expected + 0.03f, "GO with an envelope starting at 0.2");
        // The session agrees the envelope - not the fade - is what is holding it down.
        var levels = (await session.GetClipAudioLevelsAsync("c"))!;
        Assert.Equal(1f, levels.FadeLevel, 3);
        Assert.Equal(0.2f, levels.EnvelopeLevel, 3);
    }

    [Fact]
    public async Task MasterTrim_SurvivesALiveAudioMatrixEdit()
    {
        // Regression: ApplyActiveAudioMatrixAsync wrote the caller's cells straight onto the router - no trim,
        // no fade, no envelope - and left RouteTargets describing the OLD route, so no later level write could
        // reconcile it either. Under a 0.25 fader a matrix edit jumped the cue up and left it there.
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue(new ShowClipAudioRoute(TrimDevice, [0, 1])));
        await session.SetMasterTrimAsync(0.25f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        _ = await WaitForPeakAsync(outputs, TrimDevice);

        // The operator edits the cue's channel matrix (identity, unity cells) while the fader sits at 0.25.
        Assert.True(await session.ApplyActiveAudioMatrixAsync(
            "c", "clip0", new[,] { { 1f, 0f }, { 0f, 1f } }));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.25f; // tone × cells × trim
        await AssertSettledPeakAsync(
            outputs[TrimDevice], expected - 0.06f, expected + 0.05f, "matrix edit under a 0.25 fader");

        // …and the edited matrix is now the tracked route TARGET, so a later fader move reconciles it - before,
        // the installed cells were invisible to every level path in the session.
        await session.SetMasterTrimAsync(0.5f);
        var doubled = ToneAudioDecoderProvider.Amplitude * 0.5f;
        await AssertSettledPeakAsync(
            outputs[TrimDevice], doubled - 0.06f, doubled + 0.05f, "matrix edit after the fader moved");
    }

    [Fact]
    public async Task MasterTrim_ComposesWithAStopFadeInFlight()
    {
        // The trim and a running ramp write through the SAME composition, so moving the fader mid-fade
        // must scale whatever the ramp has reached rather than snapping the clip back to the ramp's
        // untrimmed level (or the fade's level being lost). Measured at the output, mid-ramp.
        var (session, outputs) = BuildToneSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue(new ShowClipAudioRoute(TrimDevice, [0, 1])));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        _ = await WaitForPeakAsync(outputs, TrimDevice);

        // A long stop fade, waited into rather than slept through, so the ramp is PROVABLY mid-flight
        // (never still on its first step) when the fader moves.
        var stop = session.StopAsync(fadeDuration: TimeSpan.FromSeconds(6));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ClipAudioLevels? levels;
        while ((levels = await session.GetClipAudioLevelsAsync("c")) is null or { FadeLevel: >= 0.9f })
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the stop fade to ramp below 0.9");
            await Task.Delay(25);
        }

        await session.SetMasterTrimAsync(0.25f);
        levels = await session.GetClipAudioLevelsAsync("c");
        Assert.NotNull(levels); // still playing - the stop fade has not released it yet
        Assert.True(levels!.FadeLevel is > 0.05f and < 0.95f, $"the stop fade left its ramp (level {levels.FadeLevel})");
        // The fader multiplies what the ramp has reached; neither side overwrites the other.
        Assert.Equal(levels.FadeLevel * 0.25f, levels.EffectiveLevel, 3);

        // …and that product is what the device actually receives. The ramp only falls from here, so the
        // max-ever peak over the sample window cannot exceed the level read above.
        await Task.Delay(100); // let the router's gain write land
        var peak = await SamplePeakAsync(outputs[TrimDevice], settleMs: 200);
        Assert.True(peak <= ToneAudioDecoderProvider.Amplitude * levels.FadeLevel * 0.25f + 0.05f,
            $"the trim was dropped by the in-flight stop fade (peak {peak})");

        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(stop, winner);
        await stop;
        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
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
