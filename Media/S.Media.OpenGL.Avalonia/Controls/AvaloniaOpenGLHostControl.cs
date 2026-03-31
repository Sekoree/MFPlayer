using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using S.Media.Core.Errors;
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

    public int UpdateHud(HudEntry entry)
    {
        lock (_gate)
        {
            HudOverlay.Update(entry);
        }

        if (EnableHudOverlay)
        {
            QueueRenderRequest();
        }

        return MediaResult.Success;
    }

    /// <summary>
    /// Push a video frame for rendering. The control will present it on the next render cycle.
    /// Takes a reference via <see cref="VideoFrame.AddRef"/> so the caller may safely dispose
    /// the frame immediately after this call returns.
    /// </summary>
    public void PushFrame(VideoFrame frame)
    {
        VideoFrame? previous;
        lock (_gate)
        {
            previous = _lastFrame;
            _lastFrame = frame.AddRef();   // take ownership of a reference
        }

        previous?.Dispose();               // release the old reference outside the lock
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
            frame = _lastFrame;
            _lastFrame = null;             // take ownership; clear so the ref is not held past this render pass
            shouldRender = surface.LastPresentedFrameGeneration != _lastRenderedGeneration
                           || frame != null
                           || EnableHudOverlay;
            keepPumping = _output.IsRunning;

            if (shouldRender)
            {
                _lastRenderedGeneration = surface.LastPresentedFrameGeneration;
            }
        }

        if (!shouldRender)
        {
            frame?.Dispose();              // release the ref we took even if we are not rendering
            return;
        }

        if (frame != null)
        {
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            var pixelWidth = Math.Max(1, (int)(Bounds.Width * scaling));
            var pixelHeight = Math.Max(1, (int)(Bounds.Height * scaling));

            _renderer.RenderFrame(gl, fb, frame, pixelWidth, pixelHeight, KeepAspectRatio);
            _output.UpdateTimings(_renderer.LastUploadMs, _renderer.LastPresentMs);
            frame.Dispose();              // release our ownership ref after upload
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

        VideoFrame? lastFrame;
        lock (_gate)
        {
            _glInitialized = false;
            _lastRenderedGeneration = -1;
            lastFrame = _lastFrame;
            _lastFrame = null;
        }

        lastFrame?.Dispose();

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
