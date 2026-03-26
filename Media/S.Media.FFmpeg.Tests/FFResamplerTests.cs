using S.Media.Core.Errors;
using S.Media.FFmpeg.Decoders.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFResamplerTests
{
    [Fact]
    public void Resample_RequiresInitialize()
    {
        using var resampler = new FFResampler();

        var code = resampler.Resample(new FFAudioDecodeResult(0, TimeSpan.Zero, 256, 0.25f), out _);

        Assert.Equal((int)MediaErrorCode.FFmpegResampleFailed, code);
    }

    [Fact]
    public void Resample_WithoutNativeFields_ReturnsError()
    {
        using var resampler = new FFResampler();
        Assert.Equal(MediaResult.Success, resampler.Initialize());

        var decoded = new FFAudioDecodeResult(4, TimeSpan.FromSeconds(1.25), 256, 0.75f);
        var code = resampler.Resample(decoded, out _);

        Assert.Equal((int)MediaErrorCode.FFmpegResampleFailed, code);
    }

    [Fact]
    public void Resample_InvalidNativeFormat_ReturnsErrorAndDisablesNativePath()
    {
        using var resampler = new FFResampler();
        Assert.Equal(MediaResult.Success, resampler.Initialize());

        var decoded = new FFAudioDecodeResult(
            Generation: 0,
            PresentationTime: TimeSpan.Zero,
            FrameCount: 256,
            SampleValue: 0.2f,
            NativeSampleRate: 48_000,
            NativeChannelCount: 2,
            NativeSampleFormat: int.MaxValue);

        var code = resampler.Resample(decoded, out _);

        Assert.Equal((int)MediaErrorCode.FFmpegResampleFailed, code);
        Assert.False(resampler.IsNativeResampleEnabled);
    }

    [Fact]
    public void Resample_WithNativeFields_WithoutNativeLibs_ReturnsError()
    {
        using var resampler = new FFResampler();
        Assert.Equal(MediaResult.Success, resampler.Initialize());

        var decoded = new FFAudioDecodeResult(
            Generation: 7,
            PresentationTime: TimeSpan.FromSeconds(0.5),
            FrameCount: 128,
            SampleValue: 0.4f,
            NativeSampleRate: 48_000,
            NativeChannelCount: 2,
            NativeSampleFormat: 1);

        var code = resampler.Resample(decoded, out _);

        // Without native FFmpeg libraries, resample fails
        Assert.Equal((int)MediaErrorCode.FFmpegResampleFailed, code);
    }
}

