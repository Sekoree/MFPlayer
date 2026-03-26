using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGLDiagnosticsContractsTests
{
    [Fact]
    public void PublishSurfaceChanged_RaisesEvent()
    {
        using var eventsHub = new OpenGLDiagnosticsEvents();
        var raised = false;
        eventsHub.SurfaceChanged += (_, surface) => raised = surface.SurfaceWidth == 2;

        eventsHub.PublishSurfaceChanged(new OpenGLSurfaceMetadata(2, 2, 2, 2, VideoPixelFormat.Rgba32, 1, new[] { 8 }, 1));

        Assert.True(raised);
    }

    [Fact]
    public void Dispose_ActsAsPublicationFence()
    {
        var eventsHub = new OpenGLDiagnosticsEvents();
        var raised = 0;
        eventsHub.DiagnosticsUpdated += (_, _) => raised++;
        eventsHub.Dispose();

        eventsHub.PublishDiagnosticsUpdated(Guid.NewGuid(), new OpenGLOutputDebugInfo(1, 0, 0, 0.1, 0.2, OpenGLSurfaceMetadata.Empty));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Engine_AttachAndDetach_PublishCloneGraphEvents()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();
        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));
        Assert.Equal(MediaResult.Success, parent.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, child.Start(new VideoOutputConfig()));

        var changes = new List<OpenGLCloneGraphChangeKind>();
        engine.Diagnostics.CloneGraphChanged += (_, e) => changes.Add(e.ChangeKind);

        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(parent.Id, child.Id));
        Assert.Equal(MediaResult.Success, engine.DetachCloneOutput(parent.Id, child.Id));

        Assert.Equal([OpenGLCloneGraphChangeKind.Attached, OpenGLCloneGraphChangeKind.Detached], changes);
    }

    [Fact]
    public void Engine_PushFrame_PublishesSurfaceAndDiagnosticsEvents()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, parent.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, engine.SetActiveOutput(parent.Id));
        using var frame = CreateFrame();

        var surfaceEvents = 0;
        var diagnosticsEvents = 0;
        engine.Diagnostics.SurfaceChanged += (_, _) => surfaceEvents++;
        engine.Diagnostics.DiagnosticsUpdated += (_, _) => diagnosticsEvents++;

        Assert.Equal(MediaResult.Success, engine.PushFrame(frame, TimeSpan.Zero));

        Assert.True(surfaceEvents >= 1);
        Assert.True(diagnosticsEvents >= 1);
    }

    [Fact]
    public void Output_PushDisposedFrame_PublishesDroppedDiagnostics()
    {
        using var engine = new OpenGLVideoEngine();
        using var output = new OpenGLVideoOutput();
        Assert.Equal(MediaResult.Success, engine.AddOutput(output));
        Assert.Equal(MediaResult.Success, output.Start(new VideoOutputConfig()));
        using var frame = CreateFrame();
        frame.Dispose();

        OpenGLOutputDebugInfo? observed = null;
        engine.Diagnostics.DiagnosticsUpdated += (_, e) =>
        {
            if (e.OutputId == output.Id)
            {
                observed = e.Snapshot;
            }
        };

        var code = output.PushFrame(frame, TimeSpan.Zero);

        Assert.Equal((int)MediaErrorCode.VideoFrameDisposed, code);
        Assert.True(observed.HasValue);
        Assert.True(observed.Value.FramesDropped >= 1);
    }

    [Fact]
    public void RemoveOutput_Parent_CascadesToCloneSubtree()
    {
        using var engine = new OpenGLVideoEngine();
        using var parent = new OpenGLVideoOutput();
        using var child = new OpenGLVideoOutput();
        using var grandChild = new OpenGLVideoOutput();
        Assert.Equal(MediaResult.Success, engine.AddOutput(parent));
        Assert.Equal(MediaResult.Success, engine.AddOutput(child));
        Assert.Equal(MediaResult.Success, engine.AddOutput(grandChild));
        Assert.Equal(MediaResult.Success, parent.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, child.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, grandChild.Start(new VideoOutputConfig()));
        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(parent.Id, child.Id));
        Assert.Equal(MediaResult.Success, engine.AttachCloneOutput(child.Id, grandChild.Id));

        Assert.Equal(MediaResult.Success, engine.RemoveOutput(parent.Id));

        Assert.Empty(engine.Outputs);
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

