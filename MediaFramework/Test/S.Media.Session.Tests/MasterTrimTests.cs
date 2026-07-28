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

    [Fact]
    public async Task MasterTrim_AppliesExactlyOnce_ToTheOutgoingCrossfadeTail()
    {
        // Regression (post-implementation review of the dual-voice round): the crossfade handoff froze
        // the outgoing tail's level as EffectiveAudioLevel - which already CONTAINS the master trim -
        // and the outgoing ramp then multiplied the live trim again, so a 0.5 fader halved the tail
        // twice (0.25 instead of 0.5). Measured at the output: a constant-amplitude tone clip's peak
        // must sit at tone × trim both while ACTIVE and as the outgoing tail of a long crossfade.
        var outputs = new ConcurrentDictionary<string, PeakAudioOutput>(StringComparer.Ordinal);
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (deviceId, format) => new ClipAudioOutputLease(
                outputs.GetOrAdd(deviceId, _ => new PeakAudioOutput(format))));
        await session.LoadDocumentAsync(new ShowDocument(
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
            Compositions: [], Routes: []));

        var expected = ToneAudioDecoderProvider.Amplitude * 0.5f; // tone × trim
        await session.SetMasterTrimAsync(0.5f);
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c1"));
        // The commit's trim pass has run by the time the fire returns (routes attach at authored gain
        // and CommitClipAsync folds the trim in one ApplyAudioScale pass), so measure steady state:
        // wait for audio, then reset the max-ever peak past that attach instant and sample.
        _ = await WaitForPeakAsync(outputs, "dev-c1");
        outputs["dev-c1"].Reset();
        await Task.Delay(300);
        Assert.InRange(outputs["dev-c1"].Peak, expected - 0.1f, expected + 0.05f);

        // A 30 s window keeps the outgoing ramp scalar ≈1 for the whole measurement, so the tail's
        // peak isolates the trim factor: ≈0.4 correct, ≈0.2 with the trim applied twice.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("c2", TimeSpan.FromSeconds(30)));
        await Task.Delay(250); // several 25 ms ramp steps have rewritten the tail's route gains
        outputs["dev-c1"].Reset();
        await Task.Delay(300); // sample the tail
        Assert.InRange(outputs["dev-c1"].Peak, expected - 0.1f, expected + 0.05f);
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

    /// <summary>An output that records the largest absolute sample it was ever submitted (resettable) -
    /// the observable end of the route-gain chain for a known-amplitude source.</summary>
    private sealed class PeakAudioOutput(S.Media.Core.Audio.AudioFormat format) : IAudioOutput
    {
        private float _peak;

        public S.Media.Core.Audio.AudioFormat Format => format;

        public float Peak => Volatile.Read(ref _peak);

        public void Reset() => Volatile.Write(ref _peak, 0f);

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            var max = 0f;
            foreach (var sample in packedSamples)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > max)
                    max = magnitude;
            }

            float current;
            while ((current = Volatile.Read(ref _peak)) < max
                   && Interlocked.CompareExchange(ref _peak, max, current) != current)
            {
            }
        }
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
