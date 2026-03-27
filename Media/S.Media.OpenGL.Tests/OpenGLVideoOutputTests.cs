using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGLVideoOutputTests
{
    [Fact]
    public void Stop_IsIdempotent_WhenAlreadyStopped()
    {
        using var output = new OpenGLVideoOutput();

        Assert.Equal(MediaResult.Success, output.Stop());
        Assert.Equal(MediaResult.Success, output.Stop());
    }

    [Fact]
    public void PushFrame_ReturnsParentNotInitialized_WhenNotRunning()
    {
        using var output = new OpenGLVideoOutput();
        using var frame = CreateFrame();

        Assert.Equal((int)MediaErrorCode.OpenGLCloneParentNotInitialized, output.PushFrame(frame));
    }

    private static VideoFrame CreateFrame()
    {
        var rgba = new byte[2 * 2 * 4];
        return new VideoFrame(
            width: 2,
            height: 2,
            pixelFormat: VideoPixelFormat.Rgba32,
            pixelFormatData: new Rgba32PixelFormatData(),
            presentationTime: TimeSpan.Zero,
            isKeyFrame: true,
            plane0: rgba,
            plane0Stride: 8);
    }
}
