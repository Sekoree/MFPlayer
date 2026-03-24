using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFVideoSourceTests
{
    [Fact]
    public void SeekToFrame_ReturnsInvalidArgument_ForNegativeFrameIndex()
    {
        var source = new FFVideoSource();

        var result = source.SeekToFrame(-1);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void SeekToFrame_ReturnsNonSeekable_WhenSourceIsNotSeekable()
    {
        var source = new FFVideoSource(isSeekable: false);

        var result = source.SeekToFrame(5);

        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, result);
    }

    [Fact]
    public void ReadFrame_IncrementsCurrentFrameIndex_OnSuccess()
    {
        var source = new FFVideoSource();

        var code = source.ReadFrame(out var frame);
        frame.Dispose();

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(1, source.CurrentFrameIndex);
    }

    [Fact]
    public void Constructor_ExposesStreamInfo()
    {
        var info = new VideoStreamInfo { Codec = "h264", Width = 1920, Height = 1080, FrameRate = 60d };
        var source = new FFVideoSource(info, durationSeconds: 4.0);

        Assert.Equal("h264", source.StreamInfo.Codec);
        Assert.Equal(1920, source.StreamInfo.Width);
        Assert.Equal(1080, source.StreamInfo.Height);
        Assert.Equal(60d, source.StreamInfo.FrameRate);
    }

    [Fact]
    public void ReadFrame_FromMediaItemSharedSession_UsesSessionQueueAndAdvancesFrameIndex()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedFrames = 2 });

        var source = item.VideoSource;
        Assert.NotNull(source);

        var code = source.ReadFrame(out var frame);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(1, source.CurrentFrameIndex);
        frame.Dispose();
    }

    [Fact]
    public void Seek_FromMediaItemSharedSession_ReadsFromTargetTimestamp()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var source = item.VideoSource;
        Assert.NotNull(source);

        Assert.Equal(MediaResult.Success, source.Seek(2.0));
        var code = source.ReadFrame(out var frame);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(TimeSpan.FromSeconds(2), frame.PresentationTime);
        Assert.Equal(61, source.CurrentFrameIndex);
        frame.Dispose();
    }

    [Fact]
    public void SeekToFrame_FromMediaItemSharedSession_UsesFrameRateMapping()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var source = item.VideoSource;
        Assert.NotNull(source);

        Assert.Equal(MediaResult.Success, source.SeekToFrame(45));
        var code = source.ReadFrame(out var frame);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(TimeSpan.FromSeconds(1.5), frame.PresentationTime);
        Assert.Equal(46, source.CurrentFrameIndex);
        frame.Dispose();
    }
}

