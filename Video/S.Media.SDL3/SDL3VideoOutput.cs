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
    public readonly record struct DiagnosticsSnapshot(
        long LoopIterations,
        long PresentedFrames,
        long BlackFrames,
        long BgraFrames,
        long RgbaFrames,
        long Nv12Frames,
        long Yuv420pFrames,
        long Yuv422p10Frames,
        long OtherFrames,
        long SwapCalls,
        long ResizeEvents,
        long RenderExceptions,
        long GlMakeCurrentFailures);

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
    private long                     _loopIterations;
    private long                     _presentedFrames;
    private long                     _blackFrames;
    private long                     _bgraFrames;
    private long                     _rgbaFrames;
    private long                     _nv12Frames;
    private long                     _yuv420pFrames;
    private long                     _yuv422p10Frames;
    private long                     _otherFrames;
    private long                     _swapCalls;
    private long                     _resizeEvents;
    private long                     _renderExceptions;
    private long                     _glMakeCurrentFailures;
    private volatile int             _yuvColorRange = (int)YuvColorRange.Auto;
    private volatile int             _yuvColorMatrix = (int)YuvColorMatrix.Auto;

    public YuvColorRange YuvColorRange
    {
        get => (YuvColorRange)_yuvColorRange;
        set
        {
            var normalized = NormalizeColorRange(value);
            _yuvColorRange = (int)normalized;
            if (_renderer != null)
                _renderer.YuvColorRange = normalized;
        }
    }

    public YuvColorMatrix YuvColorMatrix
    {
        get => (YuvColorMatrix)_yuvColorMatrix;
        set
        {
            var normalized = NormalizeColorMatrix(value);
            _yuvColorMatrix = (int)normalized;
            if (_renderer != null)
                _renderer.YuvColorMatrix = normalized;
        }
    }

    public YuvColorRange Yuv422p10ColorRange
    {
        get => YuvColorRange;
        set => YuvColorRange = value;
    }

    /// <summary>
    /// Controls YUV422P10 shader normalization mode.
    /// False = full range, true = limited/studio range.
    /// </summary>
    public bool Yuv422p10LimitedRange
    {
        get => Yuv422p10ColorRange == YuvColorRange.Limited;
        set => Yuv422p10ColorRange = value ? YuvColorRange.Limited : YuvColorRange.Full;
    }

    public YuvColorMatrix Yuv422p10ColorMatrix
    {
        get => YuvColorMatrix;
        set => YuvColorMatrix = value;
    }

    /// <summary>
    /// Legacy bool view for matrix selection.
    /// False = BT.601, true = BT.709.
    /// </summary>
    public bool Yuv422p10UseBt709Matrix
    {
        get => Yuv422p10ColorMatrix == YuvColorMatrix.Bt709;
        set => Yuv422p10ColorMatrix = value ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601;
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        LoopIterations: Interlocked.Read(ref _loopIterations),
        PresentedFrames: Interlocked.Read(ref _presentedFrames),
        BlackFrames: Interlocked.Read(ref _blackFrames),
        BgraFrames: Interlocked.Read(ref _bgraFrames),
        RgbaFrames: Interlocked.Read(ref _rgbaFrames),
        Nv12Frames: Interlocked.Read(ref _nv12Frames),
        Yuv420pFrames: Interlocked.Read(ref _yuv420pFrames),
        Yuv422p10Frames: Interlocked.Read(ref _yuv422p10Frames),
        OtherFrames: Interlocked.Read(ref _otherFrames),
        SwapCalls: Interlocked.Read(ref _swapCalls),
        ResizeEvents: Interlocked.Read(ref _resizeEvents),
        RenderExceptions: Interlocked.Read(ref _renderExceptions),
        GlMakeCurrentFailures: Interlocked.Read(ref _glMakeCurrentFailures));

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
        _renderer.YuvColorRange = YuvColorRange;
        _renderer.YuvColorMatrix = YuvColorMatrix;
        _renderer.Initialise(width, height);

        // Release the GL context from the calling thread so the render thread
        // can claim it via GLMakeCurrent.
        SDL.GLMakeCurrent(_window, nint.Zero);

        // ── Pipeline objects ──────────────────────────────────────────────
        var leaderPixelFormat = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(
            format,
            supportsNv12: true,
            supportsYuv420p: true,
            supportsYuv422p10: true,
            fallback: PixelFormat.Bgra32);
        _outputFormat = format with { PixelFormat = leaderPixelFormat };
        _mixer = new VideoMixer(_outputFormat);
        _clock = new VideoPtsClock(
            sampleRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);
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
        if (!SDL.GLMakeCurrent(_window, _glContext))
        {
            Interlocked.Increment(ref _glMakeCurrentFailures);
            Console.Error.WriteLine($"[SDL3VideoOutput] SDL_GL_MakeCurrent failed: {SDL.GetError()}");
            _isRunning = false;
            return;
        }

        var token = _cts!.Token;

        while (!token.IsCancellationRequested && !_closeRequested)
        {
            Interlocked.Increment(ref _loopIterations);

            try
            {
                // ── Event pump ────────────────────────────────────────────
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
                            Interlocked.Increment(ref _resizeEvents);
                            break;
                    }

                    if (_closeRequested) break;
                }

                if (_closeRequested) break;

                // ── Present frame ─────────────────────────────────────────
                var frame = _mixer!.PresentNextFrame(_clock!.Position);

                if (frame.HasValue)
                {
                    switch (frame.Value.PixelFormat)
                    {
                        case PixelFormat.Bgra32:
                            Interlocked.Increment(ref _bgraFrames);
                            break;
                        case PixelFormat.Rgba32:
                            Interlocked.Increment(ref _rgbaFrames);
                            break;
                        case PixelFormat.Nv12:
                            Interlocked.Increment(ref _nv12Frames);
                            break;
                        case PixelFormat.Yuv420p:
                            Interlocked.Increment(ref _yuv420pFrames);
                            break;
                        case PixelFormat.Yuv422p10:
                            Interlocked.Increment(ref _yuv422p10Frames);
                            break;
                        default:
                            Interlocked.Increment(ref _otherFrames);
                            break;
                    }

                    _renderer!.UploadAndDraw(frame.Value);
                    _clock!.UpdateFromFrame(frame.Value.Pts);
                    Interlocked.Increment(ref _presentedFrames);
                }
                else
                {
                    _renderer!.DrawBlack();
                    Interlocked.Increment(ref _blackFrames);
                }

                // ── Swap (paced by vsync) ─────────────────────────────────
                SDL.GLSwapWindow(_window);
                Interlocked.Increment(ref _swapCalls);
            }
            catch (Exception ex)
            {
                long ec = Interlocked.Increment(ref _renderExceptions);
                if (ec <= 3 || ec % 100 == 0)
                    Console.Error.WriteLine($"[SDL3VideoOutput] render-loop exception (count={ec}): {ex}");
            }
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

    private static YuvColorRange NormalizeColorRange(YuvColorRange value)
    {
        return value is YuvColorRange.Auto or YuvColorRange.Full or YuvColorRange.Limited
            ? value
            : YuvColorRange.Auto;
    }

    private static YuvColorMatrix NormalizeColorMatrix(YuvColorMatrix value)
    {
        return value is YuvColorMatrix.Auto or YuvColorMatrix.Bt601 or YuvColorMatrix.Bt709
            ? value
            : YuvColorMatrix.Auto;
    }
}

