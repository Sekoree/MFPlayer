using S.Media.Core.Mixing;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DefaultAudioMixer"/> (review §4.12 / M1).
/// Exercises both the SIMD fast path (length ≥ <c>Vector&lt;float&gt;.Count</c>) and
/// the scalar tail.
/// </summary>
public sealed class AudioMixerTests
{
    private static readonly IAudioMixer Mixer = DefaultAudioMixer.Instance;

    // ── MixInto ─────────────────────────────────────────────────────────────

    [Fact]
    public void MixInto_ShortBuffer_AccumulatesElementwise()
    {
        float[] dest = [1f, 2f, 3f, 4f, 5f, 6f, 7f];
        float[] src  = [1f, 1f, 1f, 1f, 1f, 1f, 1f];
        Mixer.MixInto(dest, src);
        Assert.Equal(new[] { 2f, 3f, 4f, 5f, 6f, 7f, 8f }, dest);
    }

    [Fact]
    public void MixInto_SimdSizedBuffer_AccumulatesElementwise()
    {
        var dest = new float[128];
        var src  = new float[128];
        for (int i = 0; i < 128; i++) { dest[i] = i; src[i] = 0.5f; }

        Mixer.MixInto(dest, src);

        for (int i = 0; i < 128; i++)
            Assert.Equal(i + 0.5f, dest[i], 6);
    }

    [Fact]
    public void MixInto_MismatchedLengths_ProcessesCommonPrefixOnly()
    {
        float[] dest = [1f, 1f, 1f, 1f];
        float[] src  = [1f, 1f];
        Mixer.MixInto(dest, src);
        Assert.Equal(new[] { 2f, 2f, 1f, 1f }, dest);
    }

    // ── ApplyGain ───────────────────────────────────────────────────────────

    [Fact]
    public void ApplyGain_ScalarTail_ScalesInPlace()
    {
        float[] buf = [1f, 2f, 3f, 4f, 5f];
        Mixer.ApplyGain(buf, 0.5f);
        Assert.Equal(new[] { 0.5f, 1f, 1.5f, 2f, 2.5f }, buf);
    }

    [Fact]
    public void ApplyGain_Simd_ScalesInPlace()
    {
        var buf = new float[256];
        for (int i = 0; i < buf.Length; i++) buf[i] = 2f;
        Mixer.ApplyGain(buf, 0.25f);
        Assert.All(buf, v => Assert.Equal(0.5f, v, 6));
    }

    [Fact]
    public void ApplyGain_ZeroGain_Zeros()
    {
        var buf = new float[17];
        for (int i = 0; i < buf.Length; i++) buf[i] = i + 1;
        Mixer.ApplyGain(buf, 0f);
        Assert.All(buf, v => Assert.Equal(0f, v));
    }

    // ── MeasurePeak ─────────────────────────────────────────────────────────

    [Fact]
    public void MeasurePeak_EmptyBuffer_IsZero()
    {
        Assert.Equal(0f, Mixer.MeasurePeak(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void MeasurePeak_PositiveAndNegative_ReturnsAbsMax()
    {
        float[] buf = [0.1f, -0.9f, 0.3f, -0.4f, 0.5f];
        Assert.Equal(0.9f, Mixer.MeasurePeak(buf), 6);
    }

    [Fact]
    public void MeasurePeak_Simd_ReturnsAbsMax()
    {
        var buf = new float[130];
        buf[100] = -1.5f;
        Assert.Equal(1.5f, Mixer.MeasurePeak(buf), 6);
    }

    // ── ApplyChannelMap ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyChannelMap_MonoToStereo_DuplicatesChannel()
    {
        float[] src = [0.1f, 0.2f, 0.3f, 0.4f];
        var dest = new float[8]; // 4 frames × 2 channels
        var routes = new (int dstCh, float gain)[][]
        {
            [(0, 1f), (1, 1f)] // source channel 0 → dest 0 and 1 at unity gain
        };

        Mixer.ApplyChannelMap(src, dest, routes, srcChannels: 1, dstChannels: 2, frameCount: 4);

        Assert.Equal(new[] { 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f }, dest);
    }

    [Fact]
    public void ApplyChannelMap_StereoToMono_SumsWithHalfGain()
    {
        float[] src = [1f, 0.5f, -1f, 0.5f]; // 2 frames × 2 channels
        var dest = new float[2];
        var routes = new (int dstCh, float gain)[][]
        {
            [(0, 0.5f)], // L → 0 at 0.5
            [(0, 0.5f)]  // R → 0 at 0.5
        };

        Mixer.ApplyChannelMap(src, dest, routes, srcChannels: 2, dstChannels: 1, frameCount: 2);

        Assert.Equal(new[] { 0.75f, -0.25f }, dest);
    }

    [Fact]
    public void ApplyChannelMap_IgnoresDstChannelsBeyondDestWidth()
    {
        float[] src = [1f, 2f];                                          // 2 frames mono
        var dest = new float[2];                                          // dstChannels = 1
        var routes = new (int dstCh, float gain)[][]
        {
            [(0, 1f), (5, 1f)] // dst channel 5 must be silently skipped
        };

        Mixer.ApplyChannelMap(src, dest, routes, srcChannels: 1, dstChannels: 1, frameCount: 2);

        Assert.Equal(new[] { 1f, 2f }, dest);
    }

    // ── FlushDenormalsToZero ────────────────────────────────────────────────

    [Fact]
    public void FlushDenormalsToZero_DoesNotThrow()
    {
        // No observable effect we can deterministically assert across platforms;
        // just ensure the call is well-defined on every CPU.
        var ex = Record.Exception(() => Mixer.FlushDenormalsToZero());
        Assert.Null(ex);
    }

    // ── CountOverflows (§4.13 / M2) ─────────────────────────────────────────

    [Fact]
    public void CountOverflows_EmptyBuffer_ReturnsZero()
        => Assert.Equal(0, Mixer.CountOverflows(ReadOnlySpan<float>.Empty));

    [Fact]
    public void CountOverflows_NoneExceedOne_ReturnsZero()
    {
        var buffer = new float[] { 0.5f, -0.5f, 1.0f, -1.0f, 0f, 0.99f };
        Assert.Equal(0, Mixer.CountOverflows(buffer));
    }

    [Fact]
    public void CountOverflows_CountsBothSignsPastUnity()
    {
        var buffer = new float[] { 1.1f, -1.01f, 0.5f, -0.99f, 2.0f, -3.0f };
        Assert.Equal(4, Mixer.CountOverflows(buffer));
    }

    [Fact]
    public void CountOverflows_SimdSized_CountsCorrectly()
    {
        var buffer = new float[256];
        int expected = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = i % 8 == 0 ? 1.5f : 0.5f;
            if (i % 8 == 0) expected++;
        }
        Assert.Equal(expected, Mixer.CountOverflows(buffer));
    }

    // ── ApplySoftClip (§4.13 / M2) ──────────────────────────────────────────

    [Fact]
    public void ApplySoftClip_EmptyBuffer_NoOp()
    {
        Span<float> buffer = [];
        Mixer.ApplySoftClip(buffer);
        // no throw, no state to check
    }

    [Fact]
    public void ApplySoftClip_BelowThreshold_Untouched()
    {
        float[] buffer = [0.1f, -0.5f, 0.9f, -0.9f];
        var copy = (float[])buffer.Clone();
        Mixer.ApplySoftClip(buffer, 0.95f);
        Assert.Equal(copy, buffer);
    }

    [Fact]
    public void ApplySoftClip_AboveThreshold_StaysSignedAndBounded()
    {
        float[] buffer = [1.5f, -2.0f, 5.0f, -10.0f];
        Mixer.ApplySoftClip(buffer, 0.9f);

        foreach (var v in buffer)
        {
            Assert.True(Math.Abs(v) < 1.0f, $"soft-clipped sample {v} must stay under ±1.0");
            Assert.True(Math.Abs(v) > 0.9f, $"soft-clipped sample {v} must exceed the threshold");
        }
        Assert.True(buffer[0] > 0, "sign of positive sample preserved");
        Assert.True(buffer[1] < 0, "sign of negative sample preserved");
    }

    [Fact]
    public void ApplySoftClip_MonotonicInAbsoluteValue()
    {
        float[] buffer = [1.01f, 1.5f, 3.0f, 10.0f];
        Mixer.ApplySoftClip(buffer, 0.9f);
        for (int i = 1; i < buffer.Length; i++)
            Assert.True(buffer[i] >= buffer[i - 1],
                $"soft-clip must be monotonically non-decreasing in |input|; {buffer[i-1]} → {buffer[i]}");
    }
}

