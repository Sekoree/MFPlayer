using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.FFmpeg.Tests.Helpers;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Integration tests for <see cref="SwrResampler"/>.
/// Requires FFmpeg shared libraries to be installed (libavutil, libswresample).
/// </summary>
[Collection("FFmpeg")]
public sealed class SwrResamplerTests : IDisposable
{
    // FFmpeg is initialised by FfmpegFixture (injected via collection fixture).

    private readonly SwrResampler _resampler = new();

    public void Dispose() => _resampler.Dispose();

    // ── Same-rate pass-through ──────────────────────────────────────────────

    [Fact]
    public void Resample_SameRate_OutputSampleCountMatches()
    {
        var fmt    = new AudioFormat(48000, 2);
        var input  = MakeSine(48000, 2, frames: 1024);
        var output = new float[1024 * 2];

        int written = _resampler.Resample(input, output, fmt, 48000);

        Assert.Equal(1024, written);
    }

    [Fact]
    public void Resample_SameRate_OutputDataIsClose()
    {
        var fmt   = new AudioFormat(48000, 1);
        var input = MakeSine(48000, 1, frames: 256);
        var output = new float[256];

        _resampler.Resample(input, output, fmt, 48000);

        // Values should be approximately equal (SWR may apply very minor changes for FLT→FLT).
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i], precision: 3);
    }

    // ── Down-sampling ───────────────────────────────────────────────────────

    [Fact]
    public void Resample_Downsample2x_HalfOutputFrames()
    {
        var fmt    = new AudioFormat(48000, 1);
        var input  = MakeSine(48000, 1, frames: 1024);
        var output = new float[512];

        int written = _resampler.Resample(input, output, fmt, 24000);

        // Sinc filter introduces some latency; output count ≈ 512 ± a few frames.
        Assert.InRange(written, 480, 512);
    }

    // ── Up-sampling ─────────────────────────────────────────────────────────

    [Fact]
    public void Resample_Upsample2x_DoubleOutputFrames()
    {
        var fmt    = new AudioFormat(24000, 1);
        var input  = MakeSine(24000, 1, frames: 512);
        var output = new float[1024];

        int written = _resampler.Resample(input, output, fmt, 48000);

        Assert.InRange(written, 980, 1024);
    }

    // ── Reset ───────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_DoesNotThrow()
    {
        var fmt   = new AudioFormat(48000, 1);
        var input = MakeSine(48000, 1, frames: 128);
        var output = new float[128];
        _resampler.Resample(input, output, fmt, 48000);

        var ex = Record.Exception(() => _resampler.Reset());
        Assert.Null(ex);
    }

    // ── Dispose guard ───────────────────────────────────────────────────────

    [Fact]
    public void Dispose_Then_Resample_ThrowsObjectDisposedException()
    {
        var local = new SwrResampler();
        local.Dispose();

        var fmt   = new AudioFormat(48000, 1);
        var input = new float[128];
        var output = new float[128];

        Assert.Throws<ObjectDisposedException>(() =>
            local.Resample(input, output, fmt, 48000));
    }

    // ── Parameter change (auto-reinitialise) ────────────────────────────────

    [Fact]
    public void Resample_ParameterChange_ReinitAndProducesOutput()
    {
        // First call: 48k → 44.1k
        var fmt48  = new AudioFormat(48000, 2);
        var input1 = MakeSine(48000, 2, frames: 1024);
        var output1 = new float[960 * 2];
        _resampler.Resample(input1, output1, fmt48, 44100);

        // Second call: 44.1k → 48k (triggers reinitialise)
        var fmt441 = new AudioFormat(44100, 2);
        var input2 = MakeSine(44100, 2, frames: 960);
        var output2 = new float[1024 * 2];
        int written = _resampler.Resample(input2, output2, fmt441, 48000);

        Assert.True(written > 0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static float[] MakeSine(int sampleRate, int channels, int frames)
    {
        var data = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)Math.Sin(2 * Math.PI * 440.0 * f / sampleRate);
            for (int ch = 0; ch < channels; ch++)
                data[f * channels + ch] = v;
        }
        return data;
    }
}

