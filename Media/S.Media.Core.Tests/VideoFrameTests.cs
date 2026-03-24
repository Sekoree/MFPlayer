using S.Media.Core.Errors;
using S.Media.Core.Video;
using System.Threading;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoFrameTests
{
    [Fact]
    public void ValidateForPush_ReturnsDisposedCode_AfterDispose()
    {
        var frame = new VideoFrame(
            width: 16,
            height: 16,
            pixelFormat: VideoPixelFormat.Rgba32,
            pixelFormatData: new Rgba32PixelFormatData(),
            presentationTime: TimeSpan.Zero,
            isKeyFrame: true,
            plane0: new byte[16 * 16 * 4],
            plane0Stride: 16 * 4);

        frame.Dispose();

        var result = frame.ValidateForPush();

        Assert.Equal((int)MediaErrorCode.VideoFrameDisposed, result);
    }

    [Fact]
    public void Dispose_IsIdempotent_WhenCalledMultipleTimes()
    {
        var releaseCalls = 0;
        var frame = new VideoFrame(
            width: 8,
            height: 8,
            pixelFormat: VideoPixelFormat.Rgba32,
            pixelFormatData: new Rgba32PixelFormatData(),
            presentationTime: TimeSpan.Zero,
            isKeyFrame: false,
            plane0: new byte[8 * 8 * 4],
            plane0Stride: 8 * 4,
            releaseAction: _ => Interlocked.Increment(ref releaseCalls));

        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, releaseCalls);
    }
}

