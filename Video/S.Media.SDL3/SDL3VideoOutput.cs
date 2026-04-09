using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Video;
using SDL = global::SDL3.SDL;

namespace S.Media.SDL3;

/// <summary>
/// SDL3 + OpenGL backed video output.
/// Creates an SDL3 window with an OpenGL 3.3 core-profile context and runs a
/// vsync-driven render loop on a dedicated thread.
/// Analogous to <c>PortAudioOutput</c> for audio.
/// </summary>
public sealed class SDL3VideoOutput : IVideoOutput
{
    // ── SDL / GL state ────────────────────────────────────────────────────

    private nint         _window;
    private nint         _glContext;
    private GLRenderer?  _renderer;
    private VideoMixer?  _mixer;
    private VideoPtsClock? _clock;
    private VideoFormat  _outputFormat;

    // ── Render thread ─────────────────────────────────────────────────────

    private Thread?                  _renderThread;
    private CancellationTokenSource? _cts;
    private volatile bool            _isRunning;
    private volatile bool            _closeRequested;
    private bool                     _disposed;

    // ── IVideoOutput / IMediaOutput ───────────────────────────────────────

    /// <inheritdoc/>
    public VideoFormat OutputFormat => _outputFormat;

    /// <inheritdoc/>
    public IVideoMixer Mixer => _mixer ?? throw new InvalidOperationException("Call Open() first.");

    /// <inheritdoc/>
    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    /// <inheritdoc/>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Raised when the user closes the SDL window (click × or Alt+F4).
    /// The output will stop its render loop automatically; the application
    /// should stop decoders and dispose resources in response.
    /// </summary>
    public event Action? WindowClosed;

    // ── Open ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Open(string title, int width, int height, VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window != nint.Zero)
            throw new InvalidOperationException("Output is already open. Dispose first.");

        // ── SDL init ──────────────────────────────────────────────────────
        if (!SDL.Init(SDL.InitFlags.Video))
        {
            var err = SDL.GetError();
            throw new InvalidOperationException($"SDL_Init failed: {err}");
        }

        // ── GL attributes (must be set before window creation) ────────────
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask,
            (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

        // ── Window ────────────────────────────────────────────────────────
        _window = SDL.CreateWindow(
            title, width, height,
            SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable);

        if (_window == nint.Zero)
        {
            var err = SDL.GetError();
            throw new InvalidOperationException($"SDL_CreateWindow failed: {err}");
        }

        // ── GL context ────────────────────────────────────────────────────
        _glContext = SDL.GLCreateContext(_window);
        if (_glContext == nint.Zero)
        {
            var err = SDL.GetError();
            SDL.DestroyWindow(_window);
            _window = nint.Zero;
            throw new InvalidOperationException(
                $"SDL_GL_CreateContext failed: {err}");
        }

        // Vsync
        SDL.GLSetSwapInterval(1);

        // ── GL renderer (context is current on this thread) ───────────────
        _renderer = new GLRenderer();
        _renderer.Initialise(width, height);

        // Release the GL context from the calling thread so the render thread
        // can claim it via GLMakeCurrent.
        SDL.GLMakeCurrent(_window, nint.Zero);

        // ── Pipeline objects ──────────────────────────────────────────────
        _outputFormat = format;
        _mixer = new VideoMixer(format);
        _clock = new VideoPtsClock(
            sampleRate: format.FrameRate > 0 ? format.FrameRate : 30);
    }

    // ── Start / Stop ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window == nint.Zero)
            throw new InvalidOperationException("Call Open() before Start.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _closeRequested = false;

        _renderThread = new Thread(RenderLoop)
        {
            Name         = "SDL3VideoOutput.Render",
            IsBackground = true
        };
        _renderThread.Start();

        _clock!.Start();
        _isRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;

        return Task.Run(() =>
        {
            _clock?.Stop();
            _isRunning = false;
            _cts?.Cancel();
            _renderThread?.Join(TimeSpan.FromSeconds(3));
        }, ct);
    }

    // ── Render loop (dedicated thread, vsync-paced) ───────────────────────

    private void RenderLoop()
    {
        // Claim the GL context on this thread.
        SDL.GLMakeCurrent(_window, _glContext);

        var token = _cts!.Token;

        while (!token.IsCancellationRequested && !_closeRequested)
        {
            // ── Event pump ────────────────────────────────────────────────
            while (SDL.PollEvent(out var evt))
            {
                var eventType = (SDL.EventType)evt.Type;

                switch (eventType)
                {
                    case SDL.EventType.Quit:
                    case SDL.EventType.WindowCloseRequested:
                        _closeRequested = true;
                        break;

                    case SDL.EventType.WindowResized:
                    case SDL.EventType.WindowPixelSizeChanged:
                        SDL.GetWindowSize(_window, out int w, out int h);
                        _renderer!.SetViewport(w, h);
                        break;
                }

                if (_closeRequested) break;
            }

            if (_closeRequested) break;

            // ── Present frame ─────────────────────────────────────────────
            var frame = _mixer!.PresentNextFrame();

            if (frame.HasValue)
            {
                _renderer!.UploadAndDraw(frame.Value);
                _clock!.UpdateFromFrame(frame.Value.Pts);
            }
            else
            {
                _renderer!.DrawBlack();
            }

            // ── Swap (paced by vsync) ─────────────────────────────────────
            SDL.GLSwapWindow(_window);
        }

        // Release GL context from this thread.
        SDL.GLMakeCurrent(_window, nint.Zero);

        // Notify the application that the window was closed (if user-initiated).
        if (_closeRequested)
        {
            _isRunning = false;
            _clock?.Stop();
            WindowClosed?.Invoke();
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the render loop if it is still running.
        if (_isRunning)
        {
            _clock?.Stop();
            _isRunning = false;
            _cts?.Cancel();
            _renderThread?.Join(TimeSpan.FromSeconds(3));
        }

        // GL resources must be destroyed with the context current.
        if (_window != nint.Zero && _glContext != nint.Zero)
        {
            SDL.GLMakeCurrent(_window, _glContext);
            _renderer?.Dispose();
            SDL.GLDestroyContext(_glContext);
            _glContext = nint.Zero;
        }

        if (_window != nint.Zero)
        {
            SDL.DestroyWindow(_window);
            _window = nint.Zero;
        }

        _mixer?.Dispose();
        _clock?.Dispose();
        _cts?.Dispose();

        SDL.Quit();
    }
}

