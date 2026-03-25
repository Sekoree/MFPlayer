using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using System.Reflection;
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
        Assert.True(frame.Plane0.Length > 0);
        Assert.True(frame.Plane0Stride > 0);
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
        Assert.InRange(frame.PresentationTime, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.05));
        Assert.InRange(source.CurrentFrameIndex, 61, 64);
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
        Assert.InRange(frame.PresentationTime, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.533334));
        Assert.InRange(source.CurrentFrameIndex, 46, 47);
        frame.Dispose();
    }

    [Fact]
    public void ReadFrame_ReturnsConcurrentReadViolation_WhenReadAlreadyInProgress()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedFrames = 8 });

        var source = item.VideoSource;
        Assert.NotNull(source);

        var field = typeof(FFVideoSource).GetField("_readInProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(source, 1);

        var code = source.ReadFrame(out _);

        Assert.Equal((int)MediaErrorCode.FFmpegConcurrentReadViolation, code);
    }

    [Fact]
    public void Constructor_FromMediaItemWithoutVideo_ThrowsDecodingException()
    {
        using var audioOnly = new FFMediaItem([new FFAudioSource()], []);

        var ex = Assert.Throws<DecodingException>(() => new FFVideoSource(audioOnly));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void ReadFrame_FromMediaItemSharedSession_DoesNotExposeInvalidMultiPlaneShape()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedFrames = 4 });

        var source = item.VideoSource;
        Assert.NotNull(source);

        var code = source.ReadFrame(out var frame);

        Assert.Equal(MediaResult.Success, code);
        if (IsMultiPlaneFormat(frame.PixelFormat))
        {
            Assert.False(frame.Plane0.IsEmpty);
            Assert.True(frame.Plane0Stride > 0);

            if (frame.PixelFormat == S.Media.Core.Video.VideoPixelFormat.Nv12 || frame.PixelFormat == S.Media.Core.Video.VideoPixelFormat.P010Le)
            {
                Assert.False(frame.Plane1.IsEmpty);
                Assert.True(frame.Plane1Stride > 0);
            }
            else
            {
                Assert.False(frame.Plane1.IsEmpty);
                Assert.True(frame.Plane1Stride > 0);
                Assert.False(frame.Plane2.IsEmpty);
                Assert.True(frame.Plane2Stride > 0);
            }
        }

        frame.Dispose();
    }

    private static bool IsMultiPlaneFormat(S.Media.Core.Video.VideoPixelFormat format)
    {
        return format is
            S.Media.Core.Video.VideoPixelFormat.Yuv420P or
            S.Media.Core.Video.VideoPixelFormat.Nv12 or
            S.Media.Core.Video.VideoPixelFormat.Yuv422P or
            S.Media.Core.Video.VideoPixelFormat.Yuv422P10Le or
            S.Media.Core.Video.VideoPixelFormat.P010Le or
            S.Media.Core.Video.VideoPixelFormat.Yuv420P10Le or
            S.Media.Core.Video.VideoPixelFormat.Yuv444P or
            S.Media.Core.Video.VideoPixelFormat.Yuv444P10Le;
    }
}
