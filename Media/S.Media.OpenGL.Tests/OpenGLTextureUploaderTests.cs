using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;
using S.Media.OpenGL.Upload;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGLTextureUploaderTests
{
    [Fact]
    public void Upload_IncrementsGeneration_OnSuccess()
    {
        var uploader = new OpenGLTextureUploader();
        using var frame = CreateRgbaFrame();
        var plan = new UploadPlan(VideoPixelFormat.Rgba32, OpenGLCloneMode.SharedTexture, false);

        Assert.Equal(MediaResult.Success, uploader.Upload(frame, plan));
        Assert.Equal(1, uploader.LastUploadGeneration);
    }

    [Fact]
    public void Upload_PropagatesDelegateFailure_WithoutGenerationMutation()
    {
        var uploader = new OpenGLTextureUploader((_, _) => (int)MediaErrorCode.OpenGLCloneCreationFailed);
        using var frame = CreateRgbaFrame();
        var plan = new UploadPlan(VideoPixelFormat.Rgba32, OpenGLCloneMode.SharedTexture, false);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneCreationFailed, uploader.Upload(frame, plan));
        Assert.Equal(0, uploader.LastUploadGeneration);
    }

    [Fact]
    public void Upload_ReturnsPixelFormatIncompatible_ForMismatchedPlanFormat()
    {
        var uploader = new OpenGLTextureUploader();
        using var frame = CreateRgbaFrame();
        var plan = new UploadPlan(VideoPixelFormat.Nv12, OpenGLCloneMode.SharedTexture, true);

        Assert.Equal((int)MediaErrorCode.OpenGLClonePixelFormatIncompatible, uploader.Upload(frame, plan));
    }

    [Fact]
    public void Reset_ClearsLastUploadGeneration()
    {
        var uploader = new OpenGLTextureUploader();
        using var frame = CreateRgbaFrame();
        var plan = new UploadPlan(VideoPixelFormat.Rgba32, OpenGLCloneMode.SharedTexture, false);

        Assert.Equal(MediaResult.Success, uploader.Upload(frame, plan));
        Assert.Equal(1, uploader.LastUploadGeneration);

        Assert.Equal(MediaResult.Success, uploader.Reset());
        Assert.Equal(0, uploader.LastUploadGeneration);
    }

    private static VideoFrame CreateRgbaFrame()
    {
        var rgba = new byte[16];
        return new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, rgba, 8);
    }
}
