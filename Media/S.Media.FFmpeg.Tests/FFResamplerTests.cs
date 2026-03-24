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
    public void Resample_PreservesDeterministicMetadata()
    {
        using var resampler = new FFResampler();
        Assert.Equal(MediaResult.Success, resampler.Initialize());

        var decoded = new FFAudioDecodeResult(4, TimeSpan.FromSeconds(1.25), 256, 0.75f);
        var code = resampler.Resample(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(4, converted.Generation);
        Assert.Equal(TimeSpan.FromSeconds(1.25), converted.PresentationTime);
        Assert.Equal(256, converted.FrameCount);
        Assert.Equal(0.75f, converted.SampleValue);
    }

    [Fact]
    public void Resample_InvalidNativeFormat_FallsBackAndDisablesNativePath()
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

        var code = resampler.Resample(decoded, out var resampled);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(256, resampled.FrameCount);
        Assert.False(resampler.IsNativeResampleEnabled);
    }

    [Fact]
    public void Resample_ResultMetadata_IsSnapshotAndUnaffectedBySourceReassignment()
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

        Assert.Equal(MediaResult.Success, resampler.Resample(decoded, out var first));

        decoded = decoded with
        {
            FrameCount = 512,
            NativeSampleRate = 96_000,
            NativeChannelCount = 1,
        };

        Assert.Equal(128, first.FrameCount);
        Assert.Equal(48_000, first.NativeSampleRate);
        Assert.Equal(2, first.NativeChannelCount);
    }
}

