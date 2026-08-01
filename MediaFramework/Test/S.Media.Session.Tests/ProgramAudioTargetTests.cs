using S.Media.Core.Audio;
using S.Media.Routing;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>The program-audio target seam (HaCue plan, "ShowSession redesign"): a session given an
/// <see cref="IShowProgramAudioTarget"/> plays <see cref="ShowClipBinding.LogicalSends"/> clips into
/// the project's program bus - no session backend, no device opens, real outputs and the V×R patch
/// live behind the target - with every level path riding the voice's logical sends; and the cue
/// preview auditions through the target's MONITORING seam instead of opening its own device.</summary>
public sealed class ProgramAudioTargetTests
{
    private const int Rate = 48_000;

    private static ShowDocument OneCue(IReadOnlyList<ShowClipLogicalSend>? sends) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", "tone://x") { LogicalSends = sends }],
        Compositions: [], Routes: []);

    /// <summary>A playing project bay with one identity-patched peak-reading terminal, adapted as the
    /// session's program-audio target (the terminal doubles as the default monitor line).</summary>
    private static (AudioPatchBay Bay, PatchBayShowProgramAudioTarget Target, PeakAudioOutput House) BuildBay()
    {
        var bay = new AudioPatchBay(logicalChannels: 2, Rate);
        var house = new PeakAudioOutput(new AudioFormat(Rate, 2));
        bay.AddTerminal("house", house, new float[,] { { 1f, 0f }, { 0f, 1f } });
        bay.Play();
        var target = new PatchBayShowProgramAudioTarget(
            bay, ["main-l", "main-r"], defaultMonitorTerminalId: "house");
        return (bay, target, house);
    }

    [Fact]
    public async Task LogicalSends_PlayThroughTheProgramBus_WithoutASessionBackend()
    {
        var (bay, target, house) = BuildBay();
        using var bayScope = bay;
        // NO audio backend: the target owns the devices - the session must not need one.
        var session = new ShowSession(ToneAudioDecoderProvider.Registry(), programAudioTarget: target);
        await using var scope = session;
        await session.LoadDocumentAsync(OneCue(
        [
            new ShowClipLogicalSend(0, "main-l"),
            new ShowClipLogicalSend(1, "main-r"),
            new ShowClipLogicalSend(0, "not-a-channel"), // unknown id: logged + skipped, the fire still plays
        ]));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        await WaitForPeakAsync(house, ToneAudioDecoderProvider.Amplitude * 0.9f,
            "program audio never reached the bay terminal");
        Assert.Equal(1, bay.ProducerCount); // ONE V-wide program input per voice

        // The master trim rides the voice's LOGICAL sends (ApplyAudioScale over the synthetic
        // matrix route) - it never rebuilds or addresses a real device.
        await session.SetMasterTrimAsync(0.5f);
        await AssertSettlesNearAsync(house, ToneAudioDecoderProvider.Amplitude * 0.5f, "trimmed program level");

        // Stopping the cue releases the voice's program lease with its other outputs.
        await session.StopCueAsync("c");
        Assert.Equal(0, bay.ProducerCount);
    }

    [Fact]
    public async Task EmptyLogicalSends_AreExplicitlySilent_NotAFallbackToDirectRoutes()
    {
        var (bay, target, house) = BuildBay();
        using var bayScope = bay;
        var backend = new RecordingAudioBackend();
        var session = new ShowSession(ToneAudioDecoderProvider.Registry(), backend, programAudioTarget: target);
        await using var scope = session;
        await session.LoadDocumentAsync(OneCue([]));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        await Task.Delay(200);

        Assert.Equal(0, bay.ProducerCount); // nothing acquired for a silent-by-authoring cue
        Assert.Equal(0, backend.OutputCount); // and no fallback to the direct-route adapter either
        Assert.True(house.Peak < 0.05f, $"explicitly silent clip is audible: {house.Peak}");
    }

    [Fact]
    public async Task Preview_AuditionsThroughTheMonitoringSeam_NotADeviceOpen()
    {
        var (bay, target, house) = BuildBay();
        using var bayScope = bay;
        var backend = new RecordingAudioBackend();
        var session = new ShowSession(ToneAudioDecoderProvider.Registry(), backend, programAudioTarget: target);
        await using var scope = session;
        await session.LoadDocumentAsync(OneCue(null));

        Assert.True(await session.PreviewCueAsync("c"));
        await WaitForPeakAsync(house, ToneAudioDecoderProvider.Amplitude * 0.9f,
            "the audition never reached the monitor line");
        // The whole point of the seam: previewing a cue opened NO device of its own - the
        // target-owned line carried it (a patched terminal is never double-opened).
        Assert.Equal(0, backend.OutputCount);

        await session.StopPreviewAsync();
        await AssertSettlesNearAsync(house, 0f, "stopped preview still audible");
    }

    [Fact]
    public async Task ApplyActiveLogicalSends_EditsTheProgramMatrixLive_AtTheComposedLevel()
    {
        var (bay, target, house) = BuildBay();
        using var bayScope = bay;
        var session = new ShowSession(ToneAudioDecoderProvider.Registry(), programAudioTarget: target);
        await using var scope = session;
        await session.LoadDocumentAsync(OneCue(
        [
            new ShowClipLogicalSend(0, "main-l"),
            new ShowClipLogicalSend(1, "main-r"),
        ]));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        await WaitForPeakAsync(house, ToneAudioDecoderProvider.Amplitude * 0.9f, "program audio never arrived");

        // Halve the sends live: click-free matrix reconciliation on the clip's router, no re-acquire.
        Assert.True(await session.ApplyActiveLogicalSendsAsync("c",
        [
            new ShowClipLogicalSend(0, "main-l", 0.5f),
            new ShowClipLogicalSend(1, "main-r", 0.5f),
        ]));
        Assert.Equal(1, bay.ProducerCount);
        await AssertSettlesNearAsync(house, ToneAudioDecoderProvider.Amplitude * 0.5f, "live-halved sends");

        // The edit composes with the master trim (the level composition stays authoritative).
        await session.SetMasterTrimAsync(0.5f);
        await AssertSettlesNearAsync(house, ToneAudioDecoderProvider.Amplitude * 0.25f, "sends × trim");

        // Every send unknown = silence, but the lease survives for the next edit...
        Assert.True(await session.ApplyActiveLogicalSendsAsync("c", [new ShowClipLogicalSend(0, "nope")]));
        await AssertSettlesNearAsync(house, 0f, "unknown-only send edit");
        Assert.Equal(1, bay.ProducerCount);
        // ...which is still live.
        await session.SetMasterTrimAsync(1f);
        Assert.True(await session.ApplyActiveLogicalSendsAsync("c", [new ShowClipLogicalSend(0, "main-l")]));
        await AssertSettlesNearAsync(house, ToneAudioDecoderProvider.Amplitude, "re-lit send");

        // An out-of-range source channel fails BEFORE anything is touched.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.ApplyActiveLogicalSendsAsync("c", [new ShowClipLogicalSend(7, "main-l")]));

        // Not the active clip / no program input → false (the edit lands on the next fire).
        Assert.False(await session.ApplyActiveLogicalSendsAsync("ghost", [new ShowClipLogicalSend(0, "main-l")]));
    }

    [Fact]
    public void Validator_RejectsMalformedLogicalSends()
    {
        var doc = OneCue(
        [
            new ShowClipLogicalSend(-1, "main-l"),
            new ShowClipLogicalSend(0, ""),
            new ShowClipLogicalSend(0, "main-l", float.NaN),
        ]);
        var errors = ShowDocumentValidator.Validate(doc);
        Assert.Contains(errors, e => e.Contains("negative source channel"));
        Assert.Contains(errors, e => e.Contains("empty logical channel id"));
        Assert.Contains(errors, e => e.Contains("invalid logical send gain"));
        // Whether an id EXISTS is a project question (the target owns the channel list) - not a
        // document error, so a well-formed unknown id passes here and is skipped at fire time.
        Assert.Empty(ShowDocumentValidator.Validate(OneCue([new ShowClipLogicalSend(0, "unknown-anywhere")])));
    }

    [Fact]
    public void Adapter_ValidatesRatesChannelsAndEndpoints_WithNamedErrors()
    {
        using var bay = new AudioPatchBay(2, Rate);
        // Id list must match the bay's logical width.
        Assert.Throws<ArgumentException>(() => new PatchBayShowProgramAudioTarget(bay, ["only-one"]));

        var target = new PatchBayShowProgramAudioTarget(bay, ["l", "r"]); // no default monitor line
        // Foreign clip rate without a resampler factory: a NAMED rejection, and no producer leaks.
        var rateEx = Assert.Throws<InvalidOperationException>(
            () => target.AcquireInput("v", new AudioFormat(44_100, 2)));
        Assert.Contains("resampler", rateEx.Message);
        Assert.Equal(0, bay.ProducerCount);
        // A program input is always V-wide.
        Assert.Throws<ArgumentException>(() => target.AcquireInput("v", new AudioFormat(Rate, 3)));
        // Monitoring needs an endpoint: none named and no default is an error, unknown ids too.
        Assert.Throws<InvalidOperationException>(() => target.AcquireMonitorOutput(null, new AudioFormat(Rate, 2)));
        Assert.Throws<ArgumentException>(() => target.AcquireMonitorOutput("nope", new AudioFormat(Rate, 2)));
    }

    private static async Task WaitForPeakAsync(PeakAudioOutput output, float level, string message)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && output.Peak < level)
            await Task.Delay(10);
        Assert.True(output.Peak >= level, $"{message} (peak {output.Peak}, wanted >= {level})");
    }

    /// <summary>Waits until a fresh measurement window peaks within ±10% (absolute 0.05 for silence)
    /// of <paramref name="expected"/> - ramps are click-free one-chunk fades, so the first windows
    /// after a level write may still contain the old level.</summary>
    private static async Task AssertSettlesNearAsync(PeakAudioOutput output, float expected, string what)
    {
        var tolerance = Math.Max(expected * 0.1f, 0.05f);
        var deadline = Environment.TickCount64 + 5000;
        var measured = float.MaxValue;
        while (Environment.TickCount64 < deadline)
        {
            output.Reset();
            await Task.Delay(150);
            measured = output.Peak;
            if (Math.Abs(measured - expected) <= tolerance)
                return;
        }

        Assert.Fail($"{what}: peak {measured} never settled near {expected} (±{tolerance})");
    }
}
