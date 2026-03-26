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
        // Worker prefetch can advance a small number of frames before first consumer read.
        Assert.InRange(first.FrameIndex, 0, 3);

        Assert.Equal(MediaResult.Success, session.Seek(2.0));
        Assert.Equal(MediaResult.Success, session.ReadVideoFrame(out var postSeek));
        Assert.InRange(postSeek.FrameIndex, 60, 63);
        Assert.InRange(postSeek.PresentationTime, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.05));
        Assert.True(postSeek.Plane0.Length > 0);
        Assert.True(postSeek.Plane0Stride > 0);
    }

    [Fact]
    public async Task ReadVideoFrame_WhileSeeking_CompletesWithoutDeadlock()
    {
        var session = new FFSharedDemuxSession();
        try
        {
            var openCode = session.Open(
                new FFmpegOpenOptions
                {
                    InputUri = "file:///tmp/fake.mp4",
                    OpenAudio = false,
                    OpenVideo = true,
                },
                new FFmpegDecodeOptions { MaxQueuedFrames = 8 });

            Assert.Equal(MediaResult.Success, openCode);

            var reader = Task.Run(async () =>
            {
                var successCount = 0;
                for (var i = 0; i < 120; i++)
                {
                    var code = session.ReadVideoFrame(out _);
                    if (code == MediaResult.Success)
                    {
                        successCount++;
                    }

                    await Task.Delay(1);
                }

                return successCount;
            });

            var seeker = Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++)
                {
                    var seekCode = session.Seek((i % 4) * 0.25);
                    Assert.Equal(MediaResult.Success, seekCode);
                    await Task.Delay(2);
                }
            });

            await Task.WhenAll(reader, seeker).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(await reader > 0);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Close_DuringActiveReadLoop_CompletesAndReaderExits()
    {
        var session = new FFSharedDemuxSession();
        try
        {
            var openCode = session.Open(
                new FFmpegOpenOptions
                {
                    InputUri = "file:///tmp/fake.mp4",
                    OpenAudio = false,
                    OpenVideo = true,
                },
                new FFmpegDecodeOptions { MaxQueuedFrames = 8 });

            Assert.Equal(MediaResult.Success, openCode);

            var reader = Task.Run(async () =>
            {
                while (true)
                {
                    var code = session.ReadVideoFrame(out _);
                    if (code != MediaResult.Success)
                    {
                        return code;
                    }

                    await Task.Delay(1);
                }
            });

            await Task.Delay(25);
            Assert.Equal(MediaResult.Success, session.Close());

            var finalCode = await reader.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((int)MediaErrorCode.FFmpegReadFailed, finalCode);
        }
        finally
        {
            session.Dispose();
        }
    }
}

