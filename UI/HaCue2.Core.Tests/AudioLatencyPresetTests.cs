using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Review 2026-08-25, C4: the output-latency preset. The default MUST stay the depths every show ran
/// at before the setting existed - lowering latency is the operator's decision, never a silent one.
/// </summary>
public sealed class AudioLatencyPresetTests
{
    [Fact]
    public void SafeIsTheDefaultAndAsksForNothing()
    {
        Assert.Equal(AudioLatencyPreset.Safe, new ProjectAudioPatch().LatencyPreset);
        // Null options = the backend's own defaults, byte-for-byte the pre-setting behavior.
        Assert.Null(ProjectPatchBay.LatencyOptions(AudioLatencyPreset.Safe, 48_000));
    }

    [Fact]
    public void LowUsesTheProvenLiveMonitoringSizing()
    {
        var options = ProjectPatchBay.LatencyOptions(AudioLatencyPreset.Low, 48_000);

        Assert.NotNull(options);
        // HaPlay's PortAudioLiveMonitoring numbers, measured dropout-free: target max(rate/15,
        // 3 chunks, 1920) ≈ 65 ms at 48 k, ring clamp(rate/4, 4096, 16384).
        Assert.Equal(3_200, options.TargetQueueFrames);
        Assert.Equal(12_000, options.RingCapacityFrames);
        Assert.Equal(0.03, options.SuggestedLatencySeconds!.Value, precision: 3);
    }

    [Fact]
    public void BalancedSitsBetween()
    {
        var options = ProjectPatchBay.LatencyOptions(AudioLatencyPreset.Balanced, 48_000);

        Assert.NotNull(options);
        Assert.Equal(4_800, options.TargetQueueFrames); // ~100 ms at 48 k
        Assert.Equal(0, options.RingCapacityFrames);    // ring stays at the backend default
    }

    [Fact]
    public void TheQueueNeverFallsBelowThreeMixChunks()
    {
        // A very low sample rate must not shrink the target below what the pump can sustain.
        var options = ProjectPatchBay.LatencyOptions(AudioLatencyPreset.Low, 8_000);

        Assert.NotNull(options);
        Assert.True(options.TargetQueueFrames >= 480 * 3);
    }

    [Fact]
    public void ThePresetTravelsInTheFileAndAnOldFileReadsSafe()
    {
        var project = new HaCueProject { Title = "Show" };
        project.AudioPatch.LatencyPreset = AudioLatencyPreset.Low;

        var json = HaCueProjectFile.Serialize(project);
        Assert.Contains("\"latencyPreset\": \"Low\"", json, StringComparison.Ordinal);
        Assert.Equal(AudioLatencyPreset.Low, HaCueProjectFile.Deserialize(json).AudioPatch.LatencyPreset);

        // A file from before the setting existed: absent field, and the default must hold - the
        // source-generated deserializer assigns only what is present because the property is `set`.
        var stripped = json.Replace("\"latencyPreset\": \"Low\",", "", StringComparison.Ordinal);
        Assert.Equal(AudioLatencyPreset.Safe, HaCueProjectFile.Deserialize(stripped).AudioPatch.LatencyPreset);
    }
}
