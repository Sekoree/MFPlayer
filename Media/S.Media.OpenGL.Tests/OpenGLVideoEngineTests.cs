using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL;
using S.Media.OpenGL.Output;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGLVideoEngineTests
{
    [Fact]
    public void AttachCloneOutput_RejectsSelfAttach()
    {
        using var engine = new OpenGLVideoEngine();
        using var output = new OpenGLVideoOutput();
        Assert.Equal(MediaResult.Success, engine.AddOutput(output));
        Assert.Equal(MediaResult.Success, output.Start(new VideoOutputConfig()));

        var code = engine.AttachCloneOutput(output.Id, output.Id);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneSelfAttachRejected, code);
    }

    [Fact]
    public void AttachCloneOutput_RejectsCycle()
    {
        using var engine = new OpenGLVideoEngine();
        using var root = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();

        Assert.Equal(MediaResult.Success, engine.AddOutput(root));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));
        Assert.Equal(MediaResult.Success, root.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, child.Start(new VideoOutputConfig()));

        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(root.Id, child.Id));

        var code = engine.AttachCloneOutput(child.Id, root.Id);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneCycleDetected, code);
        Assert.Equal(root.Id, child.CloneParentOutputId);
        Assert.Contains(child.Id, root.CloneOutputIds);
    }

    [Fact]
    public void AttachCloneOutput_Failure_IsAtomic_WhenParentNotRunning()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();

        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));

        var code = engine.AttachCloneOutput(parent.Id, child.Id);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneParentNotInitialized, code);
        Assert.Null(child.CloneParentOutputId);
        Assert.DoesNotContain(child.Id, parent.CloneOutputIds);
    }

    [Fact]
    public void DetachCloneOutput_ReturnsNotAttached_WhenCloneIsNotChild()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();

        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));
        Assert.Equal(MediaResult.Success, parent.Start(new VideoOutputConfig()));

        var code = engine.DetachCloneOutput(parent.Id, child.Id);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneNotAttached, code);
    }

    [Fact]
    public void PushFrame_PropagatesCommittedGeneration_ToAttachedRunningClone()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();
        using var frame = CreateFrame();

        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));
        Assert.Equal(MediaResult.Success, parent.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, child.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(parent.Id, child.Id));
        Assert.Equal(MediaResult.Success, engine.SetActiveOutput(parent.Id));

        var code = engine.PushFrame(frame, TimeSpan.Zero);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(parent.LastPresentedFrameGeneration, child.LastPresentedFrameGeneration);
        Assert.Equal(parent.Surface.LastPresentedFrameGeneration, child.Surface.LastPresentedFrameGeneration);
    }

    [Fact]
    public void AttachCloneOutput_ReturnsDepthExceeded_WhenConfiguredDepthWouldBeExceeded()
    {
        using var engine = new OpenGLVideoEngine(new OpenGLClonePolicyOptions { MaxCloneDepth = 1 });
        using var root = new OpenGLVideoOutput();
        using var middle = new OpenGLVideoOutput();
        using var leaf = new OpenGLVideoOutput();

        Assert.Equal(MediaResult.Success, engine.AddOutput(root));
        Assert.Equal(MediaResult.Success, engine.AddOutput(middle));
        Assert.Equal(MediaResult.Success, engine.AddOutput(leaf));
        Assert.Equal(MediaResult.Success, root.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, middle.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, leaf.Start(new VideoOutputConfig()));

        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(root.Id, middle.Id));

        var code = engine.AttachCloneOutput(middle.Id, leaf.Id);

        Assert.Equal((int)MediaErrorCode.OpenGLCloneMaxDepthExceeded, code);
        Assert.Null(leaf.CloneParentOutputId);
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

