using S.Media.Core.Audio;
using S.Media.Core.Media;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LinearResampler"/>.
/// Covers Q4 from the open-questions list: seamless cross-buffer continuity.
/// </summary>
public sealed class LinearResamplerTests
{
    private static AudioFormat Fmt(int sampleRate, int channels = 1) =>
        new(sampleRate, channels);

    // ── Pass-through (rates match) ────────────────────────────────────────

    [Fact]
    public void Resample_SameRate_ReturnsCopy()
    {
        using var r   = new LinearResampler();
        var input     = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var output    = new float[4];
        int frames    = r.Resample(input, output, Fmt(48000), 48000);

        Assert.Equal(4, frames);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Resample_SameRate_ReturnsSamplesPerChannel()
    {
        using var r  = new LinearResampler();
        var input    = new float[] { 1f, 2f, 3f, 4f }; // 2 frames, stereo
        var output   = new float[4];
        int frames   = r.Resample(input, output, Fmt(48000, 2), 48000);

        Assert.Equal(2, frames); // 2 frames, not 4 samples
    }

    // ── Downsample 2:1 ───────────────────────────────────────────────────

    [Fact]
    public void Resample_Downsample2x_HalfOutputFrames()
    {
        using var r = new LinearResampler();
        // 8 frames at 48 kHz → 4 frames at 24 kHz
        float[] input  = Enumerable.Range(0, 8).Select(i => (float)i).ToArray();
        float[] output = new float[4];

        int frames = r.Resample(input, output, Fmt(48000), 24000);

        Assert.Equal(4, frames);
        // First output frame must be input[0]; third must be input[4] (step = 2.0).
        Assert.Equal(0f, output[0]);
        Assert.Equal(4f, output[2]);
    }

    // ── Upsample 1:2 ─────────────────────────────────────────────────────

    [Fact]
    public void Resample_Upsample2x_DoubleOutputFrames()
    {
        using var r = new LinearResampler();
        // 4 frames at 24 kHz → 8 frames at 48 kHz
        float[] input  = [0f, 1f, 2f, 3f];
        float[] output = new float[8];

        int frames = r.Resample(input, output, Fmt(24000), 48000);

        Assert.Equal(8, frames);
        Assert.Equal(0f, output[0]);      // phase 0.0 → input[0]
        Assert.Equal(0.5f, output[1], 5); // phase 0.5 → lerp(0,1)
        Assert.Equal(1f, output[2], 5);   // phase 1.0 → input[1]
        Assert.Equal(1.5f, output[3], 5); // phase 1.5 → lerp(1,2)
    }

    // ── Cross-buffer continuity (Q4) ──────────────────────────────────────

    /// <summary>
    /// Verifies that a sine wave resampled in two consecutive calls produces the same
    /// result as resampling it in a single call. Any phase discontinuity at the split
    /// point would produce a different waveform.
    /// </summary>
    [Theory]
    [InlineData(48000, 44100)]  // downsample (common DAW rate)
    [InlineData(44100, 48000)]  // upsample
    [InlineData(48000, 32000)]  // 3:2 ratio
    public void Resample_CrossBufferContinuity_MatchesSingleCallResult(
        int srcRate, int dstRate)
    {
        const int TotalSrcFrames = 4096;
        const int channels       = 1;

        // Generate a 1 kHz sine wave at srcRate.
        float[] sineWave = GenerateSine(frequency: 1000, sampleRate: srcRate,
                                        frames: TotalSrcFrames, channels);

        // Expected: resample all at once.
        double ratio        = (double)srcRate / dstRate;
        int    expectedOut  = (int)(TotalSrcFrames / ratio);
        float[] expected    = new float[expectedOut * channels];
        using var rRef      = new LinearResampler();
        rRef.Resample(sineWave, expected, Fmt(srcRate, channels), dstRate);

        // Actual: split into two halves and resample sequentially.
        int    half1Src  = TotalSrcFrames / 2;
        int    half1Dst  = (int)(half1Src / ratio);
        int    half2Dst  = expectedOut - half1Dst;

        float[] actual   = new float[expectedOut * channels];
        using var rSplit = new LinearResampler();
        rSplit.Resample(
            sineWave.AsSpan(0, half1Src * channels),
            actual.AsSpan(0, half1Dst * channels),
            Fmt(srcRate, channels), dstRate);
        rSplit.Resample(
            sineWave.AsSpan(half1Src * channels),
            actual.AsSpan(half1Dst * channels, half2Dst * channels),
            Fmt(srcRate, channels), dstRate);

        // Allow a small tolerance for floating-point at the boundary.
        // If there is a discontinuity (bug), samples near half1Dst will differ significantly.
        const float Tolerance = 1e-4f;
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(
                Math.Abs(actual[i] - expected[i]) < Tolerance,
                $"Mismatch at sample {i}: expected {expected[i]:F6}, got {actual[i]:F6}");
        }
    }

    // ── Stereo continuity ─────────────────────────────────────────────────

    [Fact]
    public void Resample_Stereo_CrossBufferContinuity()
    {
        const int srcRate = 48000;
        const int dstRate = 44100;
        const int channels = 2;
        const int TotalSrcFrames = 2048;

        float[] sineWave = GenerateStereoSine(srcRate, TotalSrcFrames);

        double ratio       = (double)srcRate / dstRate;
        int    totalDst    = (int)(TotalSrcFrames / ratio);
        float[] expected   = new float[totalDst * channels];
        using var rRef     = new LinearResampler();
        rRef.Resample(sineWave, expected, Fmt(srcRate, channels), dstRate);

        int half1Src = TotalSrcFrames / 2;
        int half1Dst = (int)(half1Src / ratio);
        int half2Dst = totalDst - half1Dst;
        float[] actual = new float[totalDst * channels];
        using var rSplit = new LinearResampler();
        rSplit.Resample(sineWave.AsSpan(0, half1Src * channels),
                        actual.AsSpan(0, half1Dst * channels),
                        Fmt(srcRate, channels), dstRate);
        rSplit.Resample(sineWave.AsSpan(half1Src * channels),
                        actual.AsSpan(half1Dst * channels, half2Dst * channels),
                        Fmt(srcRate, channels), dstRate);

        const float Tolerance = 1e-4f;
        for (int i = 0; i < actual.Length; i++)
            Assert.True(Math.Abs(actual[i] - expected[i]) < Tolerance,
                $"[ch={(i%2)}, frame={i/2}] expected {expected[i]:F6}, got {actual[i]:F6}");
    }

    // ── Reset clears state ────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsPhaseAndPrevTail()
    {
        using var r = new LinearResampler();
        float[] buf = new float[8];
        float[] input = Enumerable.Range(1, 8).Select(i => (float)i).ToArray();

        // Do a partial resample to dirty the state.
        r.Resample(input, buf, Fmt(48000), 44100);

        r.Reset();

        // After reset, result should match a fresh resampler.
        using var rFresh = new LinearResampler();
        float[] resultAfterReset = new float[8];
        float[] resultFresh      = new float[8];
        r.Resample(input, resultAfterReset, Fmt(48000), 44100);
        rFresh.Resample(input, resultFresh, Fmt(48000), 44100);

        Assert.Equal(resultFresh, resultAfterReset);
    }

    // ── Dispose guards ────────────────────────────────────────────────────

    [Fact]
    public void Resample_AfterDispose_Throws()
    {
        var r = new LinearResampler();
        r.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            r.Resample(new float[4], new float[4], Fmt(48000), 44100));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static float[] GenerateSine(int frequency, int sampleRate, int frames, int channels)
    {
        float[] buf = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)Math.Sin(2 * Math.PI * frequency * f / sampleRate);
            for (int ch = 0; ch < channels; ch++)
                buf[f * channels + ch] = v;
        }
        return buf;
    }

    private static float[] GenerateStereoSine(int sampleRate, int frames)
    {
        float[] buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            buf[f * 2]     = (float)Math.Sin(2 * Math.PI * 440  * f / sampleRate); // L
            buf[f * 2 + 1] = (float)Math.Sin(2 * Math.PI * 1000 * f / sampleRate); // R
        }
        return buf;
    }
}

