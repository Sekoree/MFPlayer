using Microsoft.Extensions.Logging;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;
using S.Media.Core;
using SDL = global::SDL3.SDL;

namespace S.Media.SDL3;

/// <summary>
/// Controls the swap-interval (vsync) behavior of the SDL3 OpenGL render loop.
/// </summary>
public enum VsyncMode
{
    /// <summary>VSync ON — swap is blocked until the next vertical blanking interval. Best for file playback.</summary>
    On = 1,

    /// <summary>VSync OFF — immediate swap, no blocking. Lowest latency, may tear. Best for live monitoring.</summary>
    Off = 0,

    /// <summary>
    /// Adaptive VSync — behaves like <see cref="On"/> when frames arrive on time, but swaps immediately
    /// when a frame is late. Not all drivers support this; falls back to <see cref="Off"/> automatically.
    /// Recommended for live NDI monitoring.
    /// </summary>
    Adaptive = -1
}

/// <summary>
/// SDL3 + OpenGL backed video output.
/// Creates an SDL3 window with an OpenGL 3.3 core-profile context and runs a
/// vsync-driven render loop on a dedicated thread.
/// Analogous to <c>PortAudioOutput</c> for audio.
/// </summary>
public class SDL3VideoEndpoint : IPullVideoEndpoint, IClockCapableEndpoint, IVideoColorMatrixReceiver
{
    private static readonly ILogger Log = SDL3VideoLogging.GetLogger(nameof(SDL3VideoEndpoint));

    public readonly record struct DiagnosticsSnapshot(
        long LoopIterations,
        long PresentedFrames,
        long UniqueFrames,
        long DroppedFrames,
        long TextureUploads,
        long TextureReuseDraws,
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
    private VideoPtsClock? _clock;
    private volatile IMediaClock? _presentationClockOverride;
    private long _presentationClockOriginTicks;
    private int _hasPresentationClockOrigin;
    // HUD-only drift baseline for cross-domain clock/PTS pairs (e.g. PortAudio
    // clock vs absolute NDI PTS). Render-loop reads/writes; reset from public
    // API paths uses Interlocked/Volatile for safe publication.
    private long _hudDriftClockOriginTicks;
    private long _hudDriftPtsOriginTicks;
    private int _hasHudDriftOrigin;
    private VideoFormat  _outputFormat;
    private VideoFormat  _inputFormat;
    private readonly Lock _inputFormatLock = new();

    // ── Render thread ─────────────────────────────────────────────────────

    private Thread?                  _renderThread;
    private CancellationTokenSource? _cts;
    private volatile bool            _isRunning;
    private volatile bool            _closeRequested;
    private bool                     _disposed;
    private VsyncMode                _vsyncMode = VsyncMode.On;
    private long                     _loopIterations;
    private long                     _presentedFrames;
    // Distinct source frames presented (counts UploadAndDraw calls, not vsync
    // redraws of the same frame). Divided by elapsed time this is the real
    // content frame rate; _presentedFrames includes re-presentations and
    // therefore tracks the display refresh rate.
    private long                     _uniqueFrames;
    // Frames dropped by the render-loop catch-up path (stale frames skipped
    // because a newer frame was available and the presentation clock had
    // already moved past them by more than _catchupLagThreshold).
    private long                     _droppedFrames;
    private long                     _textureUploads;
    private long                     _textureReuseDraws;
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
    private long                     _renderPacingIntervalTicks;
    private long                     _nextRenderDueTicks;
    private volatile int             _yuvColorRange = (int)YuvColorRange.Auto;
    private volatile int             _yuvColorMatrix = (int)YuvColorMatrix.Auto;

    // §3.34 / S6 — pending-change flag for YUV hints. Setters flip this to
    // 1 instead of calling ApplyResolvedYuvHints directly (which would
    // touch `_renderer` off the render thread); the render loop consumes
    // it at the top of each tick under the GL context.
    private int                      _yuvHintsDirty;
    // Set to true when the user explicitly overrides the YUV hints; suppresses auto-detect.
    private volatile bool            _hasYuvHintsOverride;
    private volatile int             _scalingFilter = (int)ScalingFilter.Bicubic;
    // Per-frame auto-hint tracking (render thread only — no lock needed).
    private YuvColorMatrix           _lastAutoMatrix = YuvColorMatrix.Auto;
    private YuvColorRange            _lastAutoRange  = YuvColorRange.Auto;

    // HUD FPS measurement (render thread only)
    private long   _hudLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private int    _hudFrameCount;
    private double _hudFps;
    // Unique-frame FPS (content rate, not display-refresh rate).
    private long   _hudLastUniqueTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private long   _hudLastUniqueFrames;
    private double _hudUniqueFps;

    // ── Catch-up / texture-reuse (render thread only) ─────────────────────
    // Mirror of the Avalonia output's catch-up + texture-reuse logic. When
    // the render loop falls behind the presentation clock by more than
    // _catchupLagThreshold we pull additional frames from the ring (up to
    // _maxCatchupPullsPerRender) and drop the stale ones instead of happily
    // presenting an old frame per vsync — which would cause the SDL3 output
    // to visibly lag behind the audio/NDI path after any stall.
    private TimeSpan _catchupLagThreshold = TimeSpan.FromMilliseconds(45);
    private int      _maxCatchupPullsPerRender = 6;

    // Identity of the last frame uploaded to the GPU. When the pull callback
    // returns the same frame (empty ring → re-presented last frame), we skip
    // the glTexSubImage2D upload entirely and just redraw the existing GPU
    // textures. Avoids ~2 GB/s of PCIe traffic on 4K @ 60 Hz and removes the
    // driver-implicit texture sync that was contributing to frame stalls.
    private bool                  _hasUploadedFrame;
    private int                   _lastUploadedWidth;
    private int                   _lastUploadedHeight;
    private TimeSpan              _lastUploadedPts;
    private ReadOnlyMemory<byte>  _lastUploadedData;
    // §3.33 / S3, S12 — identity key for texture-reuse. `ReadOnlyMemory<byte>.Equals`
    // is structural (array ref + offset + length), so after an ArrayPool rental is
    // returned and a coincidentally-identical array is re-rented the old key can
    // falsely match and skip the upload → stale texture. Keying on the MemoryOwner
    // reference (identity-compared via ReferenceEquals) plus Pts/W/H rules this out.
    private System.IDisposable? _lastUploadedMemoryOwner;

    // ── SDL init ref-counting ─────────────────────────────────────────────
    // Multiple SDL3VideoEndpoint instances may coexist; only the last Dispose
    // call should invoke SDL.Quit.
    private static int _sdlRefCount;
    private bool _sdlInitOwned;

    private readonly Lock _cloneLock = new();
    private SDL3VideoCloneEndpoint[] _clones = [];
    private SDL3ProcessEventPump.Subscription? _eventSubscription;

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
            _hasYuvHintsOverride = range != YuvColorRange.Auto || matrix != YuvColorMatrix.Auto;
            _yuvColorRange  = (int)range;
            _yuvColorMatrix = (int)matrix;
            _lastAutoRange = YuvColorRange.Auto;
            _lastAutoMatrix = YuvColorMatrix.Auto;
            // §3.34 / S6 — defer to the render loop. ApplyResolvedYuvHints
            // writes to `_renderer`'s YUV fields; calling it from the
            // caller's thread is a race against the render thread's
            // per-frame apply. The render loop checks `_yuvHintsDirty` at
            // the top of each tick under the GL context.
            Interlocked.Exchange(ref _yuvHintsDirty, 1);
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

    /// <summary>
    /// §5.3 — receive color-matrix hint from the source channel at route-creation
    /// time. Preserves any explicit value the caller already set: <see cref="YuvColorMatrix.Auto"/>
    /// / <see cref="YuvColorRange.Auto"/> hints are ignored, and we only overwrite
    /// the current value when it is itself <c>Auto</c>. Thread-safe — <see cref="YuvConfig"/>
    /// publishes via <see cref="Interlocked.Exchange(ref int, int)"/> so the render
    /// thread picks up the new value on the next frame.
    /// </summary>
    public void ApplyColorMatrixHint(YuvColorMatrix matrix, YuvColorRange range)
    {
        var currentMatrix = (YuvColorMatrix)_yuvColorMatrix;
        var currentRange  = (YuvColorRange)_yuvColorRange;
        var newMatrix = currentMatrix == YuvColorMatrix.Auto && matrix != YuvColorMatrix.Auto ? matrix : currentMatrix;
        var newRange  = currentRange  == YuvColorRange.Auto  && range  != YuvColorRange.Auto  ? range  : currentRange;
        if (newMatrix != currentMatrix || newRange != currentRange)
            YuvConfig = new(newRange, newMatrix);
    }

    /// <summary>
    /// Scaling filter applied when the video does not fill the window at 1:1 pixel mapping.
    /// <list type="bullet">
    ///   <item><see cref="ScalingFilter.Bilinear"/> (default) — GPU hardware bilinear; fast, smooth.</item>
    ///   <item><see cref="ScalingFilter.Bicubic"/> — Catmull-Rom bicubic via an intermediate FBO; sharper edges.</item>
    ///   <item><see cref="ScalingFilter.Nearest"/> — pixel-exact; useful for 1:1 monitoring.</item>
    /// </list>
    /// Thread-safe: the render thread picks up the new value on the next frame.
    /// </summary>
    public ScalingFilter ScalingFilter
    {
        get => (ScalingFilter)_scalingFilter;
        set
        {
            _scalingFilter = (int)value;
            if (_renderer != null)
                _renderer.ScalingFilter = value;
        }
    }

    /// <summary>
    /// Controls the swap-interval (vsync) behavior.
    /// Set this <b>before</b> calling <see cref="Open"/>.
    /// <list type="bullet">
    ///   <item><see cref="VsyncMode.On"/> — classic VSync (default for file playback).</item>
    ///   <item><see cref="VsyncMode.Adaptive"/> — adaptive VSync; recommended for live NDI monitoring.</item>
    ///   <item><see cref="VsyncMode.Off"/> — no VSync; lowest latency, may tear.</item>
    /// </list>
    /// </summary>
    public VsyncMode VsyncMode
    {
        get => _vsyncMode;
        set => _vsyncMode = value;
    }

    /// <summary>
    /// When <see langword="true"/>, the render loop is additionally paced to the
    /// current input FPS hint instead of redrawing every display refresh tick.
    /// This reduces CPU/GPU work for low-FPS sources (for example 24/25/30 FPS
    /// video on a 60 Hz monitor). Default is <see langword="false"/>.
    /// </summary>
    public bool LimitRenderToInputFps { get; set; }

    /// <summary>
    /// Updates the source-format hint used by HUD diagnostics.
    /// Useful for live sources whose format/FPS can change at runtime.
    /// </summary>
    public void SetInputFormatHint(VideoFormat format)
    {
        lock (_inputFormatLock)
            _inputFormat = format;
    }

    /// <summary>
    /// When the presentation clock is more than this threshold ahead of the
    /// pulled frame's PTS, the render loop drains additional stale frames
    /// from the ring (up to <see cref="MaxCatchupPullsPerRender"/>) to catch
    /// back up. Defaults to 45 ms.
    /// </summary>
    public TimeSpan CatchupLagThreshold
    {
        get => _catchupLagThreshold;
        set => _catchupLagThreshold = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
    }

    /// <summary>
    /// Maximum number of additional pull-callback invocations per render-loop
    /// iteration when catching up from a stall. Each successful extra pull
    /// discards the previous (stale) frame and counts as one dropped frame.
    /// Defaults to 6.
    /// </summary>
    public int MaxCatchupPullsPerRender
    {
        get => _maxCatchupPullsPerRender;
        set => _maxCatchupPullsPerRender = value < 0 ? 0 : value;
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        LoopIterations: Interlocked.Read(ref _loopIterations),
        PresentedFrames: Interlocked.Read(ref _presentedFrames),
        UniqueFrames: Interlocked.Read(ref _uniqueFrames),
        DroppedFrames: Interlocked.Read(ref _droppedFrames),
        TextureUploads: Interlocked.Read(ref _textureUploads),
        TextureReuseDraws: Interlocked.Read(ref _textureReuseDraws),
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

    // ── Public properties ────────────────────────────────────────────────────

    /// <summary>Format describing the current output surface.</summary>
    public VideoFormat OutputFormat => _outputFormat;

    /// <summary>
    /// When <see langword="true"/>, a diagnostic HUD overlay is rendered on top of the video
    /// showing resolution, pixel format, FPS, presented/black frame counts, and
    /// clock diagnostics (position, source, and frame drift).
    /// Thread-safe: can be toggled from any thread at any time.
    /// </summary>
    public bool ShowHud { get; set; }

    /// <inheritdoc/>
    public string Name => "SDL3VideoEndpoint";

    /// <summary>Clock driven by video PTS.</summary>
    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    // ── IPullVideoEndpoint ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public IVideoPresentCallback? PresentCallback { get; set; }

    // ── IVideoEndpoint (push — unused for pull endpoints) ───────────────────

    /// <inheritdoc/>
    void IVideoEndpoint.ReceiveFrame(in VideoFrame frame)
    {
        // Pull endpoints do not use push delivery; no-op.
    }

    // ── IClockCapableEndpoint ───────────────────────────────────────────────

    IMediaClock IClockCapableEndpoint.Clock => Clock;

    /// <summary>
    /// Overrides the render-loop presentation clock.
    /// Set this to an audio hardware clock (for example <see cref="S.Media.PortAudio.PortAudioOutput.Clock"/>)
    /// to keep video pacing aligned with audio.
    /// </summary>
    public void OverridePresentationClock(IMediaClock? clock)
    {
        _presentationClockOverride = clock;
        // §3.40b / S5 — clear ticks *first*, then flag, so observers can never see
        // `hasOrigin==0 && ticks==<stale>` on a weakly-ordered CPU.
        Interlocked.Exchange(ref _presentationClockOriginTicks, 0);
        Interlocked.Exchange(ref _hasPresentationClockOrigin, 0);
        Interlocked.Exchange(ref _hudDriftClockOriginTicks, 0);
        Interlocked.Exchange(ref _hudDriftPtsOriginTicks, 0);
        Interlocked.Exchange(ref _hasHudDriftOrigin, 0);
    }

    /// <summary>
    /// Captures the current external presentation clock position as the video timeline
    /// origin. Call this <b>after</b> the audio output is started and pre-buffering is
    /// complete, but <b>before</b> calling <see cref="StartAsync"/>. This gives a
    /// deterministic origin instead of latching it non-deterministically on the first
    /// render-loop vsync tick (OPT-10).
    /// <para>No-op if no external presentation clock has been set.</para>
    /// </summary>
    public void ResetClockOrigin()
    {
        var clock = _presentationClockOverride;
        if (clock == null) return;

        long ticks = clock.Position.Ticks;
        if (ticks < 0) ticks = 0;
        // §3.40b / S5 — set ticks first, then flag (Interlocked provides a
        // sequentially-consistent barrier on both slots so no torn read can
        // observe `hasOrigin==1 && ticks==<stale>`).
        Interlocked.Exchange(ref _presentationClockOriginTicks, ticks);
        Interlocked.Exchange(ref _hasPresentationClockOrigin, 1);
        Interlocked.Exchange(ref _hudDriftClockOriginTicks, 0);
        Interlocked.Exchange(ref _hudDriftPtsOriginTicks, 0);
        Interlocked.Exchange(ref _hasHudDriftOrigin, 0);
        Log.LogInformation("Presentation clock origin set deterministically: {OriginMs:F2} ms",
            TimeSpan.FromTicks(ticks).TotalMilliseconds);
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
        // HighPixelDensity ensures the GL backbuffer matches the physical pixel
        // resolution on HiDPI / fractionally-scaled displays. Without it the
        // compositor upscales the logical-resolution output, causing blur that
        // no shader improvement can fix.
        _window = SDL.CreateWindow(
            title, width, height,
            SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity);

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

        // Vsync — configurable for latency optimization (OPT-8).
        // Adaptive (-1) falls back to Off (0) if the driver doesn't support it.
        if (!SDL.GLSetSwapInterval((int)_vsyncMode) && _vsyncMode == VsyncMode.Adaptive)
        {
            Log.LogWarning("Adaptive VSync not supported by driver; falling back to VSync Off");
            SDL.GLSetSwapInterval((int)VsyncMode.Off);
        }
        Log.LogInformation("VSync mode: {VsyncMode} (swap interval = {Interval})",
            _vsyncMode, (int)_vsyncMode);

        // ── GL renderer (context is current on this thread) ───────────────
        _renderer = new GLRenderer();
        ApplyResolvedYuvHints(_outputFormat.Width, _outputFormat.Height);
        _renderer.ScalingFilter  = (ScalingFilter)_scalingFilter;

        // Query actual physical pixel size — on HiDPI/scaled displays the pixel
        // dimensions differ from the logical window size and the GL viewport must
        // be set in physical pixels, otherwise the image is rendered at too low a
        // resolution and the compositor upscales it, producing a blurry result.
        SDL.GetWindowSizeInPixels(_window, out int pixelW, out int pixelH);
        _renderer.Initialise(pixelW, pixelH);

        // Log DPI diagnostic so mismatches are immediately visible.
        SDL.GetWindowSize(_window, out int logicalW, out int logicalH);
        if (logicalW != pixelW || logicalH != pixelH)
            Log.LogInformation("HiDPI active: logical={LogicalW}x{LogicalH}, physical={PixelW}x{PixelH}, scale={ScaleX:F2}x{ScaleY:F2}",
                logicalW, logicalH, pixelW, pixelH,
                (double)pixelW / logicalW, (double)pixelH / logicalH);
        else
            Log.LogInformation("Window pixel size: {PixelW}x{PixelH} (no DPI scaling detected)", pixelW, pixelH);

        // Release the GL context from the calling thread so the render thread
        // can claim it via GLMakeCurrent.
        SDL.GLMakeCurrent(_window, nint.Zero);

        _eventSubscription = SDL3ProcessEventPump.RegisterWindow(_window);

        // ── Pipeline objects ──────────────────────────────────────────────
        var leaderPixelFormat = LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat(
            format,
            supportsNv12: true,
            supportsYuv420p: true,
            supportsYuv422p10: true,
            supportsUyvy422: true,
            fallback: PixelFormat.Bgra32);
        _outputFormat = format with { PixelFormat = leaderPixelFormat };
        lock (_inputFormatLock)
            _inputFormat = format;


        _clock = new VideoPtsClock(
            frameRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);

        // Inform the renderer of the video dimensions so resize events produce
        // a correctly letterboxed/pillarboxed viewport from the start.
        if (_outputFormat.Width > 0 && _outputFormat.Height > 0)
            _renderer.SetVideoSize(_outputFormat.Width, _outputFormat.Height);

        Log.LogInformation("Opened SDL3VideoEndpoint: '{Title}' {Width}x{Height} px={PixelFormat}, fps={FrameRate}",
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
        // §3.40b — ticks first then flag; see OverridePresentationClock.
        Interlocked.Exchange(ref _presentationClockOriginTicks, 0);
        Interlocked.Exchange(ref _hasPresentationClockOrigin, 0);
        Interlocked.Exchange(ref _hudDriftClockOriginTicks, 0);
        Interlocked.Exchange(ref _hudDriftPtsOriginTicks, 0);
        Interlocked.Exchange(ref _hasHudDriftOrigin, 0);

        _renderThread = new Thread(RenderLoop)
        {
            Name         = "SDL3VideoEndpoint.Render",
            IsBackground = true
        };
        Volatile.Write(ref _renderPacingIntervalTicks, 0);
        Volatile.Write(ref _nextRenderDueTicks, 0);
        _renderThread.Start();

        _clock!.Start();
        _isRunning = true;
        Log.LogInformation("SDL3VideoEndpoint started");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;

        Log.LogInformation("Stopping SDL3VideoEndpoint");
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
            // §3.40c / S7 — also signal close so Dispose completes promptly
            // and any `WindowClosed` subscribers get notified of the failure.
            _closeRequested = true;
            RaiseWindowClosedAsync();
            return;
        }

        var token = _cts!.Token;

        while (!token.IsCancellationRequested && !_closeRequested)
        {
            Interlocked.Increment(ref _loopIterations);

            try
            {
                // §3.34 / S6 — consume any pending YUV-hint change under the
                // GL context. Setters flip `_yuvHintsDirty` instead of
                // calling ApplyResolvedYuvHints directly.
                if (Interlocked.Exchange(ref _yuvHintsDirty, 0) == 1)
                {
                    int w = _outputFormat.Width > 0 ? _outputFormat.Width : _inputFormat.Width;
                    int h = _outputFormat.Height > 0 ? _outputFormat.Height : _inputFormat.Height;
                    ApplyResolvedYuvHints(w, h);
                }

                // ── Process-wide SDL event dispatch (per-window queue) ───
                var eventSubscription = _eventSubscription;
                while (eventSubscription is not null && eventSubscription.TryDequeue(out var evt))
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
                            SDL.GetWindowSizeInPixels(_window, out int w, out int h);
                            _renderer!.SetViewport(w, h);
                            Interlocked.Increment(ref _resizeEvents);
                            break;

                        case SDL.EventType.KeyDown:
                            if (evt.Key.Key == SDL.Keycode.H)
                                ShowHud = !ShowHud;
                            break;
                    }

                    if (_closeRequested) break;
                }

                if (_closeRequested) break;

                if (!WaitForRenderCadence(token))
                    continue;

                // ── Present frame ─────────────────────────────────────────
                VideoFrame? frame = null;
                TimeSpan clockPosition = TimeSpan.Zero;
                TimeSpan hudClockPosition = TimeSpan.Zero;
                TimeSpan hudDrift = TimeSpan.Zero;
                string hudClockName = ((_presentationClockOverride ?? _clock)?.GetType().Name) ?? "n/a";

                var presentCb = PresentCallback;
                if (presentCb is not null)
                {
                    var externalClock = _presentationClockOverride;
                    var presentationClock = externalClock ?? _clock;
                    if (presentationClock == null)
                        throw new InvalidOperationException("Presentation clock is not available.");

                    hudClockName = externalClock is null
                        ? presentationClock.GetType().Name
                        : $"{presentationClock.GetType().Name} (override)";

                    clockPosition = presentationClock.Position;
                    hudClockPosition = clockPosition;

                    // HUD clock display: for override clocks we show a relative timeline
                    // from first observation so absolute wall-clock style domains do not
                    // produce unreadable values.
                    if (externalClock != null)
                    {
                        long rawTicks = clockPosition.Ticks;
                        if (rawTicks < 0) rawTicks = 0;
                        if (Interlocked.CompareExchange(ref _hasPresentationClockOrigin, 1, 0) == 0)
                            Volatile.Write(ref _presentationClockOriginTicks, rawTicks);
                        long originTicks = Volatile.Read(ref _presentationClockOriginTicks);
                        long relTicks = rawTicks - originTicks;
                        if (relTicks < 0) relTicks = 0;
                        hudClockPosition = TimeSpan.FromTicks(relTicks);
                    }
                    // No local origin subtraction. The router's per-route drift tracker
                    // (PtsDriftTracker) seeds its own origin from the first observed
                    // (pts, clock) pair, so clock and frame PTS can live in any matched
                    // or mismatched domain and the tracker reduces both to relative
                    // deltas internally. A previous revision here subtracted an
                    // "SDL3 presentation origin" from the clock only, which left
                    // clock-delta comparing against absolute PTS and stuck the gate on
                    // NDI timestamps.

                    if (presentCb.TryPresentNext(clockPosition, out VideoFrame cbFrame))
                        frame = cbFrame;

                    // ── Catch-up: if this frame is already stale, drop it and
                    // pull newer ones from the ring until we're back in sync
                    // (or run out of budget / newer frames). This mirrors the
                    // AvaloniaOpenGlVideoEndpoint catch-up loop and is what makes
                    // the router push-path (NDI) look smooth; without it the
                    // SDL3 pull-path drains at exactly one frame per vsync and
                    // can never recover from a stall.
                    if (frame.HasValue)
                    {
                        var vf = frame.Value;
                        for (int i = 0; i < _maxCatchupPullsPerRender; i++)
                        {
                            if (vf.Pts + _catchupLagThreshold >= clockPosition)
                                break;

                            if (!presentCb.TryPresentNext(clockPosition, out VideoFrame nf))
                                break;

                            // Ring is empty → callback re-presented the same frame.
                            if (nf.Pts == vf.Pts &&
                                nf.Width == vf.Width &&
                                nf.Height == vf.Height &&
                                nf.Data.Equals(vf.Data))
                                break;

                            vf = nf;
                            Interlocked.Increment(ref _droppedFrames);
                        }
                        frame = vf;
                    }
                }

                // HUD drift reads against the clock's raw position — same domain
                // as VideoFrame.Pts when the clock is correctly paired (NDIClock
                // vs NDI timestamps, VideoPtsClock vs FFmpeg PTS). When the user
                // deliberately picks a mismatched clock the number stays large,
                // which is the honest answer.
                if (!frame.HasValue && _hasUploadedFrame)
                {
                    hudDrift = _presentationClockOverride != null
                        ? ComputeHudRelativeDrift(clockPosition, _lastUploadedPts)
                        : clockPosition - _lastUploadedPts;
                }

                if (frame.HasValue)
                {
                    var vf = frame.Value;
                    lock (_inputFormatLock)
                    {
                        int inputFpsNum = _inputFormat.FrameRateNumerator > 0 ? _inputFormat.FrameRateNumerator : 30000;
                        int inputFpsDen = _inputFormat.FrameRateDenominator > 0 ? _inputFormat.FrameRateDenominator : 1001;
                        _inputFormat = new VideoFormat(vf.Width, vf.Height, vf.PixelFormat, inputFpsNum, inputFpsDen);
                    }
                    hudDrift = _presentationClockOverride != null
                        ? ComputeHudRelativeDrift(clockPosition, vf.Pts)
                        : clockPosition - vf.Pts;
                    ApplyResolvedYuvHints(vf.Width, vf.Height);
                    _renderer!.SetVideoSize(vf.Width, vf.Height);


                    switch (vf.PixelFormat)
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

                    // Texture-reuse: if the pull callback returned the same frame
                    // (empty ring → re-present last) skip the glTexSubImage2D
                    // upload entirely and just redraw the GPU-resident textures.
                    // §3.33 / S3, S12 — identity-based match via MemoryOwner reference.
                    bool sameAsUploaded = _hasUploadedFrame &&
                                          vf.Width == _lastUploadedWidth &&
                                          vf.Height == _lastUploadedHeight &&
                                          vf.Pts == _lastUploadedPts &&
                                          ReferenceEquals(vf.MemoryOwner, _lastUploadedMemoryOwner);

                    if (sameAsUploaded)
                    {
                        _renderer!.DrawLastFrame();
                        Interlocked.Increment(ref _textureReuseDraws);
                    }
                    else
                    {
                        _renderer!.UploadAndDraw(vf);
                        _hasUploadedFrame        = true;
                        _lastUploadedWidth       = vf.Width;
                        _lastUploadedHeight      = vf.Height;
                        _lastUploadedPts         = vf.Pts;
                        _lastUploadedData        = vf.Data;
                        _lastUploadedMemoryOwner = vf.MemoryOwner;
                        Interlocked.Increment(ref _textureUploads);
                        Interlocked.Increment(ref _uniqueFrames);
                    }

                    if (_presentationClockOverride == null)
                        _clock!.UpdateFromFrame(vf.Pts);
                    Interlocked.Increment(ref _presentedFrames);
                }
                else
                {
                    _renderer!.DrawBlack();
                    Interlocked.Increment(ref _blackFrames);
                }

                // ── Swap (paced by vsync) ─────────────────────────────────
                // ── HUD overlay ───────────────────────────────────────────
                if (ShowHud)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double elapsed = (double)(now - _hudLastTimestamp) / System.Diagnostics.Stopwatch.Frequency;
                    _hudFrameCount++;
                    if (elapsed >= 1.0)
                    {
                        _hudFps = _hudFrameCount / elapsed;
                        _hudFrameCount = 0;
                        _hudLastTimestamp = now;

                        // Content FPS from unique (uploaded) frames. Reveals when
                        // the display is refreshing at 60 Hz but the actual video
                        // is running at half that because frames are being
                        // re-presented — the exact bug the texture-reuse path
                        // was masking previously.
                        long uniqueNow = Interlocked.Read(ref _uniqueFrames);
                        double uniqueElapsed = (double)(now - _hudLastUniqueTimestamp) / System.Diagnostics.Stopwatch.Frequency;
                        if (uniqueElapsed > 0.0)
                            _hudUniqueFps = (uniqueNow - _hudLastUniqueFrames) / uniqueElapsed;
                        _hudLastUniqueFrames = uniqueNow;
                        _hudLastUniqueTimestamp = now;
                    }

                    double displayFps = _hudFps;
                    if (displayFps < 0 || double.IsNaN(displayFps) || double.IsInfinity(displayFps))
                        displayFps = 0;

                    double contentFps = _hudUniqueFps;
                    if (contentFps < 0 || double.IsNaN(contentFps) || double.IsInfinity(contentFps))
                        contentFps = 0;
                    VideoFormat inputFormatSnapshot;
                    lock (_inputFormatLock)
                        inputFormatSnapshot = _inputFormat;
                    double inputFps = inputFormatSnapshot.FrameRate;
                    int inputWidth = _hasUploadedFrame ? _lastUploadedWidth : inputFormatSnapshot.Width;
                    int inputHeight = _hasUploadedFrame ? _lastUploadedHeight : inputFormatSnapshot.Height;
                    PixelFormat inputPixelFormat = _hasUploadedFrame ? inputFormatSnapshot.PixelFormat : _outputFormat.PixelFormat;

                    var stats = new HudStats
                    {
                        Width = _outputFormat.Width,
                        Height = _outputFormat.Height,
                        PixelFormat = _outputFormat.PixelFormat,
                        // Keep the main FPS line as display refresh cadence.
                        // Content/unique FPS is shown separately to avoid a
                        // perceived "60 -> 30 drop" when source cadence differs.
                        Fps = displayFps,
                        InputWidth = inputWidth,
                        InputHeight = inputHeight,
                        InputFps = inputFps,
                        InputPixelFormat = inputPixelFormat,
                        PresentedFrames = Interlocked.Read(ref _presentedFrames),
                        BlackFrames = Interlocked.Read(ref _blackFrames),
                        DroppedFrames = Interlocked.Read(ref _droppedFrames),
                        ClockPosition = hudClockPosition,
                        ClockName = hudClockName,
                        Drift = hudDrift,
                        ExtraLine1 = contentFps > 0
                            ? $"fps content: {contentFps:F1}  display: {displayFps:F1}"
                            : $"fps content: n/a  display: {displayFps:F1}",
                        ExtraLine2 = $"reuse: {Interlocked.Read(ref _textureReuseDraws)}  uploads: {Interlocked.Read(ref _textureUploads)}",
                    };
                    _renderer!.DrawHud(stats.ToLines());
                }

                SDL.GLSwapWindow(_window);
                Interlocked.Increment(ref _swapCalls);
            }
            catch (Exception ex)
            {
                long ec = Interlocked.Increment(ref _renderExceptions);
                // §3.40d / S10 — tag the concrete exception type so log readers
                // can grep for a single failure mode across the rate-limited
                // samples (counts 1/2/3 then every 100th).
                if (ec <= 3 || ec % 100 == 0)
                    Log.LogError(ex, "Render-loop exception [{ExceptionType}] (count={Count})",
                        ex.GetType().Name, ec);
            }
        }

        // Release GL context from this thread.
        SDL.GLMakeCurrent(_window, nint.Zero);

        // Notify the application that the window was closed (if user-initiated).
        if (_closeRequested)
        {
            _isRunning = false;
            _clock?.Stop();
            RaiseWindowClosedAsync();
        }
    }

    /// <summary>
    /// §3.32 / S1 — dispatches the <see cref="WindowClosed"/> event on the
    /// ThreadPool rather than synchronously on the render thread. A common
    /// handler pattern is <c>endpoint.Dispose()</c>; Dispose joins the render
    /// thread with a 3-second timeout, so raising the event on the render
    /// thread would self-deadlock. ThreadPool dispatch breaks the cycle.
    /// Exceptions raised by handlers are logged so a buggy handler cannot
    /// take down the pool worker.
    /// </summary>
    private void RaiseWindowClosedAsync()
    {
        var handler = WindowClosed;
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (h, log) = ((Action handler, ILogger log))state!;
            try { h(); }
            catch (Exception ex) { log.LogError(ex, "WindowClosed handler threw"); }
        }, (handler, Log));
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Log.LogInformation("Disposing SDL3VideoEndpoint: presented={Presented}, unique={Unique}, dropped={Dropped}, textureReuse={Reuse}, black={Black}, renderExceptions={RenderExceptions}, resizeEvents={ResizeEvents}",
            Interlocked.Read(ref _presentedFrames), Interlocked.Read(ref _uniqueFrames),
            Interlocked.Read(ref _droppedFrames), Interlocked.Read(ref _textureReuseDraws),
            Interlocked.Read(ref _blackFrames),
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
            // §3.40e / S11 — MakeCurrent + renderer.Dispose + DestroyContext
            // can all fault if the context was already invalidated (user-closed
            // window, driver reset). We guard each one separately so a failure
            // in the middle of the cleanup sequence still lets the rest run.
            try { SDL.GLMakeCurrent(_window, _glContext); }
            catch (Exception ex) { Log.LogWarning(ex, "SDL.GLMakeCurrent threw during Dispose"); }
            try { _renderer?.Dispose(); }
            catch (Exception ex) { Log.LogWarning(ex, "Renderer.Dispose threw (context may be gone)"); }
            try { SDL.GLDestroyContext(_glContext); }
            catch (Exception ex) { Log.LogWarning(ex, "SDL.GLDestroyContext threw during Dispose"); }
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

        _eventSubscription?.Dispose();
        _eventSubscription = null;

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

    private bool WaitForRenderCadence(CancellationToken token)
    {
        if (!LimitRenderToInputFps || !TryGetInputFrameIntervalTicks(out long intervalTicks))
        {
            Volatile.Write(ref _renderPacingIntervalTicks, 0);
            Volatile.Write(ref _nextRenderDueTicks, 0);
            return true;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long previousInterval = Volatile.Read(ref _renderPacingIntervalTicks);
        if (previousInterval != intervalTicks)
        {
            Volatile.Write(ref _renderPacingIntervalTicks, intervalTicks);
            // Keep startup responsive (first draw can be immediate), but when
            // switching intervals mid-stream anchor from "now".
            Volatile.Write(ref _nextRenderDueTicks,
                RenderCadenceHelper.InitialDue(now, intervalTicks, immediateFirstTick: !_hasUploadedFrame));
        }

        long due;
        while (true)
        {
            due = Volatile.Read(ref _nextRenderDueTicks);
            if (due <= 0)
            {
                now = System.Diagnostics.Stopwatch.GetTimestamp();
                due = RenderCadenceHelper.InitialDue(now, intervalTicks, immediateFirstTick: !_hasUploadedFrame);
                Volatile.Write(ref _nextRenderDueTicks, due);
            }

            now = System.Diagnostics.Stopwatch.GetTimestamp();
            // SDL swaps may already block on VSync; when we're late, present
            // immediately instead of waiting another full cadence interval.
            due = RenderCadenceHelper.NormalizeDue(
                due, now, intervalTicks, RenderCadenceHelper.LateTickPolicy.PresentImmediately);
            Volatile.Write(ref _nextRenderDueTicks, due);

            now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= due)
                break;

            double remainingMs = RenderCadenceHelper.ComputeRemainingMilliseconds(now, due);
            if (remainingMs >= 2.0)
            {
                // Leave a short tail for sub-ms correction so scheduler
                // quantization doesn't systematically run fast.
                int sleepMs = (int)Math.Min(5.0, Math.Max(1.0, remainingMs - 1.0));
                Thread.Sleep(sleepMs);
            }
            else
            {
                Thread.Yield();
            }

            if (token.IsCancellationRequested || _closeRequested)
                return false;
        }

        long nowAfterPacing = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _nextRenderDueTicks, RenderCadenceHelper.ComputeNextDue(due, nowAfterPacing, intervalTicks));
        return true;
    }

    private bool TryGetInputFrameIntervalTicks(out long intervalTicks)
    {
        VideoFormat format;
        lock (_inputFormatLock)
            format = _inputFormat;

        return RenderCadenceHelper.TryGetFrameIntervalTicks(format.FrameRate, out intervalTicks);
    }

    private TimeSpan ComputeHudRelativeDrift(TimeSpan rawClockPosition, TimeSpan framePts)
    {
        long clockTicks = rawClockPosition.Ticks;
        if (clockTicks < 0) clockTicks = 0;
        long ptsTicks = framePts.Ticks;

        if (Interlocked.CompareExchange(ref _hasHudDriftOrigin, 1, 0) == 0)
        {
            Volatile.Write(ref _hudDriftClockOriginTicks, clockTicks);
            Volatile.Write(ref _hudDriftPtsOriginTicks, ptsTicks);
            return TimeSpan.Zero;
        }

        long relClockTicks = clockTicks - Volatile.Read(ref _hudDriftClockOriginTicks);
        long relPtsTicks = ptsTicks - Volatile.Read(ref _hudDriftPtsOriginTicks);
        return TimeSpan.FromTicks(relClockTicks - relPtsTicks);
    }

    private void ApplyResolvedYuvHints(int width, int height)
    {
        if (_renderer == null)
            return;

        int resolvedWidth = width > 0 ? width : 1280;
        int resolvedHeight = height > 0 ? height : 720;

        var requestedRange = (YuvColorRange)_yuvColorRange;
        var requestedMatrix = (YuvColorMatrix)_yuvColorMatrix;
        var resolvedRange = YuvAutoPolicy.ResolveRange(requestedRange);
        var resolvedMatrix = YuvAutoPolicy.ResolveMatrix(requestedMatrix, resolvedWidth, resolvedHeight);

        if (_hasYuvHintsOverride)
        {
            _renderer.YuvColorRange = resolvedRange;
            _renderer.YuvColorMatrix = resolvedMatrix;
            return;
        }

        if (_lastAutoRange == resolvedRange && _lastAutoMatrix == resolvedMatrix)
            return;

        _renderer.YuvColorRange = resolvedRange;
        _renderer.YuvColorMatrix = resolvedMatrix;
        _lastAutoRange = resolvedRange;
        _lastAutoMatrix = resolvedMatrix;
    }

    /// <summary>
    /// Creates a clone endpoint that can be registered on the router and wired
    /// into a per-source route, mirroring this parent's video output into a
    /// secondary window. See `Doc/Clone-Sinks.md` for the full wiring contract
    /// (§3.40a / S2, S4).
    /// <para>
    /// The clone is a standalone <see cref="IVideoEndpoint"/> — the router
    /// delivers frames to it through the normal
    /// <see cref="IVideoEndpoint.ReceiveFrame(in VideoFrameHandle)"/> path;
    /// this parent does <b>not</b> tee frames internally. The returned clone is
    /// tracked here so a parent <see cref="Dispose"/> cascades disposal as a
    /// safety net; callers with finer control should
    /// <c>RemoveRoute</c> + <c>UnregisterEndpoint</c> + <c>clone.Dispose()</c>
    /// in that order before disposing the parent.
    /// </para>
    /// </summary>
    public SDL3VideoCloneEndpoint CreateCloneSink(string? title = null, int? width = null, int? height = null)
    {
        if (_window == nint.Zero)
            throw new InvalidOperationException("Call Open() before creating clone sinks.");

        var clone = new SDL3VideoCloneEndpoint(
            _outputFormat,
            title: title,
            width: width ?? Math.Max(1, _outputFormat.Width),
            height: height ?? Math.Max(1, _outputFormat.Height));

        lock (_cloneLock)
        {
            var old = _clones;
            var neo = new SDL3VideoCloneEndpoint[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = clone;
            _clones = neo;
        }

        return clone;
    }

    internal static void AcquireSdlVideo()
    {
        // §3.40 / S14 — guarantee the refcount is rolled back even if
        // SDL.Init throws (rather than just returning false). Without the
        // try/finally a managed exception from SDL bindings would leak a
        // refcount slot, making the process effectively "SDL-initialised"
        // for the rest of its lifetime.
        if (Interlocked.Increment(ref _sdlRefCount) != 1)
            return;

        bool initialised = false;
        try
        {
            initialised = SDL.Init(SDL.InitFlags.Video);
            if (initialised) return;

            var err = SDL.GetError();
            throw new InvalidOperationException($"SDL_Init failed: {err}");
        }
        finally
        {
            if (!initialised)
                Interlocked.Decrement(ref _sdlRefCount);
        }
    }

    internal static void ReleaseSdlVideo()
    {
        if (Interlocked.Decrement(ref _sdlRefCount) == 0)
            SDL.Quit();
    }

    // ── §1.4 single-step factory ──────────────────────────────────────────

    /// <summary>
    /// Single-step factory (§1.4 / CH8 / P1): constructs a
    /// <see cref="SDL3VideoEndpoint"/>, opens its window, and returns a
    /// ready-to-register instance whose <see cref="Clock"/> is valid from the
    /// moment the caller receives it. Equivalent to
    /// <c>new SDL3VideoEndpoint()</c> followed by <see cref="Open"/>.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="format">
    /// Preferred output <see cref="VideoFormat"/>. If <see langword="null"/>,
    /// defaults to a <paramref name="width"/>×<paramref name="height"/> BGRA32
    /// surface at 30 fps — the endpoint will auto-negotiate a better pixel
    /// format at route creation time via <see cref="IFormatCapabilities{TFormat}"/>.
    /// </param>
    /// <param name="vsync">Initial <see cref="SDL3.VsyncMode"/>; defaults to <see cref="VsyncMode.On"/>.</param>
    public static SDL3VideoEndpoint ForWindow(
        string       title,
        int          width  = 1280,
        int          height = 720,
        VideoFormat? format = null,
        VsyncMode    vsync  = VsyncMode.On)
    {
        var ep = new SDL3VideoEndpoint { VsyncMode = vsync };
        ep.Open(title, width, height, format ?? VideoFormat.Create(width, height, PixelFormat.Bgra32, 30));
        return ep;
    }
}
