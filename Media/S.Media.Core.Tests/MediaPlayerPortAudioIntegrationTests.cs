using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Mixing;
using S.Media.Core.Playback;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class MediaPlayerPortAudioIntegrationTests
{
    [Fact]
    public void MediaPlayer_PlayBindsFFmpegAudioSource_AndAllowsPortAudioPushPath()
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

        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());

        var outputName = engine.GetOutputDevices().First().Name;
        Assert.Equal(MediaResult.Success, engine.CreateOutputByName(outputName, out var output));
        Assert.NotNull(output);

        Assert.Equal(MediaResult.Success, output.Start(new AudioOutputConfig()));
        Assert.Equal(MediaResult.Success, player.AddAudioOutput(output));
        Assert.Equal(MediaResult.Success, player.Play(media));

        Assert.Single(player.AudioSources);

        var source = player.AudioSources[0];
        var buffer = new float[256 * 2];
        Assert.Equal(MediaResult.Success, source.ReadSamples(buffer, 256, out var framesRead));
        Assert.True(framesRead > 0);

        var frame = new AudioFrame(
            Samples: buffer,
            FrameCount: framesRead,
            SourceChannelCount: 2,
            Layout: AudioFrameLayout.Interleaved,
            SampleRate: 48_000,
            PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

        Assert.Equal(MediaResult.Success, output.PushFrame(in frame, [0, 1]));
    }

    [Fact]
    public void MediaPlayer_SustainedReadPushLoop_TracksExpectedAudioTimeline()
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

        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());

        var outputName = engine.GetOutputDevices().First().Name;
        Assert.Equal(MediaResult.Success, engine.CreateOutputByName(outputName, out var output));
        Assert.NotNull(output);
        Assert.Equal(MediaResult.Success, output.Start(new AudioOutputConfig()));

        Assert.Equal(MediaResult.Success, player.AddAudioOutput(output));
        Assert.Equal(MediaResult.Success, player.Play(media));
        Assert.Single(player.AudioSources);

        var source = player.AudioSources[0];
        var sampleRate = 48_000;
        var totalFrames = 0;

        for (var i = 0; i < 24; i++)
        {
            var buffer = new float[256 * 2];
            Assert.Equal(MediaResult.Success, source.ReadSamples(buffer, 256, out var framesRead));
            Assert.True(framesRead > 0);
            totalFrames += framesRead;

            var frame = new AudioFrame(
                Samples: buffer,
                FrameCount: framesRead,
                SourceChannelCount: 2,
                Layout: AudioFrameLayout.Interleaved,
                SampleRate: sampleRate,
                PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

            Assert.Equal(MediaResult.Success, output.PushFrame(in frame, [0, 1]));
        }

        var expectedSeconds = totalFrames / (double)sampleRate;
        var drift = Math.Abs(source.PositionSeconds - expectedSeconds);
        Assert.InRange(drift, 0, 0.05);
    }
}

