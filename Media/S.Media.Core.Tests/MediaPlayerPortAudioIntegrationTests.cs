using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class MediaPlayerPortAudioIntegrationTests
{
    [Fact]
    public void MediaPlayer_PlayBindsFFmpegAudioSource_AndReadReturnsError_WhenNativeUnavailable()
    {
        using var media = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 8 });

        var mixer = new AudioVideoMixer();
        var player = new MediaPlayer(mixer);

        Assert.Equal(MediaResult.Success, player.Play(media));
        Assert.Single(player.AudioSources);

        var source = player.AudioSources[0];
        var buffer = new float[256 * 2];
        var readCode = source.ReadSamples(buffer, 256, out _);

        // Without native FFmpeg, shared session cannot produce audio frames
        Assert.NotEqual(MediaResult.Success, readCode);
    }

    [Fact]
    public void MediaPlayer_SustainedReadLoop_ReturnsConsistentErrors_WhenNativeUnavailable()
    {
        using var media = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 8 });

        var mixer = new AudioVideoMixer();
        var player = new MediaPlayer(mixer);

        Assert.Equal(MediaResult.Success, player.Play(media));
        Assert.Single(player.AudioSources);

        var source = player.AudioSources[0];

        // Multiple reads should consistently return errors without native FFmpeg
        var buffer = new float[256 * 2];
        var firstCode = source.ReadSamples(buffer, 256, out _);
        var secondCode = source.ReadSamples(buffer, 256, out _);

        Assert.NotEqual(MediaResult.Success, firstCode);
        Assert.Equal(firstCode, secondCode);
    }
}

