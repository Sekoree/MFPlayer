using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFPortAudioIntegrationTests
{
    [Fact]
    public void FFmpegAudioSource_ReadSamples_ReturnsError_WhenNativeUnavailable()
    {
        using var media = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 8 });

        var source = media.AudioSource;
        Assert.NotNull(source);

        var buffer = new float[256 * 2];
        var read = source.ReadSamples(buffer, 256, out _);

        // Without native FFmpeg, shared session cannot produce audio frames
        Assert.NotEqual(MediaResult.Success, read);
    }

    [Fact]
    public void FFmpegAudioSource_SustainedRead_ReturnsConsistentErrors_WhenNativeUnavailable()
    {
        using var media = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 8 });

        var source = media.AudioSource;
        Assert.NotNull(source);

        // Multiple reads should consistently return the same error
        var buffer = new float[256 * 2];
        var firstCode = source.ReadSamples(buffer, 256, out _);
        var secondCode = source.ReadSamples(buffer, 256, out _);

        Assert.NotEqual(MediaResult.Success, firstCode);
        Assert.Equal(firstCode, secondCode);
    }
}
