using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// The session's level/stop bus (Ideas/Structural-Refactor-Plan-2026-07-29.md §B): every sounding source
/// registers with a program/monitoring classification, and the master fader, stop-all and Panic all drive
/// that ONE enumeration. Before it there were three level authorities that never consulted each other - the
/// fader walked the transport groups only, so pulling the show to silence still let a soundboard stinger play
/// at full level and stop-all left voices running.
/// <para>The owner's 2026-07-29 scope, which these tests pin: fader and both stops cover PROGRAM audio
/// (transport cues AND soundboard voices) with IDENTICAL reach; preview/audition and analysis taps are
/// MONITORING and are never touched by any of the three.</para>
/// </summary>
public sealed class SoundingBusTests
{
    private const string CueDevice = "cue-dev";
    private const string VoiceDevice = "voice-dev";
    private const string PreviewDevice = "preview-dev";
    // Workstream A: a transport group registers ONE program source PER VOICE (label "cue:<group>:<cue>"),
    // not one per group - so a crossfade tail is on the bus in its own right. The assertions below moved with
    // it: they encoded the group-shaped registration, not the rule they exist to pin.
    private const string TransportCueSource = "cue:main:c";
    private const string VoiceSource = "voice:tile";
    private const string PreviewSource = "preview:c";

    /// <summary>A tone-cue session on a peak-reading backend: every level assertion can be made both against
    /// the session's own bookkeeping AND against the gain the device actually received.</summary>
    private static (ShowSession Session, PeakAudioBackend Backend) BuildSession()
    {
        var backend = new PeakAudioBackend();
        return (new ShowSession(ToneAudioDecoderProvider.Registry(), backend), backend);
    }

    private static ShowDocument OneToneCue() => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", "tone://1") { AudioRoutes = [new ShowClipAudioRoute(CueDevice)] }],
        Compositions: [], Routes: []);

    /// <summary>The bus entry with <paramref name="label"/>. Asserts there is exactly ONE: a duplicate entry
    /// means a released source lingered, which is itself the defect the lifecycle rules exist to prevent.</summary>
    private static SoundingSourceInfo Source(IReadOnlyList<SoundingSourceInfo> bus, string label)
    {
        var hits = bus.Where(s => string.Equals(s.Label, label, StringComparison.Ordinal)).ToArray();
        Assert.Single(hits);
        return hits[0];
    }

    private static bool Has(IReadOnlyList<SoundingSourceInfo> bus, string label) =>
        bus.Any(s => string.Equals(s.Label, label, StringComparison.Ordinal));

    private static async Task<float> LevelOfAsync(ShowSession session, string label) =>
        Source(await session.GetSoundingSourcesAsync(), label).Level;

    /// <summary>Waits until <paramref name="deviceId"/> has produced audible output - the clip/voice is
    /// genuinely running before a level is measured.</summary>
    private static async Task<PeakAudioOutput> WaitForAudioAsync(PeakAudioBackend backend, string deviceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (backend.Device(deviceId) is not { Peak: > 0.05f })
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for audio on '{deviceId}'");
            await Task.Delay(25);
        }

        return backend.Device(deviceId)!;
    }

    private static async Task<float> SamplePeakAsync(PeakAudioOutput output, int windowMs = 250)
    {
        output.Reset();
        await Task.Delay(windowMs);
        return output.Peak;
    }

    /// <summary>Asserts the device's STEADY-STATE peak reaches <paramref name="expected"/>: fresh max-ever
    /// windows are read until one lands in range, because buffers queued at the previous gain still have to
    /// drain. A wrong-but-stable gain (untrimmed, or trimmed twice) never settles and fails on the deadline.</summary>
    private static async Task AssertSettlesNearAsync(
        PeakAudioOutput output, float expected, string what, float tolerance = 0.06f)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        float peak;
        while (Math.Abs((peak = await SamplePeakAsync(output)) - expected) > tolerance)
            Assert.True(DateTime.UtcNow < deadline, $"{what} peak never settled near {expected} (last {peak})");
    }

    [Fact]
    public async Task MasterTrim_ReachesTransportClipsAndSoundboardVoices_InOneEnumeration()
    {
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        var cueOutput = await WaitForAudioAsync(backend, CueDevice);
        var voiceOutput = await WaitForAudioAsync(backend, VoiceDevice);

        var bus = await session.GetSoundingSourcesAsync();
        Assert.Equal(SoundingSourceRole.Program, Source(bus, TransportCueSource).Role);
        Assert.Equal(SoundingSourceRole.Program, Source(bus, VoiceSource).Role);

        await session.SetMasterTrimAsync(0.5f);

        Assert.Equal(0.5f, (await session.GetClipAudioLevelsAsync("c"))!.EffectiveLevel, 3);
        Assert.Equal(0.5f, await LevelOfAsync(session, VoiceSource), 3);
        // …and both devices actually receive it. THIS is the operator-visible gap that was open: the fader
        // pulled halfway down while a soundboard stinger kept playing at full level.
        var expected = ToneAudioDecoderProvider.Amplitude * 0.5f;
        await AssertSettlesNearAsync(cueOutput, expected, "transport clip");
        await AssertSettlesNearAsync(voiceOutput, expected, "soundboard voice");
    }

    [Fact]
    public async Task Preview_AndTaps_RegisterAsMonitoring_AndTheMasterFaderNeverReachesThem()
    {
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue());
        var tapId = await session.RegisterAudioTapAsync(new SinkAudioOutput(new AudioFormat(48_000, 2)), gain: 1f);
        Assert.True(await session.PreviewCueAsync("c", PreviewDevice));
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        var previewOutput = await WaitForAudioAsync(backend, PreviewDevice);
        var voiceOutput = await WaitForAudioAsync(backend, VoiceDevice);

        var bus = await session.GetSoundingSourcesAsync();
        Assert.Equal(SoundingSourceRole.Monitoring, Source(bus, PreviewSource).Role);
        Assert.Equal(SoundingSourceRole.Monitoring, Source(bus, $"tap:{tapId:N}").Role);

        await session.SetMasterTrimAsync(0.25f);

        // The audition path keeps its own level: ducking it when the show is pulled down would deafen the
        // person driving. The program voice next to it moves.
        Assert.Equal(1f, await LevelOfAsync(session, PreviewSource), 3);
        Assert.Equal(1f, Source(await session.GetSoundingSourcesAsync(), $"tap:{tapId:N}").Level, 3);
        Assert.Equal(0.25f, await LevelOfAsync(session, VoiceSource), 3);
        await AssertSettlesNearAsync(
            voiceOutput, ToneAudioDecoderProvider.Amplitude * 0.25f, "soundboard voice");
        await AssertSettlesNearAsync(previewOutput, ToneAudioDecoderProvider.Amplitude, "preview");
    }

    /// <summary>Stop-all and Panic differ ONLY in the fade duration the host resolved (Panic's default is
    /// 0 ms = hard cut), never in reach - one rule to remember under pressure. Parameterized over both so a
    /// future change cannot quietly give one of them a different set.</summary>
    [Theory]
    [InlineData(0)]   // Panic (the app's 0 ms default)
    [InlineData(300)] // Stop-all with a show stop fade
    public async Task StopAllAndPanic_TakeDownEveryProgramSource_AndLeaveMonitoringSounding(int fadeMs)
    {
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.LoadDocumentAsync(OneToneCue());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        Assert.True(await session.PreviewCueAsync("c", PreviewDevice));
        await WaitForAudioAsync(backend, CueDevice);
        await WaitForAudioAsync(backend, VoiceDevice);
        var previewOutput = await WaitForAudioAsync(backend, PreviewDevice);

        await session.StopAllAsync(TimeSpan.FromMilliseconds(fadeMs));

        Assert.All(session.Snapshot(), s => Assert.False(s.IsActive));
        Assert.False(await session.IsVoicePlayingAsync("tile"));
        var bus = await session.GetSoundingSourcesAsync();
        Assert.False(Has(bus, VoiceSource)); // the released voice left the bus with its player
        Assert.False(Has(bus, TransportCueSource)); // …and so did the released transport voice

        // The operator's audition survives both - it is how they hear what to fire next while the show is down.
        Assert.True(Source(bus, PreviewSource).IsSounding);
        Assert.True(
            await SamplePeakAsync(previewOutput) > 0.5f,
            "the preview was silenced by a stop that must not reach monitoring");
    }

    [Fact]
    public async Task StopAll_PreemptsASoundboardVoiceWhoseMediaIsStillOpening()
    {
        // Panic's other half: a stinger that was still LOADING when the button was hit is not on the bus
        // yet, so the enumeration alone would let it start playing straight after the show came down.
        await using var session = new ShowSession(BlockingOpenProvider.Registry());
        var voice = session.FireVoiceAsync("tile", "blocking://x"); // open blocks until cancelled
        await Task.Delay(150);
        Assert.False(voice.IsCompleted);

        await session.StopAllAsync(TimeSpan.Zero);

        var winner = await Task.WhenAny(voice, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(voice, winner);
        await voice; // preempted → completes without error, the voice simply never started
        Assert.False(await session.IsVoicePlayingAsync("tile"));
        Assert.False(Has(await session.GetSoundingSourcesAsync(), VoiceSource));
    }

    [Fact]
    public async Task VoiceLevel_ComposesMasterTimesVolume_WithNoDoubleApplication()
    {
        var (session, backend) = BuildSession();
        await using var scope = session;

        // Fired UNDER a reduced fader: a voice inherits the live trim at fire time, exactly as a clip does.
        await session.SetMasterTrimAsync(0.5f);
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice, volume: 0.5f);
        var voiceOutput = await WaitForAudioAsync(backend, VoiceDevice);
        Assert.Equal(0.25f, await LevelOfAsync(session, VoiceSource), 3);
        await AssertSettlesNearAsync(
            voiceOutput, ToneAudioDecoderProvider.Amplitude * 0.25f, "voice at master × volume");

        // A live volume nudge multiplies the trim rather than replacing it (the raw route write it replaces
        // un-trimmed the voice permanently - nothing else rewrites a voice with no fade running).
        await session.SetVoiceVolumeAsync("tile", 0.25f);
        Assert.Equal(0.125f, await LevelOfAsync(session, VoiceSource), 3);

        // Restoring unity brings back exactly the authored volume: the trim multiplied, never overwrote.
        await session.SetMasterTrimAsync(1f);
        Assert.Equal(0.25f, await LevelOfAsync(session, VoiceSource), 3);
        await AssertSettlesNearAsync(
            voiceOutput, ToneAudioDecoderProvider.Amplitude * 0.25f, "voice back at its authored volume");
    }

    [Fact]
    public async Task VoiceFadeOut_RampsDownFromTheTileVolume_NeverJumpingToFullLevel()
    {
        // Regression: the fade ramp wrote its 1→0 level straight onto the route, so fading a tile playing at
        // 0.4 first SNAPPED it up to full level - an audible jump before the fade even started.
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice, volume: 0.4f);
        await WaitForAudioAsync(backend, VoiceDevice);

        await session.FadeVoiceAsync("tile", TimeSpan.FromSeconds(2));

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(700);
        var highest = 0f;
        while (DateTime.UtcNow < deadline
               && await session.GetSoundingSourcesAsync() is { } bus && Has(bus, VoiceSource))
        {
            highest = Math.Max(highest, Source(bus, VoiceSource).Level);
            await Task.Delay(20);
        }

        Assert.True(highest <= 0.4f + 0.001f, $"the voice fade rose above its tile volume (peaked at {highest})");
        Assert.True(highest > 0f, "the fade never ran");
    }

    [Fact]
    public async Task ReleasedVoice_LeavesTheBus_AndIsNeverLevelledAgain()
    {
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        var voiceOutput = await WaitForAudioAsync(backend, VoiceDevice);
        Assert.True(Has(await session.GetSoundingSourcesAsync(), VoiceSource));

        await session.StopVoiceAsync("tile");
        Assert.False(Has(await session.GetSoundingSourcesAsync(), VoiceSource));

        // A trim walking the bus after the release touches nothing that is gone - the released player is
        // never written to again, and the retired device stays silent.
        await session.SetMasterTrimAsync(0.1f);
        Assert.True(
            await SamplePeakAsync(voiceOutput) <= 0.02f,
            "a released voice was still being fed after it left the bus");

        // Re-firing the same tile registers exactly ONE entry again (Source asserts it) at the current trim.
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        Assert.Equal(0.1f, await LevelOfAsync(session, VoiceSource), 3);
    }

    [Fact]
    public async Task MasterTrim_DuringAVoiceFade_ScalesTheRamp_InsteadOfFightingIt()
    {
        // The transport side of this is MasterTrimTests.MasterTrim_ComposesWithAStopFadeInFlight; a voice
        // now rides the same composition, so moving the fader mid-ramp must scale what the ramp reached
        // rather than snapping the voice back up (trim overwrites fade) or to the trim (fade overwritten).
        var (session, backend) = BuildSession();
        await using var scope = session;
        await session.FireVoiceAsync("tile", "tone://v", VoiceDevice);
        await WaitForAudioAsync(backend, VoiceDevice);

        await session.FadeVoiceAsync("tile", TimeSpan.FromSeconds(4));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        float onTheRamp;
        while ((onTheRamp = await LevelOfAsync(session, VoiceSource)) >= 0.9f)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the voice fade to ramp below 0.9");
            await Task.Delay(20);
        }

        await session.SetMasterTrimAsync(0.25f);
        var trimmed = await LevelOfAsync(session, VoiceSource);
        // The ramp has advanced a few 25 ms steps of a 4 s fade between the two reads, so the product may
        // only have fallen further - it must never exceed what the fader says.
        Assert.InRange(trimmed, onTheRamp * 0.25f * 0.85f, onTheRamp * 0.25f * 1.001f);

        // …and the ramp keeps going from there: the trim neither restarted nor froze it.
        await Task.Delay(300);
        Assert.True(await LevelOfAsync(session, VoiceSource) < trimmed, "the trim stalled the fade ramp");
    }
}
