using Avalonia.OpenGL.Controls;
using S.Media.Core.Errors;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL.Avalonia.Controls;
using S.Media.OpenGL.Avalonia.Output;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class AvaloniaAdapterTests
{
    [Fact]
    public void HostControl_InheritsOpenGlControlBase()
    {
        using var output = new OpenGLVideoOutput();
        var host = new AvaloniaOpenGLHostControl(output);

        Assert.IsAssignableFrom<OpenGlControlBase>(host);
    }

    [Fact]
    public void HostControl_ReplaceOutput_IsDeterministic_AndFailureAtomicForNull()
    {
        var original = new OpenGLVideoOutput();
        var host = new AvaloniaOpenGLHostControl(original);

        var code = host.ReplaceOutput(null!);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, code);
        Assert.Same(original, host.Output);
    }

    [Fact]
    public void AvaloniaVideoOutput_AttachDetachClone_UsesOpenGLCloneCodes()
    {
        using var parent = new AvaloniaVideoOutput();
        using var child = new AvaloniaVideoOutput();

        Assert.Equal(MediaResult.Success, parent.AttachClone(child, new AvaloniaCloneOptions()));
        Assert.Equal(parent.Id, child.CloneParentOutputId);
        Assert.Equal((int)MediaErrorCode.OpenGLCloneChildAlreadyAttached, parent.AttachClone(child, new AvaloniaCloneOptions()));
        Assert.Equal(MediaResult.Success, parent.DetachClone(child.Id));
        Assert.Null(child.CloneParentOutputId);
        Assert.Equal((int)MediaErrorCode.OpenGLCloneNotAttached, parent.DetachClone(child.Id));
    }

    [Fact]
    public void AvaloniaVideoOutput_DelegatesPushToUnderlyingOpenGLOutput()
    {
        using var output = new AvaloniaVideoOutput();
        using var frame = CreateFrame();

        Assert.Equal(MediaResult.Success, output.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, output.PushFrame(frame));
    }

    [Fact]
    public void HudOverlay_BuildsTextFromUpdatedDebugInfo()
    {
        using var output = new OpenGLVideoOutput();
        var host = new AvaloniaOpenGLHostControl(output)
        {
            EnableHudOverlay = true,
        };

        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("render.fps", DebugValueKind.Scalar, 60.0, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("video.fps", DebugValueKind.Scalar, 59.9, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("pixel.format", DebugValueKind.Scalar, "nv12->rgba", DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("queue.depth", DebugValueKind.Scalar, 2, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("upload.ms", DebugValueKind.Scalar, 0.25, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("av.drift.ms", DebugValueKind.Scalar, 0.6, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("gpu.decode", DebugValueKind.Scalar, true, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, host.UpdateHud(new DebugInfo("drop.frames", DebugValueKind.Scalar, 3, DateTimeOffset.UtcNow)));

        var text = host.HudOverlay.BuildOverlayTextSnapshot();
        Assert.Equal("R:60.0 V:59.9 NV12/RGBA\nQ:2 U:0.25 AV:0.6 GPU:1 D:3", text);
    }

    private static VideoFrame CreateFrame()
    {
        var rgba = new byte[16];
        return new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, rgba, 8);
    }
}

