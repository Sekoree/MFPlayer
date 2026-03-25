using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.PortAudio.Engine;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFPortAudioIntegrationTests
{
    [Fact]
    public void FFmpegAudioSource_CanFeed_PortAudioOutput_PushPath()
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

        var source = media.AudioSource;
        Assert.NotNull(source);

        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());

        var outputDeviceName = engine.GetOutputDevices().First().Name;

        var create = engine.CreateOutputByName(outputDeviceName, out var output);
        Assert.Equal(MediaResult.Success, create);
        Assert.NotNull(output);

        Assert.Equal(MediaResult.Success, output.Start(new AudioOutputConfig()));

        var buffer = new float[256 * 2];
        var read = source.ReadSamples(buffer, 256, out var framesRead);

        Assert.Equal(MediaResult.Success, read);
        Assert.True(framesRead > 0);

        var frame = new AudioFrame(
            Samples: buffer,
            FrameCount: framesRead,
            SourceChannelCount: 2,
            Layout: AudioFrameLayout.Interleaved,
            SampleRate: source.StreamInfo.SampleRate ?? 48_000,
            PresentationTime: TimeSpan.FromSeconds(source.PositionSeconds));

        var push = output.PushFrame(in frame, [0, 1]);
        Assert.Equal(MediaResult.Success, push);

        var semantic = ErrorCodeRanges.ResolveSharedSemantic(push);
        Assert.Equal(MediaResult.Success, semantic);
    }

    [Fact]
    public void FFmpegAudioSource_ToPortAudioOutput_SustainedPush_StaysWithinPositionDriftEnvelope()
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

        var source = media.AudioSource;
        Assert.NotNull(source);

        using var engine = new PortAudioEngine();
        Assert.Equal(MediaResult.Success, engine.Initialize(new AudioEngineConfig()));
        Assert.Equal(MediaResult.Success, engine.Start());

        var outputDeviceName = engine.GetOutputDevices().First().Name;
        Assert.Equal(MediaResult.Success, engine.CreateOutputByName(outputDeviceName, out var output));
        Assert.NotNull(output);
        Assert.Equal(MediaResult.Success, output!.Start(new AudioOutputConfig()));

        var sampleRate = source.StreamInfo.SampleRate ?? 48_000;
        var totalFrames = 0;

        for (var i = 0; i < 32; i++)
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

