using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.Gpu;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SilkGL = Silk.NET.OpenGL.GL;

namespace S.Media.Present.SDL3;

/// <summary>
/// <see cref="IVideoOutput"/> backed by an SDL3 window + an OpenGL 3.3 Core
/// context. Same dispatch model as <see cref="SDL3VideoOutput"/>
/// (auto-thread or manual <see cref="Pump"/>) but rendering goes through
/// <see cref="YuvVideoRenderer"/>, so the same shader pipeline can be
/// shared with an Avalonia OpenGL surface later on.
/// </summary>
/// <remarks>
/// <para>
/// On Windows, when the negotiated format is <see cref="PixelFormat.Nv12"/>, a default
/// hardware <c>ID3D11Device</c> is created via <see cref="D3D11GlInteropDeviceHost"/> so <see cref="VideoFrame.Win32Nv12"/> frames
/// (D3D11 shared-handle decode path) can upload through <see cref="Nv12Win32SharedHandleGpuUploader"/>,
/// unless <paramref name="borrowD3D11DeviceComPtrForNv12Gl"/> supplies libav's device (same adapter as decoded textures),
/// or <see cref="VideoFormatNegotiator.Connect"/> wires an <see cref="IHardwareD3D11GlInteropSource"/> via <see cref="IVideoOutputD3D11GlBorrowSetup"/>.
/// When <paramref name="createFallbackD3D11InteropDeviceForWin32Nv12"/> is <see langword="false"/>, SDL does not create that helper device;
/// <see cref="YuvVideoRenderer"/> then binds from <see cref="Win32SharedNv12Backing.LibavD3D11DeviceComPtr"/> on the first frame (true zero-host; requires D3D11VA COM pointers on the backing or a negotiated borrow).
/// </para>
/// <para>
/// Pixel-format support matches <see cref="YuvVideoRenderer.SupportedPixelFormats"/>
/// (BGRA/RGBA/RGB24, planar and semi-planar YUV, 10-bit 422, packed 422, P010/P016, …).
/// </para>
/// <para>
/// VSYNC: enabled by default via <c>SDL_GL_SetSwapInterval(1)</c>; pass
/// <c>vsync:false</c> to free-run. The render thread (or the
/// <see cref="Pump"/> caller in manual mode) blocks on
/// <c>SDL_GL_SwapWindow</c> when VSYNC is on. During <see cref="Dispose"/>, if teardown races a pending present,
/// the output forces <c>SDL_GL_SetSwapInterval(0)</c> before the next swap so shutdown is less likely to block on vsync.
/// </para>
/// <para>
/// <b>Texture mirrors (same pixels, extra windows)</b>: call <see cref="CreateTextureMirror"/>, configure it with the same
/// <see cref="VideoFormat"/> as the anchor, then <see cref="RegisterTextureMirror"/>. The anchor performs a single
/// <see cref="YuvVideoRenderer.Upload"/> per frame; mirrors only draw shared GL textures (via <see cref="YuvVideoRenderer.CreateSharedTextureDrawView"/>).
/// The anchor must use <c>ownsThread:false</c> so mirror initialization can safely call <c>SDL_GL_MakeCurrent</c> on the anchor context.
/// NV12 dmabuf / Win32 D3D11 paths cannot be mirrored yet (CPU-plane and other non-interop formats can).
/// </para>
/// </remarks>
public sealed unsafe class SDL3GLVideoOutput : INonBlockingVideoOutput, IVideoOutputD3D11GlBorrowSetup, IVideoOutputQueueControl,
    IVideoOutputPresentDiagnostics, IDisposable
{
    private static readonly PixelFormat[] AcceptedFormats = YuvVideoRenderer.SupportedPixelFormats.ToArray();

    private readonly string _title;
    private readonly int _initialWindowWidth;
    private readonly int _initialWindowHeight;
    private readonly bool _vsync;
    private readonly bool _ownsThread;

    private GlVideoOutputHdrPreference _hdrPreference = GlVideoOutputHdrPreference.FollowFrameHints;
    private GlOutputBitDepth _swapchainBitDepth = GlOutputBitDepth.Eight;
    private static int _swapchainTenBitFallbackLogged;

    private readonly Win32Nv12GlUploadDeviceResolver _win32Nv12Device;

    private VideoFormat _format;
    private bool _configured;
    private volatile bool _disposed;

    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wakeup = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _renderError;

    /// <summary>
    /// Frames waiting for a vblank. This is the seam where the producer's cadence meets the panel's; see
    /// <see cref="PresentFrameQueue"/> for why it needs depth rather than a single slot.
    /// </summary>
    private readonly PresentFrameQueue _presentQueue;

    private long _displayed;
    private long _droppedNew;
    private long _repeated;
    private long _lastPresentedPtsTicks;
    private int _activePresentations;
    private int _hasLastPresentedPts;
    private int _firstPresentLogged;

    /// <summary>A vsynced swap that returns faster than this did not block on a vblank.</summary>
    private static readonly TimeSpan NonBlockingSwapThreshold = TimeSpan.FromMilliseconds(1);

    /// <summary>Consecutive non-blocking swaps before the loop stops trusting the swap to pace it.</summary>
    private const int NonBlockingSwapStreakLimit = 8;

    /// <summary>
    /// Consecutive empty pulls before the loop stops free-running. Long enough to ride through the
    /// normal repeats of a producer that is slightly slower than the display, short enough that a truly
    /// idle window settles back to cheap timed repaints instead of burning a swap every vblank.
    /// </summary>
    private const int EmptyPullsBeforeIdle = 4;

    private int _nonBlockingSwapStreak;

    /// <summary>
    /// Whether the vsynced render loop paces frame promotion by PTS-vs-predicted-vblank (see
    /// <see cref="PresentFramePacer"/>) instead of showing the newest queued frame at every vblank.
    /// Latest-wins alone lets the producer's arrival phase pick the vblank, so 60 fps content on a
    /// 165 Hz panel shows an irregular repeat pattern (4-2 beats, drop+repeat pairs) instead of the
    /// clean 3-3-3-2 rotation. Default on; set <c>MFP_SDL3_PRESENT_PACING=0</c> to restore the old
    /// latest-wins behaviour in the field (same kill-switch pattern as <c>MFP_PORTAUDIO_CALLBACK</c>).
    /// </summary>
    private static readonly bool PresentPacingEnabled =
        Environment.GetEnvironmentVariable("MFP_SDL3_PRESENT_PACING") != "0";

    /// <summary>Cadence decisions for the vsynced render loop. Touched only by the presenting thread.</summary>
    private readonly PresentFramePacer _pacer = new();

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> of the last swap return that actually blocked on a vblank,
    /// or 0 when the previous swap did not block (the interval to the next blocking swap would then
    /// span an unknown number of refreshes, so the measurement chain is broken on purpose).
    /// Presenting thread only.
    /// </summary>
    private long _lastBlockingSwapReturn;

    /// <summary>Manual-mode analogue of the render loop's empty-pull streak, for repeat accounting in <see cref="Pump"/>.</summary>
    private int _pumpEmptyPulls;

    /// <summary>Converts a <see cref="Stopwatch"/> reading to a monotonic <see cref="TimeSpan"/> for the pacer.</summary>
    private static TimeSpan StopwatchToTimeSpan(long stopwatchTicks) =>
        TimeSpan.FromTicks((long)(stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    /// <summary>
    /// Smoothed submit-to-swap-return latency, in <see cref="Stopwatch"/> ticks. Written only by the presenting
    /// thread, read by anyone. An exponential average rather than a lifetime one so it follows a window
    /// moved to a different panel instead of averaging the two forever.
    /// </summary>
    private long _presentLatencyTicks;

    /// <summary>EMA weight: the new sample counts for 1/N. ~50 frames to converge, so under a second at 60 Hz.</summary>
    private const int PresentLatencySmoothing = 16;

    private readonly ConcurrentQueue<Action> _renderThreadActions = new();
    private volatile bool _canIdleRepaint;

    private nint _window;
    private nint _glContext;
    private SilkGL? _gl;
    private YuvVideoRenderer? _renderer;
    private int _viewportWidth;
    private int _viewportHeight;
    private readonly object _windowConstraintLock = new();
    private readonly object _windowPlacementLock = new();
    private Action? _pendingWindowPlacement;
    private bool _windowAspectLocked;
    private bool _windowResolutionLocked;
    private float _windowAspectRatio;

    /// <summary>Set after <see cref="InitGraphics"/> completes; controls whether <see cref="ForceDisposeMirrorFromAnchor"/> must release the SDL runtime.</summary>
    private bool _graphicsInitialized;

    /// <summary>When non-null, this output is a texture mirror: no <see cref="Submit"/>; GL shares plane textures with the anchor.</summary>
    private readonly SDL3GLVideoOutput? _textureMirrorAnchor;

    private readonly List<SDL3GLVideoOutput> _registeredTextureMirrors = new();
    private readonly object _textureMirrorLock = new();

    public VideoFormat Format
    {
        get
        {
            if (!_configured)
                throw new InvalidOperationException("SDL3GLVideoOutput.Configure has not been called yet");
            return _format;
        }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => AcceptedFormats;
    public bool OwnsThread => _ownsThread;

    /// <summary>The GL viewport (drawable pixels) the renderer currently draws into - diagnostic, for
    /// comparing against the source frame size to reason about scaling quality.</summary>
    public (int Width, int Height) ViewportPixelSize => (_viewportWidth, _viewportHeight);
    public long DisplayedCount => Volatile.Read(ref _displayed);

    /// <summary>
    /// Frames superseded by a newer queued frame at a vblank, or evicted when the queue was full. Expect
    /// a slow, steady count between unequal rates; a burst indicates a presenter/upstream stall.
    /// </summary>
    public long DroppedNewer => _presentQueue.DroppedOldest + Volatile.Read(ref _droppedNew);

    /// <summary>
    /// Vblanks that re-presented the previous frame because no new one was queued - the mirror image of
    /// <see cref="DroppedNewer"/>, and what a producer slightly slower than the display looks like.
    /// Counted only while content is flowing, not while the window sits idle.
    /// </summary>
    public long RepeatedFrames => Volatile.Read(ref _repeated);

    /// <summary>Frames currently queued for a vblank (0..<see cref="PresentQueueCapacity"/>).</summary>
    public int QueuedFrameCount => _presentQueue.Count;

    /// <summary>Maximum present hand-off capacity; the presenter takes the newest queued frame.</summary>
    public int PresentQueueCapacity => _presentQueue.Capacity;

    /// <summary>
    /// Smoothed time from <see cref="Submit"/> until the swap call returns (queue wait + upload + any
    /// vsync block). This is a software-boundary diagnostic, not physical scanout feedback.
    /// </summary>
    public TimeSpan PresentLatency
    {
        get
        {
            var ticks = Volatile.Read(ref _presentLatencyTicks);
            return ticks <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks((long)(ticks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }
    }

    /// <summary>Folds one submit-to-swap-return sample into the smoothed estimate. Presenting thread only.</summary>
    private void RecordPresentLatency(long enqueuedTimestamp)
    {
        if (enqueuedTimestamp <= 0)
            return;

        var sample = Stopwatch.GetTimestamp() - enqueuedTimestamp;
        if (sample < 0)
            return;

        var previous = Volatile.Read(ref _presentLatencyTicks);
        Volatile.Write(
            ref _presentLatencyTicks,
            previous <= 0 ? sample : previous + ((sample - previous) / PresentLatencySmoothing));
    }

    long IVideoOutputPresentDiagnostics.PresentedFrames => DisplayedCount;
    long IVideoOutputPresentDiagnostics.DroppedFrames => DroppedNewer;
    long IVideoOutputPresentDiagnostics.RepeatedFrames => RepeatedFrames;
    int IVideoOutputPresentDiagnostics.QueuedFrames => QueuedFrameCount;
    int IVideoOutputPresentDiagnostics.PresentQueueDepth => PresentQueueCapacity;
    TimeSpan IVideoOutputPresentDiagnostics.PresentLatency => PresentLatency;
    public TimeSpan? LastPresentedPresentationTime =>
        Volatile.Read(ref _hasLastPresentedPts) != 0
            ? TimeSpan.FromTicks(Volatile.Read(ref _lastPresentedPtsTicks))
            : null;

    /// <summary>True when this output was created with <see cref="CreateTextureMirror"/> (frames go to the anchor only).</summary>
    public bool IsTextureMirror => _textureMirrorAnchor is not null;

    /// <summary>How the frame is mapped into the window's viewport. Default <see cref="VideoViewportFit.Contain"/>
    /// preserves the source aspect ratio, letterboxing/pillarboxing on resize (the bars are painted black before
    /// each render) - and is identical to <see cref="VideoViewportFit.Stretch"/> when the window already matches
    /// the frame aspect. Set to <see cref="VideoViewportFit.Stretch"/> to fill the window and ignore aspect.</summary>
    public VideoViewportFit ViewportFit { get; set; } = VideoViewportFit.Contain;

    /// <summary>
    /// Constrains the real desktop window. May be called before <see cref="Configure"/> or while the
    /// output is running; runtime changes are marshalled to the SDL render thread.
    /// </summary>
    public void SetWindowConstraints(bool lockAspectRatio, bool lockResolution, float aspectRatio)
    {
        if (lockAspectRatio && (!float.IsFinite(aspectRatio) || aspectRatio <= 0))
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));

        lock (_windowConstraintLock)
        {
            _windowAspectLocked = lockAspectRatio;
            _windowResolutionLocked = lockResolution;
            _windowAspectRatio = aspectRatio;
        }

        if (_configured)
            InvokeOnRenderThread(ApplyWindowConstraintsCore);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler<(int Width, int Height)>? Resized;

    public SDL3GLVideoOutput(string title = "SDL3 GL Video", int initialWidth = 1280, int initialHeight = 720,
                          bool vsync = true, bool ownsThread = true,
                          GlVideoOutputHdrPreference hdrPreference = GlVideoOutputHdrPreference.FollowFrameHints,
                          GlOutputBitDepth swapchainBitDepth = GlOutputBitDepth.Auto,
                          nint borrowD3D11DeviceComPtrForNv12Gl = 0,
                          bool createFallbackD3D11InteropDeviceForWin32Nv12 = true,
                          SDL3GLVideoOutput? textureMirrorAnchor = null,
                          int presentQueueCapacity = DefaultPresentQueueCapacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (initialWidth <= 0) throw new ArgumentOutOfRangeException(nameof(initialWidth));
        if (initialHeight <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeight));
        if (presentQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(presentQueueCapacity));
        if (textureMirrorAnchor is not null && ownsThread)
        {
            throw new ArgumentException(
                "Texture mirror outputs cannot use ownsThread:true; the anchor drives rendering. Use ownsThread:false or CreateTextureMirror.",
                nameof(ownsThread));
        }

        _title = title;
        _initialWindowWidth = initialWidth;
        _initialWindowHeight = initialHeight;
        _vsync = vsync;
        _ownsThread = ownsThread;
        _hdrPreference = hdrPreference;
        _swapchainBitDepth = swapchainBitDepth;
        _viewportWidth = initialWidth;
        _viewportHeight = initialHeight;
        _win32Nv12Device = new Win32Nv12GlUploadDeviceResolver(borrowD3D11DeviceComPtrForNv12Gl,
            createFallbackD3D11InteropDeviceForWin32Nv12,
            "SDL3GLVideoOutput");
        _textureMirrorAnchor = textureMirrorAnchor;
        _presentQueue = new PresentFrameQueue(presentQueueCapacity);
    }

    /// <summary>Default <see cref="PresentQueueCapacity"/>. See <see cref="PresentFrameQueue.DefaultCapacity"/>.</summary>
    public const int DefaultPresentQueueCapacity = PresentFrameQueue.DefaultCapacity;

    /// <summary>
    /// Creates a secondary window that draws the same GL plane textures as <paramref name="anchor"/> (one upload on the anchor).
    /// Configure with the same <see cref="VideoFormat"/> as the anchor, then call <see cref="RegisterTextureMirror"/> on the anchor.
    /// </summary>
    public static SDL3GLVideoOutput CreateTextureMirror(SDL3GLVideoOutput anchor, string title = "SDL3 GL (mirror)",
        int initialWidth = 1280, int initialHeight = 720, bool vsync = true,
        GlVideoOutputHdrPreference hdrPreference = GlVideoOutputHdrPreference.FollowFrameHints,
        GlOutputBitDepth swapchainBitDepth = GlOutputBitDepth.Auto)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new SDL3GLVideoOutput(title, initialWidth, initialHeight, vsync, ownsThread: false, hdrPreference,
            swapchainBitDepth,
            borrowD3D11DeviceComPtrForNv12Gl: 0, createFallbackD3D11InteropDeviceForWin32Nv12: true, textureMirrorAnchor: anchor);
    }

    /// <summary>Requested drawable bit depth (actual depth may differ when <see cref="GlOutputBitDepth.Auto"/> falls back).</summary>
    public GlOutputBitDepth SwapchainBitDepth => _swapchainBitDepth;

    /// <summary>
    /// After both outputs are <see cref="Configure"/>d with the same format, register this mirror so the anchor presents it each frame.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="mirror"/> was not created for this anchor.</exception>
    /// <exception cref="InvalidOperationException">When formats differ or outputs are not configured.</exception>
    public void RegisterTextureMirror(SDL3GLVideoOutput mirror)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mirror);
        if (mirror._textureMirrorAnchor != this)
            throw new ArgumentException("Mirror must be created with CreateTextureMirror(this, ...).", nameof(mirror));
        if (mirror == this)
            throw new ArgumentException("Cannot register a output as its own mirror.", nameof(mirror));
        if (!_configured || !mirror._configured)
            throw new InvalidOperationException("Configure both the anchor and the mirror before RegisterTextureMirror.");
        if (_ownsThread)
            throw new InvalidOperationException(
                "RegisterTextureMirror requires the anchor to use ownsThread:false (see CreateTextureMirror remarks).");
        if (mirror._format != _format)
            throw new InvalidOperationException(
                $"Mirror format {mirror._format} must match anchor format {_format}.");

        lock (_textureMirrorLock)
        {
            if (_registeredTextureMirrors.Contains(mirror))
                return;
            _registeredTextureMirrors.Add(mirror);
        }
    }

    public void UnregisterTextureMirror(SDL3GLVideoOutput mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        lock (_textureMirrorLock)
            _registeredTextureMirrors.Remove(mirror);
    }

    private SDL3GLVideoOutput[] SnapshotRegisteredMirrors()
    {
        lock (_textureMirrorLock)
            return _registeredTextureMirrors.Count == 0
                ? []
                : _registeredTextureMirrors.ToArray();
    }

    /// <summary>Allows changing HDR handling after construction (effective on the next rendered frame).</summary>
    public GlVideoOutputHdrPreference HdrPreference
    {
        get => _hdrPreference;
        set => _hdrPreference = value;
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        _win32Nv12Device.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the SDL render thread (same thread as OpenGL for this window),
    /// or runs it immediately when <see cref="OwnsThread"/> is <see langword="false"/> (caller must be the thread
    /// that calls <see cref="Pump"/> and owns the GL context).
    /// </summary>
    public void InvokeOnRenderThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
            throw new InvalidOperationException("SDL3GLVideoOutput.InvokeOnRenderThread called before Configure");

        if (_ownsThread)
        {
            _renderThreadActions.Enqueue(action);
            _wakeup.Set();
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Moves / resizes the SDL window and toggles desktop fullscreen on the chosen display index
    /// (same ordering as <see cref="SDL.GetDisplays(System.Int32@)"/>).
    /// </summary>
    public void ApplyWindowPlacement(int displayIndex, bool fullscreen, int? windowWidth, int? windowHeight) =>
        ScheduleWindowPlacement(() => ApplyWindowPlacementCore(displayIndex, fullscreen, windowWidth, windowHeight));

    /// <summary>
    /// Moves/resizes the SDL window using a display rectangle supplied by the host UI. This is useful
    /// when the host's screen list is authoritative and may not match SDL's display ordering.
    /// </summary>
    public void ApplyWindowPlacementToBounds(
        int displayX,
        int displayY,
        int displayWidth,
        int displayHeight,
        bool fullscreen,
        int? windowWidth,
        int? windowHeight) =>
        ScheduleWindowPlacement(() => ApplyWindowPlacementCore(
            displayX,
            displayY,
            displayWidth,
            displayHeight,
            fullscreen,
            windowWidth,
            windowHeight));

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
            throw new NotSupportedException(
                $"SDL3GLVideoOutput does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("video format must have positive dimensions", nameof(format));

        if (_configured)
        {
            // Idempotent for the same format.
            if (_format == format)
                return;

            // Mirrors share GL textures with the anchor's renderer - reconfiguring the anchor would
            // invalidate them. Disallow reconfigure while any mirror is registered.
            lock (_textureMirrorLock)
            {
                if (_registeredTextureMirrors.Count > 0)
                    throw new InvalidOperationException(
                        "Cannot reconfigure an SDL3GLVideoOutput that has registered texture mirrors - dispose mirrors first.");
            }

            // Drop frames queued for the old format so Submit's format guard doesn't reject them after switch.
            foreach (var stale in _presentQueue.DrainAll())
                stale.Dispose();

            _format = format;
            _canIdleRepaint = false;

            // Rebuild the renderer on the render thread so GL teardown happens with the right context current.
            Exception? rebuildError = null;
            using var done = new ManualResetEventSlim(false);
            InvokeOnRenderThread(() =>
            {
                try
                {
                    try { _renderer?.Dispose(); }
                    catch { /* best effort */ }
                    _renderer = null;
                    _renderer = BuildRendererForCurrentFormat();
                }
                catch (Exception ex)
                {
                    rebuildError = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait(TimeSpan.FromSeconds(4));
            if (rebuildError is not null)
                throw new InvalidOperationException("SDL3GLVideoOutput reconfigure failed", rebuildError);

            return;
        }

        _format = format;

        if (_textureMirrorAnchor is { } anchor && !anchor._configured)
            throw new InvalidOperationException("Configure the anchor SDL3GLVideoOutput before configuring a texture mirror.");

        if (_ownsThread)
        {
            _configured = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _renderThread = new Thread(() => RenderLoop(token))
            {
                IsBackground = true,
                Name = $"SDL3GLVideoOutput:{_title}",
            };
            _renderThread.Start();

            _ready.Wait(token);
            if (_renderError is not null)
            {
                var err = _renderError;
                _renderError = null;
                throw err;
            }
        }
        else
        {
            SDL3Runtime.Acquire();
            try
            {
                InitGraphics();
                _configured = true;
            }
            catch
            {
                SDL3Runtime.Release();
                throw;
            }
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("SDL3GLVideoOutput.Submit called before Configure");
        }
        if (_textureMirrorAnchor is not null)
        {
            frame.Dispose();
            throw new NotSupportedException(
                "Texture mirror outputs do not accept frames; submit to the anchor output that this mirror was created from.");
        }
        if (frame.Format.Width != _format.Width || frame.Format.Height != _format.Height
            || frame.Format.PixelFormat != _format.PixelFormat)
        {
            frame.Dispose();
            throw new ArgumentException(
                $"frame format {frame.Format} does not match output format {_format}", nameof(frame));
        }

        _presentQueue.Enqueue(frame, out var evicted);
        evicted?.Dispose();

        // Dispose sets _disposed, joins the render thread, and then drains the queue. A Submit that
        // passed the guard above can interleave after that drain and park a pooled frame with no
        // presenter left to ever collect it (a pooled-buffer leak, not just a stale frame), so
        // re-check afterwards and clean up ourselves. DrainAll is atomic, so if Dispose already took
        // the frames this returns empty rather than double-freeing them. The same reasoning covers a
        // concurrent AbandonQueuedFrames: enqueue-after-its-drain is fine while the output is alive
        // (the presenter will collect the frame), and fatal only in this disposed case.
        // Same pattern as VideoOpenGlControl.Submit.
        if (_disposed)
        {
            foreach (var stranded in _presentQueue.DrainAll())
                stranded.Dispose();
            throw new ObjectDisposedException(nameof(SDL3GLVideoOutput));
        }

        if (_ownsThread) _wakeup.Set();
    }

    public void AbandonQueuedFrames()
    {
        foreach (var frame in _presentQueue.DrainAll())
        {
            frame.Dispose();
            Interlocked.Increment(ref _droppedNew);
        }
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        if (_ownsThread)
            _wakeup.Set();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (QueuedFrameCount == 0 && Volatile.Read(ref _activePresentations) == 0)
                return true;
            if (Environment.TickCount64 >= deadline)
                return false;
            Thread.Sleep(1);
            if (_ownsThread)
                _wakeup.Set();
        }
    }

    /// <summary>Manual mode driver (see <see cref="SDL3VideoOutput.Pump"/>).</summary>
    public void Pump()
    {
        if (_ownsThread)
            throw new InvalidOperationException("SDL3GLVideoOutput.Pump called on an auto-thread output - use ownsThread:false");
        if (_disposed) return;
        if (!_configured) return;

        DrainEvents();

        Interlocked.Increment(ref _activePresentations);
        try
        {
            var frame = _presentQueue.TryDequeueLatest(out var queuedAt, out var superseded);
            foreach (var stale in superseded)
                stale.Dispose();
            if (frame is not null)
            {
                _pumpEmptyPulls = 0;
                var presented = false;
                try { presented = PresentFrame(frame); }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput.Pump PresentFrame");
                }
                finally { frame.Dispose(); }

                if (presented)
                    RecordPresentLatency(queuedAt);
            }
            else if (_canIdleRepaint)
            {
                // Same repeat accounting as the RenderLoop's empty pull: a repaint that actually
                // reached the panel while content was still flowing is a repeat that the
                // IVideoOutputPresentDiagnostics contract requires us to report, but once the queue
                // has been dry for a while the window is just idle and counting would bury the real
                // signal. Without this, manual-Pump outputs reported RepeatedFrames == 0 forever,
                // which made a producer slower than the display look perfectly healthy.
                var contentWasFlowing = _pumpEmptyPulls < EmptyPullsBeforeIdle;
                if (contentWasFlowing)
                    _pumpEmptyPulls++;

                var swapped = false;
                try { swapped = PresentWithoutNewUpload(); }
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput.Pump PresentWithoutNewUpload");
                }

                if (swapped && contentWasFlowing)
                    Interlocked.Increment(ref _repeated);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activePresentations);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _textureMirrorAnchor?.UnregisterTextureMirror(this);
        _disposed = true;
        _win32Nv12Device.SetBorrowVideoSourceForWin32Nv12Gl(null);

        var threadStillRunning = false;
        if (_ownsThread)
        {
            _cts?.Cancel();
            _wakeup.Set();
            CooperativePlaybackJoin.JoinThread(_renderThread, TimeSpan.FromSeconds(45));
            threadStillRunning = _renderThread is { IsAlive: true };
        }
        else
        {
            try { TeardownGraphics(); }
            finally { SDL3Runtime.Release(); }
        }

        foreach (var leftover in _presentQueue.DrainAll())
            leftover.Dispose();

        if (threadStillRunning)
        {
            // Render thread didn't stop within the join window - it may still touch _wakeup / _ready /
            // the cts token / the D3D11 device. Leak them rather than dispose under the live thread
            // (use-after-dispose / handle-reuse race). Same policy as VideoOutputPump.Dispose (P2-4).
            MediaDiagnostics.LogWarning(
                "SDL3GLVideoOutput: render thread did not stop within 45 s on Dispose; leaking wait handles/cts/device to avoid disposing them under the live thread.");
            return;
        }

        _cts?.Dispose();
        _wakeup.Dispose();
        _ready.Dispose();
        _win32Nv12Device.Dispose();
    }

    internal bool TryGetGlStateForMirror(out nint window, out nint glContext, out YuvVideoRenderer? anchorRenderer)
    {
        window = _window;
        glContext = _glContext;
        anchorRenderer = _renderer;
        return _gl != null && _window != nint.Zero && _glContext != nint.Zero && _renderer != null;
    }

    /// <summary>Used when the anchor tears down: mirrors must release GL before the anchor destroys shared textures.</summary>
    internal void ForceDisposeMirrorFromAnchor()
    {
        if (_textureMirrorAnchor is null)
            return;
        if (_disposed)
            return;
        _disposed = true;
        var releaseSDL = _graphicsInitialized;
        TeardownGraphicsCore();
        if (releaseSDL)
            SDL3Runtime.Release();
    }

    // ---------- render thread (auto mode) ---------------------------------

    private void RenderLoop(CancellationToken token)
    {
        try
        {
            SDL3Runtime.Acquire();
            SDL3Runtime.RegisterAutoThreadOutput($"SDL3GLVideoOutput '{_title}'");
            try
            {
                InitGraphics();
                _ready.Set();

                var handles = new WaitHandle[] { _wakeup, token.WaitHandle };
                // Pull, don't wait to be pushed. A vsynced swap blocks until the next vblank, so once
                // frames are flowing the swap IS the loop's clock: each iteration takes exactly one
                // refresh period and pulls whatever the queue has for it. That makes the queue - not the
                // arrival phase of a submit - decide what gets shown, which is the whole point.
                //
                // Two cases must still wait on a submit instead, or the loop spins a core:
                //   * nothing has been presented yet, or the queue has been dry for a while (idle window)
                //   * the swap isn't actually blocking (vsync off, or occluded/minimised window)
                var swapPacedUs = false;
                var emptyPulls = 0;

                // A frame pulled from the queue but deliberately not yet shown: the pacer decided its
                // ideal display time is nearer the FOLLOWING vblank than the upcoming one. Held here
                // rather than left in the queue because PresentFrameQueue has no peek, and because the
                // hold slot is what gives a jittery producer a vblank of arrival slack (see
                // PresentFramePacer). Only ever non-null while the swap is pacing the loop, so it is
                // shown or discarded within a refresh or two - never parked. Disposed on loop exit.
                VideoFrame? heldForPacing = null;
                long heldForPacingQueuedAt = 0;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!swapPacedUs)
                        {
                            var idx = WaitHandle.WaitAny(handles, 50);
                            if (idx == 1) break;
                        }

                        DrainEvents();

                        var swapped = false;
                        Interlocked.Increment(ref _activePresentations);
                        try
                        {
                            // Pacing needs the swap to actually be the loop's clock AND a converged
                            // refresh estimate AND a blocking swap return to predict the next vblank
                            // from. Anything less (vsync off, occluded window, fresh panel) keeps the
                            // proven latest-wins behaviour.
                            var paceThisVblank = PresentPacingEnabled
                                                 && swapPacedUs
                                                 && _pacer.RefreshEstimateReady
                                                 && _lastBlockingSwapReturn != 0;

                            VideoFrame? frameToShow = null;
                            long frameQueuedAt = 0;
                            var deliberateHold = false;

                            if (paceThisVblank)
                            {
                                // Producer got two or more frames ahead of a frame we were holding:
                                // pacing must never convert producer lead into latency, so the held
                                // frame loses its slot and the loop falls back toward latest-wins.
                                // The pacer's phase anchor referred to the abandoned cadence.
                                if (heldForPacing is not null && _presentQueue.Count > 1)
                                {
                                    heldForPacing.Dispose();
                                    heldForPacing = null;
                                    Interlocked.Increment(ref _droppedNew);
                                    _pacer.ResetPhase();
                                }

                                if (heldForPacing is null)
                                {
                                    if (_presentQueue.Count > 1)
                                    {
                                        // Depth beyond one means the content is outrunning the panel;
                                        // holding anything would only add latency. Latest-wins.
                                        frameToShow = _presentQueue.TryDequeueLatest(out frameQueuedAt, out var superseded);
                                        foreach (var stale in superseded)
                                            stale.Dispose();
                                        _pacer.ResetPhase();
                                    }
                                    else
                                    {
                                        heldForPacing = _presentQueue.TryDequeue(out heldForPacingQueuedAt);
                                    }
                                }

                                if (frameToShow is null && heldForPacing is not null)
                                {
                                    var decision = _pacer.DecideAtVblank(
                                        StopwatchToTimeSpan(_lastBlockingSwapReturn),
                                        hasCurrentFrame: Volatile.Read(ref _hasLastPresentedPts) != 0,
                                        currentFramePts: TimeSpan.FromTicks(Volatile.Read(ref _lastPresentedPtsTicks)),
                                        nextFramePts: heldForPacing.PresentationTime);
                                    if (decision == PresentPacingDecision.ShowNext)
                                    {
                                        frameToShow = heldForPacing;
                                        frameQueuedAt = heldForPacingQueuedAt;
                                        heldForPacing = null;
                                    }
                                    else
                                    {
                                        deliberateHold = true;
                                    }
                                }
                            }
                            else
                            {
                                if (heldForPacing is not null)
                                {
                                    // Pacing just disengaged with a frame in hand (swap stopped
                                    // blocking, estimate reseeded, or the kill switch semantics never
                                    // held one). Fold it back into latest-wins: anything queued was
                                    // submitted after it, so the newest queued frame supersedes it.
                                    _pacer.ResetPhase();
                                    frameToShow = heldForPacing;
                                    frameQueuedAt = heldForPacingQueuedAt;
                                    heldForPacing = null;
                                }

                                var newest = _presentQueue.TryDequeueLatest(out var newestQueuedAt, out var superseded);
                                foreach (var stale in superseded)
                                    stale.Dispose();
                                if (newest is not null)
                                {
                                    if (frameToShow is not null)
                                    {
                                        frameToShow.Dispose();
                                        Interlocked.Increment(ref _droppedNew);
                                    }

                                    frameToShow = newest;
                                    frameQueuedAt = newestQueuedAt;
                                }
                            }

                            if (frameToShow is not null)
                            {
                                emptyPulls = 0;
                                try { swapped = PresentFrame(frameToShow); }
                                catch (Exception ex)
                                {
                                    MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput.RenderLoop PresentFrame");
                                }
                                finally { frameToShow.Dispose(); }

                                // AFTER the swap returns, so the sample includes the vsync block - that wait
                                // is most of the latency and measuring before it would report a fraction of it.
                                if (swapped)
                                    RecordPresentLatency(frameQueuedAt);
                            }
                            else if (deliberateHold)
                            {
                                // The pacer is parking the queued frame for the vblank nearest its
                                // ideal time. The panel still re-shows the previous frame, and by the
                                // diagnostics contract that is a repeat whether it was deliberate or
                                // not - the viewer saw the same frame twice either way. Content is
                                // flowing (a frame is literally in hand), so no empty-pull bookkeeping.
                                emptyPulls = 0;
                                try { swapped = PresentWithoutNewUpload(); }
                                catch (Exception ex)
                                {
                                    MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput.RenderLoop PresentWithoutNewUpload");
                                }

                                if (swapped)
                                    Interlocked.Increment(ref _repeated);
                            }
                            else if (_canIdleRepaint)
                            {
                                // The producer had nothing for this vblank.
                                var contentWasFlowing = emptyPulls < EmptyPullsBeforeIdle;
                                if (contentWasFlowing)
                                    emptyPulls++;

                                try { swapped = PresentWithoutNewUpload(); }
                                catch (Exception ex)
                                {
                                    MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput.RenderLoop PresentWithoutNewUpload");
                                }

                                // Only a repaint that actually reached the panel, and only while content was
                                // still flowing, is a repeat. Once the queue has been dry for a while the
                                // window is simply idle and the repaint is just keeping it alive - counting
                                // those would bury the real signal under a steady idle drip.
                                if (swapped && contentWasFlowing)
                                    Interlocked.Increment(ref _repeated);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activePresentations);
                        }

                        swapPacedUs = swapped
                                      && _vsync
                                      && emptyPulls < EmptyPullsBeforeIdle
                                      && _nonBlockingSwapStreak < NonBlockingSwapStreakLimit;
                    }
                }
                finally
                {
                    // A held frame is loop-local, so Dispose's queue drain cannot see it - collect it
                    // here or it strands a pooled buffer exactly like the Submit/Dispose race.
                    heldForPacing?.Dispose();
                }
            }
            finally
            {
                TeardownGraphics();
                SDL3Runtime.UnregisterAutoThreadOutput();
                SDL3Runtime.Release();
            }
        }
        catch (Exception ex)
        {
            _renderError = ex;
            _ready.Set();
        }
    }

    private void InitGraphics()
    {
        // The GL attribute block, the window list and SDL's EGL state are process-global, and every
        // video output builds its window on its OWN render thread. Overlapping this with the
        // composition compositor's context creation segfaults inside SDL_CreateWindow — see
        // SDL3Runtime.EnterVideoDevice. Held for the whole sequence, released before the render loop.
        using var videoDevice = SDL3Runtime.EnterVideoDevice();

        ApplyStandardGlContextAttributes();

        if (_textureMirrorAnchor is { } shareFrom)
        {
            if (shareFrom._ownsThread)
            {
                throw new InvalidOperationException(
                    "Texture mirror initialization requires the anchor output to use ownsThread:false so the anchor GL context can be made current on this thread. Initializing a mirror while the anchor runs OpenGL on a background thread is not supported (unsafe SDL_GL_MakeCurrent across threads).");
            }

            if (!shareFrom.TryGetGlStateForMirror(out var anchorWindow, out var anchorGlContext, out var anchorRenderer)
                || anchorRenderer is null)
            {
                throw new InvalidOperationException(
                    "Texture mirror init requires the anchor output to finish OpenGL initialization first (configure the anchor before the mirror).");
            }

            if (!SDL.GLMakeCurrent(anchorWindow, anchorGlContext))
                throw new InvalidOperationException($"SDL_GL_MakeCurrent (anchor) failed: {SDL.GetError()}");

            try
            {
                if (!SDL.GLSetAttribute(SDL.GLAttr.ShareWithCurrentContext, 1))
                    MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_SetAttribute(ShareWithCurrentContext) failed: {0}", SDL.GetError());

                // NotFocusable: a video-output window needs no keyboard focus, and on Wayland the clipboard /
                // primary-selection data offer is delivered to the keyboard-focused surface - so a non-focusable
                // window never receives it, sidestepping SDL3 3.4.x's null-deref in Wayland_data_offer_add_mime
                // (uncatchable SIGSEGV on a middle-click primary paste). Mouse events + WM resize/close still work.
                _window = SDL.CreateWindow(_title, _initialWindowWidth, _initialWindowHeight,
                    SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable | SDL.WindowFlags.NotFocusable);
                if (_window == nint.Zero)
                    throw new InvalidOperationException($"SDL_CreateWindow (mirror) failed: {SDL.GetError()}");

                ApplyWindowConstraintsCore();
                ApplyPendingWindowPlacementCore();

                if (!SDL.ShowWindow(_window))
                    MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_ShowWindow failed: {0}", SDL.GetError());
                if (!SDL.RaiseWindow(_window))
                    MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_RaiseWindow failed: {0}", SDL.GetError());

                _glContext = SDL.GLCreateContext(_window);
                if (_glContext == nint.Zero)
                    throw new InvalidOperationException($"SDL_GL_CreateContext (mirror) failed: {SDL.GetError()}");

                if (!SDL.GLSetAttribute(SDL.GLAttr.ShareWithCurrentContext, 0))
                    MediaDiagnostics.LogWarning("SDL3GLVideoOutput: clearing ShareWithCurrentContext failed: {0}", SDL.GetError());

                if (!SDL.GLMakeCurrent(_window, _glContext))
                    throw new InvalidOperationException($"SDL_GL_MakeCurrent (mirror) failed: {SDL.GetError()}");

                SDL.GLSetSwapInterval(_vsync ? 1 : 0);

                _gl = SilkGL.GetApi(name => SDL.GLGetProcAddress(name));

                try
                {
                    _renderer = YuvVideoRenderer.CreateSharedTextureDrawView(_gl, anchorRenderer, sharedShaderPrograms: true,
                        yPlaneMipmaps: false);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidOperationException(
                        "Texture mirror is not supported for this pixel format or upload path (NV12 interop and similar require CPU-plane decoding or future shared-interop work).",
                        ex);
                }

                _graphicsInitialized = true;
                return;
            }
            catch
            {
                TeardownGraphicsCore();
                if (!SDL.GLMakeCurrent(anchorWindow, anchorGlContext))
                {
                    MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_MakeCurrent (restore anchor after mirror init failure) failed: {0}",
                        SDL.GetError());
                }

                throw;
            }
        }

        // NotFocusable - see the mirror-window note above: a focus-less window never receives the Wayland
        // clipboard/primary-selection data offer, avoiding SDL3's Wayland_data_offer_add_mime crash on middle-click.
        _window = SDL.CreateWindow(_title, _initialWindowWidth, _initialWindowHeight,
            SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable | SDL.WindowFlags.NotFocusable);
        if (_window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        ApplyWindowConstraintsCore();
        ApplyPendingWindowPlacementCore();

        if (!SDL.ShowWindow(_window))
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_ShowWindow failed: {0}", SDL.GetError());
        if (!SDL.RaiseWindow(_window))
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_RaiseWindow failed: {0}", SDL.GetError());

        _glContext = SDL.GLCreateContext(_window);
        if (_glContext == nint.Zero)
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");

        if (!SDL.GLMakeCurrent(_window, _glContext))
            throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

        SDL.GLSetSwapInterval(_vsync ? 1 : 0);

        _gl = SilkGL.GetApi(name => SDL.GLGetProcAddress(name));
        VerifySwapchainBitDepth();

        _renderer = BuildRendererForCurrentFormat();

        _graphicsInitialized = true;
    }

    /// <summary>Constructs a <see cref="YuvVideoRenderer"/> for <see cref="_format"/>. Caller must hold the GL
    /// context current and have <see cref="_gl"/> initialized. Used by initial setup and by reconfigure.</summary>
    private YuvVideoRenderer BuildRendererForCurrentFormat()
    {
        nint win32D3d11DevicePtr = 0;
        if (OperatingSystem.IsWindows() && _format.PixelFormat == PixelFormat.Nv12)
            win32D3d11DevicePtr = _win32Nv12Device.ResolveDevicePointerForNv12RendererConstruction();

        var allowLazyNv12 = OperatingSystem.IsWindows()
            && _format.PixelFormat == PixelFormat.Nv12
            && win32D3d11DevicePtr == 0
            && !_win32Nv12Device.CreatesFallbackOwnedInteropDevice;

        return new YuvVideoRenderer(_gl!, _format, eglDmabufInterop: OperatingSystem.IsLinux()
                ? new YuvDmabufEglInterop(SDL.EGLGetCurrentDisplay(), name => SDL.EGLGetProcAddress(name))
                : null,
            win32D3D11DeviceComPtrForNv12: win32D3d11DevicePtr,
            allowLazyWin32Nv12UploaderFromDecodedFrame: allowLazyNv12);
    }

    private void ApplyStandardGlContextAttributes()
    {
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
        var useTenBit = _swapchainBitDepth is GlOutputBitDepth.Ten or GlOutputBitDepth.Auto;
        if (useTenBit)
        {
            SDL.GLSetAttribute(SDL.GLAttr.RedSize, 10);
            SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 10);
            SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 10);
            SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 2);
        }
        else
        {
            SDL.GLSetAttribute(SDL.GLAttr.RedSize, 8);
            SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 8);
            SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 8);
            SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 0);
        }

        SDL.GLSetAttribute(SDL.GLAttr.DepthSize, 0);
    }

    private void VerifySwapchainBitDepth()
    {
        if (!SDL.GLGetAttribute(SDL.GLAttr.RedSize, out var red)
            || !SDL.GLGetAttribute(SDL.GLAttr.GreenSize, out var green)
            || !SDL.GLGetAttribute(SDL.GLAttr.BlueSize, out var blue))
            return;

        var gotTenBit = red >= 10 && green >= 10 && blue >= 10;
        if (_swapchainBitDepth is GlOutputBitDepth.Ten or GlOutputBitDepth.Auto)
        {
            if (!gotTenBit && Interlocked.Exchange(ref _swapchainTenBitFallbackLogged, 1) == 0)
            {
                MediaDiagnostics.LogWarning(
                    "SDL3GLVideoOutput: 10-bit swapchain unavailable (R/G/B bits={Red}/{Green}/{Blue}); using 8-bit drawable.",
                    red, green, blue);
            }
        }
    }

    private void TeardownGraphics()
    {
        if (_textureMirrorAnchor is null)
        {
            SDL3GLVideoOutput[] copy;
            lock (_textureMirrorLock)
            {
                copy = _registeredTextureMirrors.ToArray();
                _registeredTextureMirrors.Clear();
            }

            foreach (var m in copy)
            {
                MediaDiagnostics.SwallowDisposeErrors(m.ForceDisposeMirrorFromAnchor, "SDL3GLVideoOutput.TeardownGraphics: mirror");
            }
        }

        TeardownGraphicsCore();
    }

    private void TeardownGraphicsCore()
    {
        // Destroying a window mutates the same global list creation walks. An output going away while
        // another builds its window is the same race as two creations overlapping.
        using var videoDevice = SDL3Runtime.EnterVideoDevice();

        MediaDiagnostics.SwallowDisposeErrors(() => _renderer?.Dispose(), "SDL3GLVideoOutput.TeardownGraphicsCore: renderer");
        _renderer = null;
        MediaDiagnostics.SwallowDisposeErrors(_win32Nv12Device.DisposeOwnedInteropHost, "SDL3GLVideoOutput.TeardownGraphicsCore: Win32 NV12 resolver");
        MediaDiagnostics.SwallowDisposeErrors(() => _gl?.Dispose(), "SDL3GLVideoOutput.TeardownGraphicsCore: GL");
        _gl = null;
        if (_glContext != nint.Zero)
        {
            SDL.GLDestroyContext(_glContext);
            _glContext = nint.Zero;
        }

        if (_window != nint.Zero)
        {
            SDL.DestroyWindow(_window);
            _window = nint.Zero;
        }

        _graphicsInitialized = false;
    }

    /// <returns><c>true</c> when a swap was actually issued (so the caller may treat it as its pacer).</returns>
    private bool PresentFrame(VideoFrame frame)
    {
        if (_renderer is null || _gl is null) return false;
        if (_disposed)
            return false;

        GlVideoOutputHdr.ApplyTransferHint(_renderer, frame, _hdrPreference);
        _renderer.Upload(frame);
        _gl.Flush();
        PresentTextureMirrors(frame);
        if (_window != nint.Zero && _glContext != nint.Zero
            && !SDL.GLMakeCurrent(_window, _glContext))
        {
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_MakeCurrent (anchor) before render failed: {0}", SDL.GetError());
            return false;
        }

        ClearForLetterboxIfNeeded();
        _renderer.Render(_viewportWidth, _viewportHeight, ViewportFit);
        if (_disposed)
            SDL.GLSetSwapInterval(0);

        SwapWindowTimed();
        Volatile.Write(ref _lastPresentedPtsTicks, frame.PresentationTime.Ticks);
        Volatile.Write(ref _hasLastPresentedPts, 1);
        Interlocked.Increment(ref _displayed);
        _canIdleRepaint = true;
        LogFirstPresentDiagnostics(frame);
        return true;
    }

    /// <summary>
    /// Swaps and records whether the swap blocked. With vsync on, a swap that returns essentially
    /// instantly did NOT wait for a vblank - the window is occluded/minimised, or the platform queues
    /// rather than blocks - so the render loop must not treat it as a pacer or it spins a core. A streak
    /// is required before believing it, because a single fast swap is normal right after a missed vblank.
    /// </summary>
    private void SwapWindowTimed()
    {
        var started = Stopwatch.GetTimestamp();
        SDL.GLSwapWindow(_window);
        var returned = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(started, returned) < NonBlockingSwapThreshold)
        {
            if (_nonBlockingSwapStreak < NonBlockingSwapStreakLimit)
                _nonBlockingSwapStreak++;

            // This swap did not land on a vblank, so the interval from here to the next blocking
            // swap spans an unknown number of refreshes - break the pacer's measurement chain
            // rather than feed it a length it would have to guess at.
            _lastBlockingSwapReturn = 0;
        }
        else
        {
            _nonBlockingSwapStreak = 0;

            // Two consecutive blocking swap returns straddle exactly the vblanks between them; the
            // pacer's own outlier rejection discards the double-length intervals of a missed vblank.
            if (_lastBlockingSwapReturn != 0)
                _pacer.ObserveSwapInterval(Stopwatch.GetElapsedTime(_lastBlockingSwapReturn, returned));
            _lastBlockingSwapReturn = returned;
        }
    }

    private void LogFirstPresentDiagnostics(VideoFrame frame)
    {
        if (Interlocked.Exchange(ref _firstPresentLogged, 1) != 0)
            return;

        var luma = SampleAverageLuma(frame);
        MediaDiagnostics.LogInformation(
            "SDL3GLVideoOutput '{Title}': first present pts={Pts} {W}x{H} {Pf} stride0={Stride} avgLuma={Luma:F1} displayed={Displayed}",
            _title, frame.PresentationTime, frame.Format.Width, frame.Format.Height, frame.Format.PixelFormat,
            frame.Strides.Length > 0 ? frame.Strides[0] : 0, luma, Volatile.Read(ref _displayed));
    }

    private static double SampleAverageLuma(VideoFrame frame)
    {
        try
        {
            if (frame.PlaneCount == 0 || frame.Planes[0].Length == 0)
                return 0;
            var span = frame.Planes[0].Span;
            return frame.Format.PixelFormat switch
            {
                PixelFormat.Bgra32 or PixelFormat.Rgba32 => SampleBgraLuma(span),
                PixelFormat.Uyvy or PixelFormat.Yuyv => SamplePacked422Luma(span, isUyvy: frame.Format.PixelFormat == PixelFormat.Uyvy),
                _ => 0,
            };
        }
        catch
        {
            return -1;
        }
    }

    private static double SampleBgraLuma(ReadOnlySpan<byte> bgra)
    {
        long sum = 0;
        var samples = 0;
        var step = Math.Max(16, (bgra.Length / 4) / 4096);
        for (var i = 0; i < bgra.Length - 3; i += step * 4)
        {
            sum += bgra[i + 2]; // BGRA: G channel approximates luma for quick sanity check
            samples++;
        }

        return samples == 0 ? 0 : (double)sum / samples;
    }

    private static double SamplePacked422Luma(ReadOnlySpan<byte> packed, bool isUyvy)
    {
        long sum = 0;
        var samples = 0;
        var step = Math.Max(8, packed.Length / 4096);
        for (var i = 0; i < packed.Length - 3; i += step)
        {
            sum += isUyvy ? packed[i + 1] : packed[i];
            if (!isUyvy)
                i++;
            samples++;
        }

        return samples == 0 ? 0 : (double)sum / samples;
    }

    private void ClearForLetterboxIfNeeded()
    {
        // Stretch already covers every pixel - skip the clear to avoid an unnecessary state change.
        if (ViewportFit == VideoViewportFit.Stretch || _gl is null)
            return;
        _gl.Viewport(0, 0, (uint)_viewportWidth, (uint)_viewportHeight);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit);
    }

    /// <returns><c>true</c> when a swap was actually issued (so the caller may treat it as its pacer).</returns>
    private bool PresentWithoutNewUpload()
    {
        if (_textureMirrorAnchor is not null)
            return false;
        if (_renderer is null || _gl is null || !_canIdleRepaint || _disposed)
            return false;

        if (_window != nint.Zero && _glContext != nint.Zero
            && !SDL.GLMakeCurrent(_window, _glContext))
        {
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_MakeCurrent (idle) failed: {0}", SDL.GetError());
            return false;
        }

        ClearForLetterboxIfNeeded();
        _renderer.Render(_viewportWidth, _viewportHeight, ViewportFit);
        if (_disposed)
            SDL.GLSetSwapInterval(0);

        SwapWindowTimed();
        return true;
    }

    private void PresentTextureMirrors(VideoFrame frame)
    {
        if (_textureMirrorAnchor is not null)
            return;

        foreach (var mirror in SnapshotRegisteredMirrors())
        {
            if (mirror._disposed)
                continue;
            try { mirror.PresentMirroredFrame(frame); }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput mirror present");
            }
        }
    }

    internal void PresentMirroredFrame(VideoFrame frame)
    {
        if (_textureMirrorAnchor is null)
            return;
        if (_textureMirrorAnchor._disposed || _disposed)
            return;
        if (_renderer is null || _gl is null)
            return;
        var anchor = _textureMirrorAnchor;
        if (anchor._window == nint.Zero || anchor._glContext == nint.Zero)
            return;

        GlVideoOutputHdr.ApplyTransferHint(_renderer, frame, _hdrPreference);
        if (!SDL.GLMakeCurrent(_window, _glContext))
        {
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_MakeCurrent (mirror) failed: {0}", SDL.GetError());
            return;
        }

        try
        {
            ClearForLetterboxIfNeeded();
            _renderer.Render(_viewportWidth, _viewportHeight, ViewportFit);
            if (_disposed || anchor._disposed)
                SDL.GLSetSwapInterval(0);

            SDL.GLSwapWindow(_window);
            Interlocked.Increment(ref _displayed);
        }
        finally
        {
            if (!SDL.GLMakeCurrent(anchor._window, anchor._glContext))
            {
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_GL_MakeCurrent (restore anchor) failed: {0}",
                    SDL.GetError());
            }
        }
    }

    private void DrainEvents()
    {
        DrainPendingRenderThreadActions();
        SDL.PumpEvents();
        var mirrors = SnapshotRegisteredMirrors();
        while (SDL.PollEvent(out var ev))
        {
            if (TryConsumeWindowEvent(ev))
                continue;

            var consumed = false;
            foreach (var m in mirrors)
            {
                if (m.TryConsumeWindowEvent(ev))
                {
                    consumed = true;
                    break;
                }
            }

            if (consumed)
                continue;

            if ((SDL.EventType)ev.Type == SDL.EventType.Quit)
                SafeRaise(CloseRequested);
        }
    }

    private bool TryConsumeWindowEvent(SDL.Event ev)
    {
        switch ((SDL.EventType)ev.Type)
        {
            case SDL.EventType.WindowCloseRequested:
                if (ev.Window.WindowID == GetWindowId())
                {
                    SafeRaise(CloseRequested);
                    return true;
                }

                return false;
            case SDL.EventType.WindowResized:
            case SDL.EventType.WindowPixelSizeChanged:
                if (ev.Window.WindowID == GetWindowId())
                {
                    var (width, height) = CurrentWindowPixelSize(ev.Window.Data1, ev.Window.Data2);
                    _viewportWidth = width;
                    _viewportHeight = height;
                    SafeRaiseResized(width, height);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private uint GetWindowId() => _window == nint.Zero ? 0u : SDL.GetWindowID(_window);

    private (int Width, int Height) CurrentWindowPixelSize(int fallbackWidth, int fallbackHeight)
    {
        if (_window != nint.Zero
            && SDL.GetWindowSizeInPixels(_window, out var width, out var height)
            && width > 0
            && height > 0)
        {
            return (width, height);
        }

        return (fallbackWidth, fallbackHeight);
    }

    private void DrainPendingRenderThreadActions()
    {
        while (_renderThreadActions.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput render-thread action");
            }
        }
    }

    private void ApplyWindowPlacementCore(int displayIndex, bool fullscreen, int? windowWidth, int? windowHeight)
    {
        if (_window == nint.Zero)
            return;

        uint displayId = 0;
        var displays = SDL.GetDisplays(out _);
        if (displays is { Length: > 0 })
        {
            var idx = Math.Clamp(displayIndex, 0, displays.Length - 1);
            displayId = displays[idx];
        }
        else
        {
            displayId = SDL.GetPrimaryDisplay();
        }

        if (displayId == 0 || !SDL.GetDisplayBounds(displayId, out var r))
            return;

        ApplyWindowPlacementCore(r.X, r.Y, r.W, r.H, fullscreen, windowWidth, windowHeight);
    }

    private void ScheduleWindowPlacement(Action placement)
    {
        lock (_windowPlacementLock)
        {
            if (!_configured)
            {
                // Last request wins before the native window exists. Project hosts normally choose
                // the display immediately after construction and the session configures it later.
                _pendingWindowPlacement = placement;
                return;
            }
        }

        InvokeOnRenderThread(placement);
    }

    private void ApplyPendingWindowPlacementCore()
    {
        Action? placement;
        lock (_windowPlacementLock)
        {
            placement = _pendingWindowPlacement;
            _pendingWindowPlacement = null;
        }

        placement?.Invoke();
    }

    private void ApplyWindowPlacementCore(
        int displayX,
        int displayY,
        int displayWidth,
        int displayHeight,
        bool fullscreen,
        int? windowWidth,
        int? windowHeight)
    {
        if (_window == nint.Zero || displayWidth <= 0 || displayHeight <= 0)
            return;

        if (fullscreen)
        {
            if (!SDL.SetWindowFullscreen(_window, false))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: leave fullscreen before placement failed: {0}", SDL.GetError());
            if (!SDL.SetWindowPosition(_window, displayX, displayY))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowPosition failed: {0}", SDL.GetError());
            if (!SDL.SetWindowSize(_window, displayWidth, displayHeight))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowSize failed: {0}", SDL.GetError());
            if (!SDL.SetWindowFullscreen(_window, true))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowFullscreen failed: {0}", SDL.GetError());
        }
        else
        {
            if (!SDL.SetWindowFullscreen(_window, false))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowFullscreen(false) failed: {0}", SDL.GetError());

            var ww = windowWidth ?? 960;
            var wh = windowHeight ?? 540;
            if (!SDL.SetWindowSize(_window, ww, wh))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowSize failed: {0}", SDL.GetError());

            var x = displayX + Math.Max(0, (displayWidth - ww) / 2);
            var y = displayY + Math.Max(0, (displayHeight - wh) / 2);
            if (!SDL.SetWindowPosition(_window, x, y))
                MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowPosition failed: {0}", SDL.GetError());
        }
    }

    private void ApplyWindowConstraintsCore()
    {
        if (_window == nint.Zero)
            return;

        bool lockAspect;
        bool lockResolution;
        float aspect;
        lock (_windowConstraintLock)
        {
            lockAspect = _windowAspectLocked;
            lockResolution = _windowResolutionLocked;
            aspect = _windowAspectRatio;
        }

        var minimumAspect = lockAspect ? aspect : 0f;
        var maximumAspect = lockAspect ? aspect : 0f;
        if (!SDL.SetWindowAspectRatio(_window, minimumAspect, maximumAspect))
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowAspectRatio failed: {0}", SDL.GetError());

        if (!SDL.SetWindowResizable(_window, !lockResolution))
            MediaDiagnostics.LogWarning("SDL3GLVideoOutput: SDL_SetWindowResizable failed: {0}", SDL.GetError());
    }

    private void SafeRaise(EventHandler? handler)
    {
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput event subscriber");
        }
    }

    private void SafeRaiseResized(int width, int height)
    {
        var handler = Resized;
        if (handler is null) return;
        try { handler.Invoke(this, (width, height)); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3GLVideoOutput Resized subscriber");
        }
    }
}
