using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Decoders.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFSharedDemuxSessionTests
{
    [Fact]
    public void OpenClose_IsDeterministic_AndCloseIsIdempotent()
    {
        using var session = new FFSharedDemuxSession();
        var openCode = session.Open(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = true,
            },
            new FFmpegDecodeOptions());

        Assert.Equal(MediaResult.Success, openCode);
        Assert.Equal(MediaResult.Success, session.Close());
        Assert.Equal(MediaResult.Success, session.Close());
    }

    [Fact]
    public void Dispose_ThenOpen_ReturnsDisposedCode()
    {
        var session = new FFSharedDemuxSession();
        session.Dispose();

        var code = session.Open(
            new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" },
            new FFmpegDecodeOptions());

        Assert.Equal((int)MediaErrorCode.FFmpegSharedContextDisposed, code);
    }

    [Fact]
    public void Seek_ResetsVideoQueueGeneration_AndProducesRequestedTimeline()
    {
        using var session = new FFSharedDemuxSession();
        var openCode = session.Open(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = false,
                OpenVideo = true,
            },
            new FFmpegDecodeOptions { MaxQueuedFrames = 8 });

        Assert.Equal(MediaResult.Success, openCode);
        Assert.Equal(MediaResult.Success, session.ReadVideoFrame(out var first));
        Assert.Equal(0, first.FrameIndex);

        Assert.Equal(MediaResult.Success, session.Seek(2.0));
        Assert.Equal(MediaResult.Success, session.ReadVideoFrame(out var postSeek));
        Assert.Equal(60, postSeek.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(2), postSeek.PresentationTime);
    }
}

