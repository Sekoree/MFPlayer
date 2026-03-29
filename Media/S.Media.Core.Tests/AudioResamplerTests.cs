using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioResamplerTests
{
    // ──────────────────────────────────────────────────────────────
    //  Identity / pass-through
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_IdentityRate_ProducesExactFrameCount(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(44100, 2, 44100, 2, mode);
        var input = GenerateSine(1000, 44100, 2, 1024);
        var output = new float[resampler.EstimateOutputFrameCount(1024) * 2];

        var frames = resampler.Resample(input, 1024, output);

        Assert.Equal(1024, frames);
    }

    [Fact]
    public void Resample_IdentityRate_Linear_ProducesBitExactCopy()
    {
        using var resampler = new AudioResampler(48000, 1, 48000, 1, AudioResamplerMode.Linear);
        var input = GenerateSine(440, 48000, 1, 512);
        var output = new float[resampler.EstimateOutputFrameCount(512)];

        var frames = resampler.Resample(input, 512, output);

        Assert.Equal(512, frames);
        for (var i = 0; i < 512; i++)
        {
            Assert.Equal(input[i], output[i], precision: 5);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Upsample
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_Upsample_44100_to_48000_ProducesCorrectFrameCount(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(44100, 2, 48000, 2, mode);
        const int inputFrames = 4410;
        var input = GenerateSine(1000, 44100, 2, inputFrames);
        var output = new float[resampler.EstimateOutputFrameCount(inputFrames) * 2];

        var frames = resampler.Resample(input, inputFrames, output);

        var expected = (int)Math.Round((double)inputFrames * 48000 / 44100);
        // Sinc mode defers the last ~halfSize frames (kernel latency), reducing single-chunk output.
        var tolerance = mode == AudioResamplerMode.Sinc ? 40 : 2;
        Assert.InRange(frames, expected - tolerance, expected + 2);
    }

    // ──────────────────────────────────────────────────────────────
    //  Downsample
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_Downsample_48000_to_44100_ProducesCorrectFrameCount(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(48000, 2, 44100, 2, mode);
        const int inputFrames = 4800;
        var input = GenerateSine(1000, 48000, 2, inputFrames);
        var output = new float[resampler.EstimateOutputFrameCount(inputFrames) * 2];

        var frames = resampler.Resample(input, inputFrames, output);

        var expected = (int)Math.Round((double)inputFrames * 44100 / 48000);
        var tolerance = mode == AudioResamplerMode.Sinc ? 40 : 2;
        Assert.InRange(frames, expected - tolerance, expected + 2);
    }

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_96000_to_48000_ApproximatelyHalvesFrameCount(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(96000, 1, 48000, 1, mode);
        const int inputFrames = 9600;
        var input = GenerateSine(1000, 96000, 1, inputFrames);
        var output = new float[resampler.EstimateOutputFrameCount(inputFrames)];

        var frames = resampler.Resample(input, inputFrames, output);

        // Sinc defers the last ~halfSize frames, so single-chunk output is reduced.
        var low = mode == AudioResamplerMode.Sinc ? 4780 : 4798;
        Assert.InRange(frames, low, 4802);
    }

    // ──────────────────────────────────────────────────────────────
    //  Edge cases
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_SingleFrame_DoesNotCrash(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1, mode);
        var input = new[] { 0.5f };
        var output = new float[resampler.EstimateOutputFrameCount(1)];

        var frames = resampler.Resample(input, 1, output);

        // Linear needs only 2 taps so can produce ≥1 frame.
        // Sinc defers output when input < kernelHalfSize (not enough context), returning 0.
        if (mode == AudioResamplerMode.Linear)
            Assert.True(frames >= 1);
        else
            Assert.True(frames >= 0); // sinc may return 0 for very small inputs
    }

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_EmptyInput_ReturnsZeroFrames(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1, mode);
        var output = new float[64];

        var frames = resampler.Resample(ReadOnlySpan<float>.Empty, 0, output);

        Assert.Equal(0, frames);
    }

    // ──────────────────────────────────────────────────────────────
    //  Streaming continuity
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_StreamingContinuity_NoGlitch(AudioResamplerMode mode)
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1, mode);
        const int chunkSize = 1024;
        const int chunks = 10;

        var allOutput = new List<float>();

        for (var c = 0; c < chunks; c++)
        {
            var input = GenerateSine(440, 44100, 1, chunkSize, phaseOffset: c * chunkSize);
            var output = new float[resampler.EstimateOutputFrameCount(chunkSize)];
            var frames = resampler.Resample(input, chunkSize, output);
            for (var i = 0; i < frames; i++)
                allOutput.Add(output[i]);
        }

        // Skip a few initial samples for the sinc mode's startup transient:
        // The very first output frames may lack full look-back history (no previous chunk).
        // This is an inherent edge-of-stream effect, not a chunk-boundary glitch.
        var startIndex = mode == AudioResamplerMode.Sinc ? 64 : 2;

        // Check for discontinuities: second-derivative should not spike
        var maxDerivJump = 0.0;
        var maxJumpIndex = -1;
        for (var i = startIndex; i < allOutput.Count; i++)
        {
            var d1 = allOutput[i] - allOutput[i - 1];
            var d2 = allOutput[i - 1] - allOutput[i - 2];
            var jump = Math.Abs(d1 - d2);
            if (jump > maxDerivJump)
            {
                maxDerivJump = jump;
                maxJumpIndex = i;
            }
        }

        // For a 440 Hz sine at 48 kHz the max second derivative is moderate.
        // A glitch would produce a spike > 0.5. Allow generous headroom.
        Assert.True(maxDerivJump < 0.5, $"Derivative jump {maxDerivJump} at index {maxJumpIndex}/{allOutput.Count} exceeds glitch threshold.");
    }

    // ──────────────────────────────────────────────────────────────
    //  Sine wave frequency preservation
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resample_Sinc_SineWave_PreservesFrequency()
    {
        const int srcRate = 44100;
        const int dstRate = 48000;
        const int freq = 1000;
        const int inputFrames = 4410;

        using var resampler = new AudioResampler(srcRate, 1, dstRate, 1, AudioResamplerMode.Sinc);
        var input = GenerateSine(freq, srcRate, 1, inputFrames);
        var output = new float[resampler.EstimateOutputFrameCount(inputFrames)];
        var frames = resampler.Resample(input, inputFrames, output);

        // Simple zero-crossing frequency estimation on the output
        var crossings = 0;
        for (var i = 1; i < frames; i++)
        {
            if ((output[i - 1] < 0 && output[i] >= 0) || (output[i - 1] >= 0 && output[i] < 0))
                crossings++;
        }

        // Each full cycle has 2 zero-crossings.
        // Expected ≈ freq * (frames / dstRate) * 2
        var durationSeconds = (double)frames / dstRate;
        var expectedCrossings = freq * durationSeconds * 2;
        Assert.InRange(crossings, expectedCrossings - 4, expectedCrossings + 4);
    }

    // ──────────────────────────────────────────────────────────────
    //  Channel mismatch policies
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ChannelMismatch_Drop_DiscardsExtraChannels()
    {
        using var resampler = new AudioResampler(48000, 6, 48000, 2, channelPolicy: ChannelMismatchPolicy.Drop);
        // 4 frames of 6-channel: ch0=1, ch1=2, ch2=3, ch3=4, ch4=5, ch5=6
        var input = new float[4 * 6];
        for (var f = 0; f < 4; f++)
            for (var ch = 0; ch < 6; ch++)
                input[f * 6 + ch] = ch + 1;

        var output = new float[4 * 2];
        var frames = resampler.Resample(input, 4, output);

        Assert.Equal(4, frames);
        for (var f = 0; f < 4; f++)
        {
            Assert.Equal(1f, output[f * 2 + 0], precision: 5); // ch0
            Assert.Equal(2f, output[f * 2 + 1], precision: 5); // ch1
        }
    }

    [Fact]
    public void ChannelMismatch_MixToStereo_AveragesChannels()
    {
        // 4 channels → 2 channels with MixToStereo
        // ch0=1(L), ch1=2(R), ch2=3(L), ch3=4(R)
        // L = avg(1,3) = 2, R = avg(2,4) = 3
        using var resampler = new AudioResampler(48000, 4, 48000, 2, channelPolicy: ChannelMismatchPolicy.MixToStereo);
        var input = new float[2 * 4];
        for (var f = 0; f < 2; f++)
        {
            input[f * 4 + 0] = 1f;
            input[f * 4 + 1] = 2f;
            input[f * 4 + 2] = 3f;
            input[f * 4 + 3] = 4f;
        }

        var output = new float[2 * 2];
        var frames = resampler.Resample(input, 2, output);

        Assert.Equal(2, frames);
        for (var f = 0; f < 2; f++)
        {
            Assert.Equal(2f, output[f * 2 + 0], precision: 5); // L = avg(1,3)
            Assert.Equal(3f, output[f * 2 + 1], precision: 5); // R = avg(2,4)
        }
    }

    [Fact]
    public void ChannelMismatch_MixToMono_SumsAndDivides()
    {
        // 4 channels → 1 channel with MixToMono
        // avg(1,2,3,4) = 2.5
        using var resampler = new AudioResampler(48000, 4, 48000, 1, channelPolicy: ChannelMismatchPolicy.MixToMono);
        var input = new float[2 * 4];
        for (var f = 0; f < 2; f++)
        {
            input[f * 4 + 0] = 1f;
            input[f * 4 + 1] = 2f;
            input[f * 4 + 2] = 3f;
            input[f * 4 + 3] = 4f;
        }

        var output = new float[2];
        var frames = resampler.Resample(input, 2, output);

        Assert.Equal(2, frames);
        for (var f = 0; f < 2; f++)
        {
            Assert.Equal(2.5f, output[f], precision: 5);
        }
    }

    [Fact]
    public void ChannelMismatch_Fail_ReturnsNegativeOne()
    {
        using var resampler = new AudioResampler(48000, 6, 48000, 2, channelPolicy: ChannelMismatchPolicy.Fail);
        var input = new float[4 * 6];
        var output = new float[4 * 2];

        var frames = resampler.Resample(input, 4, output);

        Assert.Equal(-1, frames);
    }

    [Fact]
    public void ChannelMismatch_Fail_AllowsWhenSourceFitsTarget()
    {
        // 2ch source → 2ch target: no mismatch, should succeed
        using var resampler = new AudioResampler(48000, 2, 48000, 2, channelPolicy: ChannelMismatchPolicy.Fail);
        var input = new float[4 * 2];
        var output = new float[4 * 2];

        var frames = resampler.Resample(input, 4, output);

        Assert.Equal(4, frames);
    }

    // ──────────────────────────────────────────────────────────────
    //  Reset
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsFractionalState()
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1, AudioResamplerMode.Linear);
        var input = GenerateSine(440, 44100, 1, 512);

        // First pass
        var output1 = new float[resampler.EstimateOutputFrameCount(512)];
        var frames1 = resampler.Resample(input, 512, output1);

        resampler.Reset();

        // Second pass after reset — should produce identical result
        var output2 = new float[resampler.EstimateOutputFrameCount(512)];
        var frames2 = resampler.Resample(input, 512, output2);

        Assert.Equal(frames1, frames2);
        for (var i = 0; i < Math.Min(frames1, frames2); i++)
        {
            Assert.Equal(output1[i], output2[i], precision: 5);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Combined rate + channel conversion
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioResamplerMode.Linear)]
    [InlineData(AudioResamplerMode.Sinc)]
    public void Resample_CombinedRateAndChannelConversion(AudioResamplerMode mode)
    {
        // 44100Hz 6ch → 48000Hz 2ch with Drop policy
        using var resampler = new AudioResampler(44100, 6, 48000, 2, mode, ChannelMismatchPolicy.Drop);
        const int inputFrames = 1024;
        var input = new float[inputFrames * 6];
        for (var f = 0; f < inputFrames; f++)
        {
            var t = (double)f / 44100;
            var val = (float)Math.Sin(2 * Math.PI * 440 * t);
            for (var ch = 0; ch < 6; ch++)
                input[f * 6 + ch] = val * (ch + 1) * 0.1f;
        }

        var output = new float[resampler.EstimateOutputFrameCount(inputFrames) * 2];
        var frames = resampler.Resample(input, inputFrames, output);

        var expected = (int)Math.Round((double)inputFrames * 48000 / 44100);
        var tolerance = mode == AudioResamplerMode.Sinc ? 40 : 3;
        Assert.InRange(frames, expected - tolerance, expected + 3);

        // Verify output is not all zeros
        var hasNonZero = false;
        for (var i = 0; i < frames * 2; i++)
        {
            if (Math.Abs(output[i]) > 1e-6f)
            {
                hasNonZero = true;
                break;
            }
        }
        Assert.True(hasNonZero, "Output should contain non-zero samples.");
    }

    // ──────────────────────────────────────────────────────────────
    //  EstimateOutputFrameCount
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void EstimateOutputFrameCount_ReturnsZero_ForZeroInput()
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1);
        Assert.Equal(0, resampler.EstimateOutputFrameCount(0));
        Assert.Equal(0, resampler.EstimateOutputFrameCount(-5));
    }

    [Fact]
    public void EstimateOutputFrameCount_ReturnsExact_ForIdentityRate()
    {
        using var resampler = new AudioResampler(48000, 2, 48000, 2);
        Assert.Equal(1024, resampler.EstimateOutputFrameCount(1024));
    }

    [Fact]
    public void EstimateOutputFrameCount_IsUpperBound()
    {
        using var resampler = new AudioResampler(44100, 1, 48000, 1, AudioResamplerMode.Sinc);
        var input = GenerateSine(440, 44100, 1, 4410);
        var estimate = resampler.EstimateOutputFrameCount(4410);
        var output = new float[estimate];
        var actual = resampler.Resample(input, 4410, output);
        Assert.True(actual <= estimate, $"Actual {actual} should be ≤ estimate {estimate}");
    }

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private static float[] GenerateSine(double frequency, int sampleRate, int channels, int frameCount, int phaseOffset = 0)
    {
        var samples = new float[frameCount * channels];
        for (var f = 0; f < frameCount; f++)
        {
            var t = (double)(f + phaseOffset) / sampleRate;
            var value = (float)Math.Sin(2 * Math.PI * frequency * t);
            for (var ch = 0; ch < channels; ch++)
                samples[f * channels + ch] = value;
        }
        return samples;
    }
}

