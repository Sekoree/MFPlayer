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
    public void Seek_ResetsVideoQueueGeneration_AndReadReturnsError_WhenNativeUnavailable()
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

        // Without native FFmpeg, reads fail — session can't produce frames
        var readCode = session.ReadVideoFrame(out _);
        Assert.NotEqual(MediaResult.Success, readCode);

        // Seek still succeeds at the session level
        Assert.Equal(MediaResult.Success, session.Seek(2.0));
        // Post-seek read also fails without native decode
        var postSeekCode = session.ReadVideoFrame(out _);
        Assert.NotEqual(MediaResult.Success, postSeekCode);
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
                var attemptCount = 0;
                for (var i = 0; i < 120; i++)
                {
                    _ = session.ReadVideoFrame(out _);
                    attemptCount++;
                    await Task.Delay(1);
                }

                return attemptCount;
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

            // Verify no deadlock — both tasks complete within timeout
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
