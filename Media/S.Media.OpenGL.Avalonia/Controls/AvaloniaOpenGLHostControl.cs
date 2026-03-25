using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using S.Media.Core.Errors;
using S.Media.Core.Diagnostics;
using S.Media.OpenGL.Avalonia.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Controls;

public sealed class AvaloniaOpenGLHostControl : OpenGlControlBase
{
    private readonly Lock _gate = new();
    private OpenGLVideoOutput _output;
    private bool _glInitialized;
    private long _lastRenderedGeneration = -1;
    private int _renderRequestQueued;

    public AvaloniaOpenGLHostControl(OpenGLVideoOutput output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public bool EnableHudOverlay { get; set; }

    public MediaHudOverlay HudOverlay { get; } = new();

    public OpenGLVideoOutput Output
    {
        get
        {
            lock (_gate)
            {
                return _output;
            }
        }
    }

    public OpenGLSurfaceMetadata Surface
    {
        get
        {
            lock (_gate)
            {
                return _output.Surface;
            }
        }
    }

    public int ReplaceOutput(OpenGLVideoOutput? output)
    {
        if (output is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            _output = output;
            _lastRenderedGeneration = -1;
        }

        QueueRenderRequest();
        return MediaResult.Success;
    }

    public int UpdateHud(DebugInfo debugInfo)
    {
        lock (_gate)
        {
            HudOverlay.Update(debugInfo);
        }

        if (EnableHudOverlay)
        {
            QueueRenderRequest();
        }

        return MediaResult.Success;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        lock (_gate)
        {
            _glInitialized = true;
            _lastRenderedGeneration = -1;
        }

        QueueRenderRequest();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        OpenGLSurfaceMetadata surface;
        bool shouldRender;

        lock (_gate)
        {
            if (!_glInitialized)
            {
                return;
            }

            surface = _output.Surface;
            shouldRender = surface.LastPresentedFrameGeneration != _lastRenderedGeneration || EnableHudOverlay;

            if (shouldRender)
            {
                _lastRenderedGeneration = surface.LastPresentedFrameGeneration;
            }

            _ = fb;
            _ = gl;
        }

        if (!shouldRender)
        {
            return;
        }

        if (EnableHudOverlay)
        {
            _ = HudOverlay.BuildOverlayTextSnapshot();
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        lock (_gate)
        {
            _glInitialized = false;
            _lastRenderedGeneration = -1;
        }

        base.OnOpenGlDeinit(gl);
    }

    private void QueueRenderRequest()
    {
        if (Interlocked.Exchange(ref _renderRequestQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _renderRequestQueued, 0);
            RequestNextFrameRendering();
        });
    }

}

