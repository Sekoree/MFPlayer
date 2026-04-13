using Microsoft.Extensions.Logging;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.Core;
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
    private static readonly ILogger Log = SDL3VideoLogging.GetLogger(nameof(SDL3VideoOutput));

    public readonly record struct DiagnosticsSnapshot(
        long LoopIterations,
        long PresentedFrames,
        long BlackFrames,
        long BgraFrames,
        long RgbaFrames,
        long Nv12Frames,
        long Yuv420pFrames,
        long Yuv422p10Frames,
        long Uyvy422Frames,
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
    private volatile IVideoMixer? _activeMixer;
    private VideoPtsClock? _clock;
    private volatile IMediaClock? _presentationClockOverride;
    private long _presentationClockOriginTicks;
    private int _hasPresentationClockOrigin;
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
    private long                     _uyvy422Frames;
    private long                     _otherFrames;
    private long                     _swapCalls;
    private long                     _resizeEvents;
    private long                     _renderExceptions;
    private long                     _glMakeCurrentFailures;
    private volatile int             _yuvColorRange = (int)YuvColorRange.Auto;
    private volatile int             _yuvColorMatrix = (int)YuvColorMatrix.Auto;
    // Set to true when the user explicitly overrides the YUV hints; suppresses auto-detect.
    private volatile bool            _hasYuvHintsOverride;
    // Per-frame auto-hint tracking (render thread only — no lock needed).
    private YuvColorMatrix           _lastAutoMatrix = YuvColorMatrix.Auto;
    private YuvColorRange            _lastAutoRange  = YuvColorRange.Auto;

    // ── SDL init ref-counting ─────────────────────────────────────────────
    // Multiple SDL3VideoOutput instances may coexist; only the last Dispose
    // call should invoke SDL.Quit.
    private static int _sdlRefCount;
    private bool _sdlInitOwned;

    private readonly Lock _cloneLock = new();
    private SDL3VideoCloneSink[] _clones = [];

    // ── YUV shader config ─────────────────────────────────────────────────

    /// <summary>
    /// Combined YUV color-range and color-matrix hint used by the GL shader.
    /// Updating this property is thread-safe; the render thread picks up the
    /// new values on the next frame.
    /// </summary>
    public YuvShaderConfig YuvConfig
    {
        get => new((YuvColorRange)_yuvColorRange, (YuvColorMatrix)_yuvColorMatrix);
        set
        {
            var range  = NormalizeColorRange(value.Range);
            var matrix = NormalizeColorMatrix(value.Matrix);
            _hasYuvHintsOverride = true;
            _yuvColorRange  = (int)range;
            _yuvColorMatrix = (int)matrix;
            if (_renderer != null)
            {
                _renderer.YuvColorRange  = range;
                _renderer.YuvColorMatrix = matrix;
            }
        }
    }

    /// <summary>YUV color range used by the GL shader. Shortcut for <see cref="YuvConfig"/>.</summary>
    public YuvColorRange YuvColorRange
    {
        get => (YuvColorRange)_yuvColorRange;
        set => YuvConfig = new(value, (YuvColorMatrix)_yuvColorMatrix);
    }

    /// <summary>YUV color matrix used by the GL shader. Shortcut for <see cref="YuvConfig"/>.</summary>
    public YuvColorMatrix YuvColorMatrix
    {
        get => (YuvColorMatrix)_yuvColorMatrix;
        set => YuvConfig = new((YuvColorRange)_yuvColorRange, value);
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
        Uyvy422Frames: Interlocked.Read(ref _uyvy422Frames),
        OtherFrames: Interlocked.Read(ref _otherFrames),
        SwapCalls: Interlocked.Read(ref _swapCalls),
        ResizeEvents: Interlocked.Read(ref _resizeEvents),
        RenderExceptions: Interlocked.Read(ref _renderExceptions),
        GlMakeCurrentFailures: Interlocked.Read(ref _glMakeCurrentFailures));

    // ── IVideoOutput / IMediaOutput ───────────────────────────────────────

    /// <inheritdoc/>
    public VideoFormat OutputFormat => _outputFormat;

    /// <inheritdoc/>
    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");
    public void OverridePresentationMixer(IVideoMixer mixer) => _activeMixer = mixer;

    /// <summary>
    /// Overrides the render-loop presentation clock.
    /// Set this to an audio hardware clock (for example <see cref="S.Media.PortAudio.PortAudioOutput.Clock"/>)
    /// to keep video pacing aligned with audio.
    /// </summary>
    public void OverridePresentationClock(IMediaClock? clock)
    {
        _presentationClockOverride = clock;
        Volatile.Write(ref _hasPresentationClockOrigin, 0);
        Volatile.Write(ref _presentationClockOriginTicks, 0);
    }


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

        // ── SDL init (ref-counted across instances) ───────────────────────
        AcquireSdlVideo();
        _sdlInitOwned = true;

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
        _renderer.YuvColorRange  = (YuvColorRange)_yuvColorRange;
        _renderer.YuvColorMatrix = (YuvColorMatrix)_yuvColorMatrix;
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
            supportsUyvy422: true,
            fallback: PixelFormat.Bgra32);
        _outputFormat = format with { PixelFormat = leaderPixelFormat };
        _mixer = new VideoMixer(_outputFormat);
        _activeMixer = _mixer;


        _clock = new VideoPtsClock(
            sampleRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);

        // Inform the renderer of the video dimensions so resize events produce
        // a correctly letterboxed/pillarboxed viewport from the start.
        if (_outputFormat.Width > 0 && _outputFormat.Height > 0)
            _renderer.SetVideoSize(_outputFormat.Width, _outputFormat.Height);

        Log.LogInformation("Opened SDL3VideoOutput: '{Title}' {Width}x{Height} px={PixelFormat}, fps={FrameRate}",
            title, _outputFormat.Width, _outputFormat.Height, _outputFormat.PixelFormat, _outputFormat.FrameRate);
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
        Volatile.Write(ref _hasPresentationClockOrigin, 0);
        Volatile.Write(ref _presentationClockOriginTicks, 0);

        _renderThread = new Thread(RenderLoop)
        {
            Name         = "SDL3VideoOutput.Render",
            IsBackground = true
        };
        _renderThread.Start();

        _clock!.Start();
        _isRunning = true;
        Log.LogInformation("SDL3VideoOutput started");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;

        Log.LogInformation("Stopping SDL3VideoOutput");
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
            Log.LogError("SDL_GL_MakeCurrent failed: {Error}", SDL.GetError());
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
                var mixer = _activeMixer ?? _mixer;
                if (mixer == null)
                    throw new InvalidOperationException("Presentation mixer is not available.");

                var externalClock = _presentationClockOverride;
                var presentationClock = externalClock ?? _clock;
                if (presentationClock == null)
                    throw new InvalidOperationException("Presentation clock is not available.");

                var clockPosition = presentationClock.Position;
                if (externalClock != null)
                {
                    long rawTicks = clockPosition.Ticks;
                    if (rawTicks < 0) rawTicks = 0;

                    if (Interlocked.CompareExchange(ref _hasPresentationClockOrigin, 1, 0) == 0)
                        Volatile.Write(ref _presentationClockOriginTicks, rawTicks);

                    long originTicks = Volatile.Read(ref _presentationClockOriginTicks);
                    long relTicks = rawTicks - originTicks;
                    if (relTicks < 0) relTicks = 0;
                    clockPosition = TimeSpan.FromTicks(relTicks);
                }

                var frame = mixer.PresentNextFrame(clockPosition);

                if (frame.HasValue)
                {
                    _renderer!.SetVideoSize(frame.Value.Width, frame.Value.Height);

                    // Auto-propagate IVideoColorMatrixHint from the active channel when no
                    // manual override has been set. O(1) comparison avoids redundant GL uniform
                    // updates when the matrix/range are stable across frames.
                    if (!_hasYuvHintsOverride && mixer.ActiveChannel is IVideoColorMatrixHint hint)
                    {
                        var m = hint.SuggestedYuvColorMatrix;
                        var r = hint.SuggestedYuvColorRange;
                        if (m != _lastAutoMatrix || r != _lastAutoRange)
                        {
                            _lastAutoMatrix = m;
                            _lastAutoRange  = r;
                            _renderer.YuvColorMatrix = m;
                            _renderer.YuvColorRange  = r;
                        }
                    }

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
                        case PixelFormat.Uyvy422:
                            Interlocked.Increment(ref _uyvy422Frames);
                            break;
                        default:
                            Interlocked.Increment(ref _otherFrames);
                            break;
                    }

                    _renderer!.UploadAndDraw(frame.Value);
                    if (_presentationClockOverride == null)
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
                    Log.LogError(ex, "Render-loop exception (count={Count})", ec);
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

        Log.LogInformation("Disposing SDL3VideoOutput: presented={Presented}, black={Black}, renderExceptions={RenderExceptions}, resizeEvents={ResizeEvents}",
            Interlocked.Read(ref _presentedFrames), Interlocked.Read(ref _blackFrames),
            Interlocked.Read(ref _renderExceptions), Interlocked.Read(ref _resizeEvents));

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

        lock (_cloneLock)
        {
            for (int i = 0; i < _clones.Length; i++)
                _clones[i].Dispose();
            _clones = [];
        }

        _mixer?.Dispose();
        _clock?.Dispose();
        _cts?.Dispose();

        // Release SDL only when the last instance has been disposed.
        if (_sdlInitOwned)
            ReleaseSdlVideo();
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

    public SDL3VideoCloneSink CreateCloneSink(string? title = null, int? width = null, int? height = null)
    {
        if (_window == nint.Zero)
            throw new InvalidOperationException("Call Open() before creating clone sinks.");

        var clone = new SDL3VideoCloneSink(
            _outputFormat,
            title: title,
            width: width ?? Math.Max(1, _outputFormat.Width),
            height: height ?? Math.Max(1, _outputFormat.Height));

        lock (_cloneLock)
        {
            var old = _clones;
            var neo = new SDL3VideoCloneSink[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = clone;
            _clones = neo;
        }

        return clone;
    }

    internal static void AcquireSdlVideo()
    {
        if (Interlocked.Increment(ref _sdlRefCount) != 1)
            return;

        if (SDL.Init(SDL.InitFlags.Video))
            return;

        Interlocked.Decrement(ref _sdlRefCount);
        var err = SDL.GetError();
        throw new InvalidOperationException($"SDL_Init failed: {err}");
    }

    internal static void ReleaseSdlVideo()
    {
        if (Interlocked.Decrement(ref _sdlRefCount) == 0)
            SDL.Quit();
    }
}

