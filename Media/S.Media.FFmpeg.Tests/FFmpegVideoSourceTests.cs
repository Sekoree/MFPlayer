using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using System.Reflection;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegVideoSourceTests
{
    [Fact]
    public void SeekToFrame_ReturnsInvalidArgument_ForNegativeFrameIndex()
    {
        var source = new FFmpegVideoSource();

        var result = source.SeekToFrame(-1);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void SeekToFrame_ReturnsNonSeekable_WhenSourceIsNotSeekable()
    {
        var source = new FFmpegVideoSource(isSeekable: false);

        var result = source.SeekToFrame(5);

        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, result);
    }

    [Fact]
    public void ReadFrame_IncrementsCurrentFrameIndex_OnSuccess()
    {
        var source = new FFmpegVideoSource();

        var code = source.ReadFrame(out var frame);
        frame.Dispose();

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(1, source.CurrentFrameIndex);
    }

    [Fact]
    public void Constructor_ExposesStreamInfo()
    {
        var info = new VideoStreamInfo { Codec = "h264", Width = 1920, Height = 1080, FrameRate = 60d };
        var source = new FFmpegVideoSource(info, durationSeconds: 4.0);

        Assert.Equal("h264", source.StreamInfo.Codec);
        Assert.Equal(1920, source.StreamInfo.Width);
        Assert.Equal(1080, source.StreamInfo.Height);
        Assert.Equal(60d, source.StreamInfo.FrameRate);
    }

    [Fact]
    public void ReadFrame_FromMediaItemSharedSession_ReturnsError_WhenNativeUnavailable()
    {
        using var item = new FFmpegMediaItem(
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

        var code = source.ReadFrame(out _);

        // Without native FFmpeg, shared session cannot produce video frames
        Assert.NotEqual(MediaResult.Success, code);
    }

    [Fact]
    public void Seek_FromMediaItemSharedSession_SucceedsButReadReturnsError_WhenNativeUnavailable()
    {
        using var item = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var source = item.VideoSource;
        Assert.NotNull(source);

        // Seek itself succeeds (updates position tracking)
        Assert.Equal(MediaResult.Success, source.Seek(2.0));
        // But read fails without native decode pipeline
        var code = source.ReadFrame(out _);
        Assert.NotEqual(MediaResult.Success, code);
    }

    [Fact]
    public void SeekToFrame_FromMediaItemSharedSession_ReturnsNonSeekable_WhenFrameRateUnknown()
    {
        // Issue 4.1 fix: SeekToFrame returns MediaSourceNonSeekable when no frame rate
        // can be determined (no native stream, no observed frame rate from prior reads).
        // The old behaviour silently fell back to 30 fps which produced wrong seek positions.
        using var item = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var source = item.VideoSource;
        Assert.NotNull(source);

        var seekCode = source.SeekToFrame(45);
        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, seekCode);
    }

    [Fact]
    public void ReadFrame_ReturnsConcurrentReadViolation_WhenReadAlreadyInProgress()
    {
        using var item = new FFmpegMediaItem(
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

        var field = typeof(FFmpegVideoSource).GetField("_readInProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(source, 1);

        var code = source.ReadFrame(out _);

        Assert.Equal((int)MediaErrorCode.FFmpegConcurrentReadViolation, code);
    }

    [Fact]
    public void Constructor_FromMediaItemWithoutVideo_ThrowsDecodingException()
    {
        using var audioOnly = new FFmpegMediaItem([new FFmpegAudioSource()], []);

        var ex = Assert.Throws<DecodingException>(() => new FFmpegVideoSource(audioOnly));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void ReadFrame_FromMediaItemSharedSession_ReturnsNonSuccess_WhenNativeUnavailable_MultiPlaneCheck()
    {
        using var item = new FFmpegMediaItem(
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

        var code = source.ReadFrame(out _);

        // Without native FFmpeg, shared session cannot produce frames
        Assert.NotEqual(MediaResult.Success, code);
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
