using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFPlaybackStressTests
{
    [HeavyFfmpegFact]
    public void HeavyVideo_BurstRead_IsStable_WhenOptInStressEnabled()
    {
        var resolvedPath = HeavyFfmpegTestConfig.ResolveVideoPath();

        var openOptions = new FFmpegOpenOptions
        {
            InputUri = new Uri(resolvedPath).AbsoluteUri,
            OpenAudio = false,
            OpenVideo = true,
        };

        var decodeOptions = new FFmpegDecodeOptions
        {
            DecodeThreadCount = 6,
            MaxQueuedPackets = 8,
            MaxQueuedFrames = 8,
        };

        using var item = new FFmpegMediaItem(openOptions, decodeOptions);
        var source = item.VideoSource;
        Assert.NotNull(source);

        var expectedThreadCount = Math.Min(6, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal(expectedThreadCount, item.ResolvedDecodeOptions!.DecodeThreadCount);

        Assert.Equal(MediaResult.Success, source.Start());

        for (var i = 0; i < 600; i++)
        {
            var code = source.ReadFrame(out var frame);
            Assert.Equal(MediaResult.Success, code);
            frame.Dispose();
        }

        Assert.Equal(MediaResult.Success, source.Stop());
    }
}
