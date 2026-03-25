using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;
using S.Media.OpenGL.Upload;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGLUploadPlannerTests
{
    [Fact]
    public void CreatePlan_UsesSharedTexture_WhenCapabilitySupportsTextureSharing()
    {
        var planner = new OpenGLUploadPlanner();
        _ = planner.UpdateCapabilities(new OpenGLCapabilitySnapshot(true, true, 4096, false));
        using var frame = CreateRgbaFrame();

        var plan = planner.CreatePlan(frame);

        Assert.Equal(OpenGLCloneMode.SharedTexture, plan.PreferredPath);
        Assert.False(plan.RequiresGpuConversion);
    }

    [Fact]
    public void CreatePlan_RequiresGpuConversion_ForYuvFormats()
    {
        var planner = new OpenGLUploadPlanner();
        using var frame = CreateYuv420Frame();

        var plan = planner.CreatePlan(frame);

        Assert.True(plan.RequiresGpuConversion);
        Assert.Equal(VideoPixelFormat.Yuv420P, plan.PixelFormat);
    }

    [Fact]
    public void Supports_ReturnsFalse_ForUnknownPixelFormat()
    {
        var planner = new OpenGLUploadPlanner();

        Assert.False(planner.Supports(VideoPixelFormat.Unknown));
        Assert.True(planner.Supports(VideoPixelFormat.Rgba32));
    }

    private static VideoFrame CreateRgbaFrame()
    {
        var rgba = new byte[16];
        return new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, rgba, 8);
    }

    private static VideoFrame CreateYuv420Frame()
    {
        var y = new byte[] { 16, 32, 48, 64 };
        var u = new byte[] { 128 };
        var v = new byte[] { 128 };
        return new VideoFrame(
            2,
            2,
            VideoPixelFormat.Yuv420P,
            new Yuv420PPixelFormatData(),
            TimeSpan.Zero,
            true,
            y,
            2,
            u,
            1,
            v,
            1);
    }
}

