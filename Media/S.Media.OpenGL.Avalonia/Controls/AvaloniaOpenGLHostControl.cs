using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using S.Media.Core.Errors;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL.Avalonia.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.Avalonia.Controls;

public sealed class AvaloniaOpenGLHostControl : OpenGlControlBase
{
    private readonly Lock _gate = new();
    private OpenGLVideoOutput _output;
    private readonly AvaloniaGLRenderer _renderer = new();
    private bool _glInitialized;
    private long _lastRenderedGeneration = -1;
    private int _renderRequestQueued;
    private VideoFrame? _lastFrame;

    public AvaloniaOpenGLHostControl(OpenGLVideoOutput output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public bool KeepAspectRatio { get; set; } = true;

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

    /// <summary>
    /// Push a video frame for rendering. The control will present it on the next render cycle.
    /// </summary>
    public void PushFrame(VideoFrame frame)
    {
        lock (_gate)
        {
            _lastFrame = frame;
        }

        QueueRenderRequest();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _renderer.Initialize(gl);

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
        bool keepPumping;
        VideoFrame? frame;

        lock (_gate)
        {
            if (!_glInitialized || !_renderer.IsReady)
            {
                return;
            }

            surface = _output.Surface;
            shouldRender = surface.LastPresentedFrameGeneration != _lastRenderedGeneration
                           || _lastFrame != null
                           || EnableHudOverlay;
            keepPumping = _output.IsRunning;
            frame = _lastFrame;

            if (shouldRender)
            {
                _lastRenderedGeneration = surface.LastPresentedFrameGeneration;
            }
        }

        if (!shouldRender)
        {
            return;
        }

        if (frame != null)
        {
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            var pixelWidth = Math.Max(1, (int)(Bounds.Width * scaling));
            var pixelHeight = Math.Max(1, (int)(Bounds.Height * scaling));

            _renderer.RenderFrame(gl, fb, frame, pixelWidth, pixelHeight, KeepAspectRatio);
        }

        if (EnableHudOverlay)
        {
            _ = HudOverlay.BuildOverlayTextSnapshot();
        }

        if (keepPumping)
        {
            QueueRenderRequest();
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.Deinitialize(gl);

        lock (_gate)
        {
            _glInitialized = false;
            _lastRenderedGeneration = -1;
            _lastFrame = null;
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
