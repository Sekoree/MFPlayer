using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;
using S.Media.Core;

namespace S.Media.Avalonia;

/// <summary>
/// Avalonia embedded video output based on OpenGlControlBase.
/// Host apps place this control in their visual tree and wire channels via Mixer.
/// </summary>
public class AvaloniaOpenGlVideoEndpoint
    : OpenGlControlBase, IPullVideoEndpoint, IClockCapableEndpoint, IVideoColorMatrixReceiver, IVideoPresentationClockOverridable
{
    private static readonly ILogger Log = AvaloniaVideoLogging.GetLogger(nameof(AvaloniaOpenGlVideoEndpoint));

    string IMediaEndpoint.Name => Name ?? nameof(AvaloniaOpenGlVideoEndpoint);

    public readonly record struct DiagnosticsSnapshot(
        long RenderCalls,
        long PresentedFrames,
        long BlackFrames,
        long RenderExceptions,
        long InitCalls,
        long DeinitCalls,
        long TextureUploads,
        long TextureReuseDraws,
        long CatchupSkips);

    private readonly Lock _stateLock = new();
    private AvaloniaGlRenderer? _renderer;
    private VideoPtsClock? _clock;
    private volatile IMediaClock? _presentationClockOverride;
    private long _presentationClockOriginTicks;
    private int _hasPresentationClockOrigin;
    // HUD-only drift baseline for cross-domain clock/PTS pairs (e.g. PortAudio
    // clock vs absolute NDI PTS). Uses atomic/volatile publication because
    // public API paths can reset from non-render threads.
    private long _hudDriftClockOriginTicks;
    private long _hudDriftPtsOriginTicks;
    private int _hasHudDriftOrigin;
    private VideoFormat _outputFormat;
    private VideoFormat _inputFormat;
    private bool _hasYuvHintsOverride;
    private bool _yuvBt709;
    private bool _yuvLimitedRange;
    private YuvColorMatrix _yuvColorMatrix = YuvColorMatrix.Auto;
    private volatile int _scalingFilter = (int)ScalingFilter.Bicubic;
    private bool _isOpen;
    private bool _isRunning;
    private bool _disposed;
    private long _renderPacingIntervalTicks;
    private long _nextRenderRequestDueTicks;
    private int _renderRequestScheduled;

    private long _renderCalls;
    private long _presentedFrames;
    private long _blackFrames;
    private long _renderExceptions;
    private long _initCalls;
    private long _deinitCalls;
    private long _textureUploads;
    private long _textureReuseDraws;
    private long _catchupSkips;

    // HUD FPS measurement (render thread only).
    private long _hudLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private int _hudFrameCount;
    private double _hudFps;
    private long _hudLastUniqueTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _hudLastUniqueFrames;
    private double _hudUniqueFps;

    private bool _hasUploadedFrame;
    private int _lastUploadedWidth;
    private int _lastUploadedHeight;
    private TimeSpan _lastUploadedPts;
    private ReadOnlyMemory<byte> _lastUploadedData;
    // §3.33 / S3, S12, A2 — identity key for texture-reuse. `ReadOnlyMemory<byte>.Equals`
    // is structural (array ref + offset + length), so after an ArrayPool rental is
    // returned and a coincidentally-identical array is re-rented the old key can
    // falsely match and skip the upload → stale texture. Keying on the MemoryOwner
    // reference (identity-compared via ReferenceEquals) plus Pts/W/H rules this out.
    private IDisposable? _lastUploadedMemoryOwner;

    // Per-frame auto-hint tracking (render thread only — no lock needed).
    private YuvColorMatrix _lastAutoMatrix = YuvColorMatrix.Auto;
    private YuvColorRange  _lastAutoRange  = YuvColorRange.Auto;

    // §3.34 / A5 — pending-change flag for user-pinned YUV hints. Setters
    // mutate the state fields from any thread and flip this to 1; the
    // render tick picks it up under the GL context and forwards to the
    // renderer. Avoids touching `_renderer` off-thread.
    private int _yuvHintsDirty;

    // §3.40g / A6 — stored as long ticks and accessed via Volatile.Read/Write so
    // cross-thread setters (UI thread ↔ render thread) cannot observe a torn
    // TimeSpan on 32-bit runtimes.
    private long _catchupLagThresholdTicks = TimeSpan.FromMilliseconds(45).Ticks;
    private int _maxCatchupPullsPerRender = 6;

    // §3.40k / A13 — cached render-scaling factor. Stashed on the UI thread via
    // OnPropertyChanged(BoundsProperty) / OnAttachedToVisualTree so the render
    // thread can read it without walking VisualRoot each frame (which is
    // technically UI-thread-only). Stored as the IEEE-754 bit pattern of the
    // double to allow tear-free cross-thread publication via Interlocked.
    private long _renderScalingBits = BitConverter.DoubleToInt64Bits(1.0);

    // §3.36 / A10 — "live" mode forces a render tick every vsync (for push endpoints
    // that tee from an external source where no pull callback upload happens).
    /// <summary>
    /// When <see langword="true"/>, the control requests another render tick at
    /// the end of every <c>OnOpenGlRender</c> call even if no frame was uploaded
    /// — useful for live/push scenarios. Default <see langword="false"/>: ticks
    /// are only rescheduled after a successful upload (§3.36 / A1, A10, A14).
    /// </summary>
    public bool LiveMode { get; set; }

    private readonly Lock _cloneLock = new();
    private AvaloniaOpenGlVideoCloneEndpoint[] _clones = [];

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        RenderCalls: Interlocked.Read(ref _renderCalls),
        PresentedFrames: Interlocked.Read(ref _presentedFrames),
        BlackFrames: Interlocked.Read(ref _blackFrames),
        RenderExceptions: Interlocked.Read(ref _renderExceptions),
        InitCalls: Interlocked.Read(ref _initCalls),
        DeinitCalls: Interlocked.Read(ref _deinitCalls),
        TextureUploads: Interlocked.Read(ref _textureUploads),
        TextureReuseDraws: Interlocked.Read(ref _textureReuseDraws),
        CatchupSkips: Interlocked.Read(ref _catchupSkips));

    public VideoFormat OutputFormat => _outputFormat;

    /// <summary>
    /// When <see langword="true"/>, a diagnostic HUD overlay is rendered on top
    /// of the video, including clock source and frame drift.
    /// Thread-safe: can be toggled from any thread at any time.
    /// </summary>
    public bool ShowHud { get; set; }

    /// <summary>
    /// When <see langword="true"/>, re-render requests are paced to the current
    /// input FPS hint instead of continuously ticking at display cadence.
    /// This reduces CPU/GPU work for low-FPS sources. Default is
    /// <see langword="false"/>.
    /// </summary>
    public bool LimitRenderToInputFps { get; set; }

    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    public bool IsRunning => _isRunning;


    // ── IPullVideoEndpoint ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public IVideoPresentCallback? PresentCallback { get; set; }

    // ── IVideoEndpoint (push — unused for pull endpoints) ───────────────────

    void IVideoEndpoint.ReceiveFrame(in VideoFrameHandle handle) { _ = handle; }

    // ── IClockCapableEndpoint ───────────────────────────────────────────────

    IMediaClock IClockCapableEndpoint.Clock => Clock;

    /// <summary>
    /// Overrides the YUV color matrix and range used by the GPU shaders.
    /// Call this after Open() once you know the stream's color metadata (e.g. from IVideoColorMatrixHint).
    /// </summary>
    public void SetYuvHints(bool bt709, bool limitedRange)
    {
        _hasYuvHintsOverride = true;
        _yuvBt709 = bt709;
        _yuvLimitedRange = limitedRange;
        _yuvColorMatrix = bt709 ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601;
        _lastAutoMatrix = YuvColorMatrix.Auto;
        _lastAutoRange = YuvColorRange.Auto;
        // §3.34 / A5 — defer the renderer write to the next render tick
        // (which owns the GL context). The fields above are read by the
        // render thread under `_yuvHintsDirty`-gated apply.
        Interlocked.Exchange(ref _yuvHintsDirty, 1);
    }

    /// <summary>
    /// Overrides the YUV color matrix and range using the full <see cref="YuvColorMatrix"/> enum
    /// (supports BT.601, BT.709, BT.2020 and Auto).
    /// </summary>
    public void SetYuvHints(YuvColorMatrix matrix, bool limitedRange)
    {
        _hasYuvHintsOverride = true;
        _yuvColorMatrix = matrix;
        _yuvBt709 = matrix == YuvColorMatrix.Bt709;
        _yuvLimitedRange = limitedRange;
        _lastAutoMatrix = YuvColorMatrix.Auto;
        _lastAutoRange = YuvColorRange.Auto;
        // §3.34 / A5 — defer the renderer write to the next render tick.
        Interlocked.Exchange(ref _yuvHintsDirty, 1);
    }

    /// <summary>Resets YUV hints to auto-detect from frame resolution.</summary>
    public void ResetYuvHints()
    {
        _hasYuvHintsOverride = false;
        _lastAutoMatrix = YuvColorMatrix.Auto;
        _lastAutoRange = YuvColorRange.Auto;
        // §3.34 / A5 — defer. The render tick applies "Auto" via
        // ApplyAutoYuvHintsIfNeeded on the next frame; we additionally
        // flip the dirty flag so an explicit ResetYuvHintsToAuto call
        // fires even before the first frame arrives.
        Interlocked.Exchange(ref _yuvHintsDirty, 1);
    }

    /// <summary>YUV color range used by the GL shaders. Equivalent to calling <see cref="SetYuvHints(YuvColorMatrix,bool)"/>.</summary>
    public YuvColorRange YuvColorRange
    {
        get => _yuvLimitedRange ? YuvColorRange.Limited : YuvColorRange.Full;
        set => SetYuvHints(_yuvColorMatrix, YuvAutoPolicy.ResolveRange(value) == YuvColorRange.Limited);
    }

    /// <summary>YUV color matrix used by the GL shaders. Equivalent to calling <see cref="SetYuvHints(YuvColorMatrix,bool)"/>.</summary>
    public YuvColorMatrix YuvColorMatrix
    {
        get => _yuvColorMatrix;
        set => SetYuvHints(value, _yuvLimitedRange);
    }

    /// <summary>
    /// §5.3 — receive color-matrix hint from the source channel at route-creation
    /// time. Preserves any explicit value the caller already set (an <c>Auto</c>
    /// hint is ignored, and we only overwrite the current value when it is
    /// itself <c>Auto</c>).
    /// </summary>
    public void ApplyColorMatrixHint(YuvColorMatrix matrix, YuvColorRange range)
    {
        if (_hasYuvHintsOverride) return; // user already pinned an explicit value
        var newMatrix = matrix == YuvColorMatrix.Auto ? _yuvColorMatrix : matrix;
        var resolvedRange = YuvAutoPolicy.ResolveRange(range);
        var newLimited = range == YuvColorRange.Auto
            ? _yuvLimitedRange
            : resolvedRange == YuvColorRange.Limited;
        if (newMatrix != _yuvColorMatrix || newLimited != _yuvLimitedRange)
        {
            _lastAutoMatrix = newMatrix;
            _lastAutoRange = newLimited ? YuvColorRange.Limited : YuvColorRange.Full;
            _yuvColorMatrix = newMatrix;
            _yuvLimitedRange = newLimited;
            // §3.34 / A5 — defer the renderer write to the next render tick.
            Interlocked.Exchange(ref _yuvHintsDirty, 1);
        }
    }

    /// <summary>
    /// Scaling filter applied during final presentation.
    /// Defaults to <see cref="ScalingFilter.Bicubic"/> for broadcast-quality monitoring.
    /// Thread-safe; the render thread picks up the new value on the next frame.
    /// </summary>
    public ScalingFilter ScalingFilter
    {
        get => (ScalingFilter)_scalingFilter;
        set
        {
            _scalingFilter = (int)value;
            if (_renderer != null) _renderer.ScalingFilter = value;
        }
    }

    /// <summary>
    /// Updates the source-format hint used by HUD diagnostics.
    /// Useful for live sources whose format/FPS can change at runtime.
    /// </summary>
    public void SetInputFormatHint(VideoFormat format)
    {
        lock (_stateLock)
            _inputFormat = format;
    }

    /// <summary>
    /// Overrides the render-loop presentation clock.
    /// Set this to an audio hardware clock to keep video pacing aligned with audio.
    /// </summary>
    public void OverridePresentationClock(IMediaClock? clock)
    {
        _presentationClockOverride = clock;
        // Clear ticks first then flag so observers can never see a stale origin.
        Interlocked.Exchange(ref _presentationClockOriginTicks, 0);
        Interlocked.Exchange(ref _hasPresentationClockOrigin, 0);
        Interlocked.Exchange(ref _hudDriftClockOriginTicks, 0);
        Interlocked.Exchange(ref _hudDriftPtsOriginTicks, 0);
        Interlocked.Exchange(ref _hasHudDriftOrigin, 0);
    }

    /// <summary>
    /// Frames older than (clock - threshold) are eligible for per-render catch-up skipping.
    /// Defaults to 45 ms.
    /// </summary>
    public TimeSpan CatchupLagThreshold
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _catchupLagThresholdTicks));
        set
        {
            long ticks = value <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1).Ticks
                : value.Ticks;
            Volatile.Write(ref _catchupLagThresholdTicks, ticks);
        }
    }

    /// <summary>
    /// Maximum additional mixer pulls per render call when trying to catch up.
    /// Defaults to 6.
    /// </summary>
    public int MaxCatchupPullsPerRender
    {
        get => _maxCatchupPullsPerRender;
        set => _maxCatchupPullsPerRender = value < 0 ? 0 : value;
    }

    /// <summary>
    /// Single-step factory (§1.4 / CH8 / P1): constructs an
    /// <see cref="AvaloniaOpenGlVideoEndpoint"/>, calls <see cref="Open"/>
    /// with the provided dimensions, and returns a ready-to-register instance
    /// whose <see cref="Clock"/> is valid from the moment the caller receives
    /// it. The returned instance still needs to be placed in an Avalonia
    /// visual tree (<c>OpenGlControlBase</c>) for actual rendering.
    /// </summary>
    /// <param name="width">Logical width hint for the output pipeline.</param>
    /// <param name="height">Logical height hint for the output pipeline.</param>
    /// <param name="format">
    /// Preferred output <see cref="VideoFormat"/>. If <see langword="null"/>,
    /// defaults to a <paramref name="width"/>×<paramref name="height"/> BGRA32
    /// surface at 30 fps — the endpoint auto-negotiates a better pixel format
    /// at route creation time via <see cref="IFormatCapabilities{TFormat}"/>.
    /// </param>
    public static AvaloniaOpenGlVideoEndpoint Create(
        int          width  = 1280,
        int          height = 720,
        VideoFormat? format = null)
    {
        var ep = new AvaloniaOpenGlVideoEndpoint();
        ep.Open(title: string.Empty, width, height, format ?? VideoFormat.Create(width, height, PixelFormat.Bgra32, 30));
        return ep;
    }

    /// <summary>
    /// Opens the output pipeline. The title parameter is ignored for embedded controls.
    /// </summary>
    public void Open(string title, int width, int height, VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        {
            if (_isOpen)
                throw new InvalidOperationException("Output is already open.");

            _outputFormat = format;
            lock (_stateLock)
                _inputFormat = format;
            _clock = new VideoPtsClock(frameRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);
            _isOpen = true;
        }

        Log.LogInformation("Opened AvaloniaOpenGlVideoEndpoint: {Width}x{Height} px={PixelFormat}, fps={FrameRate}",
            _outputFormat.Width, _outputFormat.Height, _outputFormat.PixelFormat, _outputFormat.FrameRate);

        // Keep the control stretchable in layout; do not pin it to source pixel size.
        Width = double.NaN;
        Height = double.NaN;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning)
            return Task.CompletedTask;
        if (!_isOpen)
            throw new InvalidOperationException("Call Open() before Start.");

        _clock!.Start();
        _isRunning = true;
        Volatile.Write(ref _renderPacingIntervalTicks, 0);
        Volatile.Write(ref _nextRenderRequestDueTicks, 0);
        Interlocked.Exchange(ref _renderRequestScheduled, 0);
        Interlocked.Exchange(ref _presentationClockOriginTicks, 0);
        Interlocked.Exchange(ref _hasPresentationClockOrigin, 0);
        Interlocked.Exchange(ref _hudDriftClockOriginTicks, 0);
        Interlocked.Exchange(ref _hudDriftPtsOriginTicks, 0);
        Interlocked.Exchange(ref _hasHudDriftOrigin, 0);
        Log.LogInformation("AvaloniaOpenGlVideoEndpoint started");
        ScheduleNextFrameRendering(forceImmediate: true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        Log.LogInformation("Stopping AvaloniaOpenGlVideoEndpoint");
        _isRunning = false;
        _clock?.Stop();
        Volatile.Write(ref _renderPacingIntervalTicks, 0);
        Volatile.Write(ref _nextRenderRequestDueTicks, 0);
        Interlocked.Exchange(ref _renderRequestScheduled, 0);
        // Release the last-uploaded frame reference so the ArrayPool rental can be returned.
        _hasUploadedFrame = false;
        _lastUploadedData = default;
        _lastUploadedMemoryOwner = null;
        return Task.CompletedTask;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Interlocked.Increment(ref _initCalls);
        _renderer ??= new AvaloniaGlRenderer();
        _renderer.Initialise(gl);
        _renderer.ScalingFilter = (ScalingFilter)_scalingFilter;
        if (_hasYuvHintsOverride)
            _renderer.SetYuvHints(_yuvColorMatrix, _yuvLimitedRange);
        else
            ApplyAutoYuvHintsIfNeeded(_outputFormat.Width, _outputFormat.Height);
        _hasUploadedFrame = false;
        _lastUploadedData = default;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Interlocked.Increment(ref _deinitCalls);
        // §3.40e / S11 — wrap glDelete* in try/catch. The renderer's Dispose
        // issues GL deletions; if we hit a driver-level fault the exception
        // must not escape and prevent `_renderer = null`, otherwise a later
        // re-init would leak state.
        try { _renderer?.Dispose(); }
        catch (Exception ex) { Log.LogWarning(ex, "Renderer dispose threw in OnOpenGlDeinit"); }
        _renderer = null;
        _hasUploadedFrame = false;
        _lastUploadedData = default;
        _lastUploadedMemoryOwner = null;
    }

    protected override void OnOpenGlLost()
    {
        // §3.40e / S11 — same guard as OnOpenGlDeinit.
        try { _renderer?.Dispose(); }
        catch (Exception ex) { Log.LogWarning(ex, "Renderer dispose threw in OnOpenGlLost"); }
        _renderer = null;
        _hasUploadedFrame = false;
        _lastUploadedData = default;
        _lastUploadedMemoryOwner = null;
        lock (_stateLock)
            _inputFormat = _outputFormat;
        // §3.35 / A4, A7 — auto-hint tracking must return to `Auto` so the next
        // OnOpenGlInit re-applies resolved hints instead of believing they are
        // already live on a brand-new renderer.
        _lastAutoMatrix = YuvColorMatrix.Auto;
        _lastAutoRange  = YuvColorRange.Auto;
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Increment(ref _renderCalls);

        if (_renderer == null || _clock == null)
            return;

        // §3.40k / A13 — read the UI-thread-stashed render-scaling factor
        // instead of touching VisualRoot.RenderScaling (UI-thread-only).
        double scale = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _renderScalingBits));
        int viewportWidth = (int)Math.Max(1, Math.Round(Bounds.Width * scale));
        int viewportHeight = (int)Math.Max(1, Math.Round(Bounds.Height * scale));

        // §3.34 / A5 — apply any queued user-pinned YUV hints under the GL
        // context. `ApplyAutoYuvHintsIfNeeded` below handles the Auto path.
        if (Interlocked.Exchange(ref _yuvHintsDirty, 0) == 1)
            ApplyPendingYuvHints();

        // §3.36 — track whether we actually uploaded a frame this tick so the
        // finally block can decide whether to re-arm the render timer.
        bool uploadedThisTick = false;
        TimeSpan clockPosition = TimeSpan.Zero;
        TimeSpan hudClockPosition = TimeSpan.Zero;
        TimeSpan hudDrift = TimeSpan.Zero;
        string hudClockName = ((_presentationClockOverride ?? _clock)?.GetType().Name) ?? "n/a";
        VideoFrame hudFrame = default;
        bool hasHudFrame = false;

        try
        {
            if (!_isRunning)
            {
                _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
                Interlocked.Increment(ref _blackFrames);
                return;
            }

            var (clock, resolvedClockPosition, hasOverride, resolvedHudClockPosition) = ResolvePresentationClock();
            clockPosition = resolvedClockPosition;
            hudClockPosition = resolvedHudClockPosition;
            hudClockName = hasOverride
                ? $"{clock.GetType().Name} (override)"
                : clock.GetType().Name;

            if (TryPullFrameWithCatchUp(clockPosition, out VideoFrame vf))
            {
                PresentFrame(in vf, fb, viewportWidth, viewportHeight, hasOverride);
                uploadedThisTick = true;
                hudDrift = hasOverride
                    ? ComputeHudRelativeDrift(clockPosition, vf.Pts)
                    : clockPosition - vf.Pts;
                hudFrame = vf;
                hasHudFrame = true;
            }
            else
            {
                _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
                Interlocked.Increment(ref _blackFrames);
                if (_hasUploadedFrame)
                {
                    hudDrift = hasOverride
                        ? ComputeHudRelativeDrift(clockPosition, _lastUploadedPts)
                        : clockPosition - _lastUploadedPts;
                }
            }

            if (ShowHud)
                DrawHudOverlay(fb, viewportWidth, viewportHeight, hudClockPosition, hudClockName, hasHudFrame, in hudFrame, hudDrift);
        }
        catch (Exception ex)
        {
            long ec = Interlocked.Increment(ref _renderExceptions);
            // §3.40d / S10 — tag the exception type so the rate-limited log
            // samples (1/2/3 then every 100th) can be grouped per failure mode.
            if (ec <= 3 || ec % 100 == 0)
                Log.LogError(ex, "Render exception [{ExceptionType}] (count={Count})",
                    ex.GetType().Name, ec);
        }
        finally
        {
            // §3.36 / A1, A10, A14 — only re-arm the render timer when an
            // upload actually happened (or LiveMode is on). The previous
            // unconditional RequestNextFrameRendering caused a busy-loop when
            // the mixer had no new frame, pinning the UI thread to 100%.
            if (_isRunning)
            {
                if (uploadedThisTick || LiveMode)
                {
                    ScheduleNextFrameRendering();
                }
                else if (!_hasUploadedFrame)
                {
                    // Bootstrap pull mode: if the very first frame isn't ready on
                    // the initial tick, keep polling at a low cadence so playback
                    // can start once decode produces data (instead of dead-starting
                    // at clock/source = 0 forever).
                    QueueRenderRequest(TimeSpan.FromMilliseconds(5));
                }
                else
                {
                    // Safety net: a render tick produced no upload even though
                    // frames have been uploaded before. This can happen after a
                    // clock switch (drift re-seed), a transient render exception,
                    // or a subscription race. Poll at a low cadence so the loop
                    // recovers instead of permanently stalling.
                    QueueRenderRequest(TimeSpan.FromMilliseconds(100));
                }
            }
        }
    }

    private void DrawHudOverlay(
        int fb,
        int viewportWidth,
        int viewportHeight,
        TimeSpan hudClockPosition,
        string clockName,
        bool hasFrame,
        in VideoFrame frame,
        TimeSpan drift)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = (double)(now - _hudLastTimestamp) / System.Diagnostics.Stopwatch.Frequency;
        _hudFrameCount++;
        if (elapsed >= 1.0)
        {
            _hudFps = _hudFrameCount / elapsed;
            _hudFrameCount = 0;
            _hudLastTimestamp = now;

            long uniqueNow = Interlocked.Read(ref _textureUploads);
            double uniqueElapsed = (double)(now - _hudLastUniqueTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            if (uniqueElapsed > 0.0)
                _hudUniqueFps = (uniqueNow - _hudLastUniqueFrames) / uniqueElapsed;
            _hudLastUniqueFrames = uniqueNow;
            _hudLastUniqueTimestamp = now;
        }

        VideoFormat inputFormatSnapshot;
        lock (_stateLock)
        {
            if (hasFrame)
            {
                int fpsNum = _inputFormat.FrameRateNumerator > 0 ? _inputFormat.FrameRateNumerator : 30000;
                int fpsDen = _inputFormat.FrameRateDenominator > 0 ? _inputFormat.FrameRateDenominator : 1001;
                _inputFormat = new VideoFormat(frame.Width, frame.Height, frame.PixelFormat, fpsNum, fpsDen);
            }
            inputFormatSnapshot = _inputFormat;
        }

        int inputWidth = hasFrame ? frame.Width : (_lastUploadedWidth > 0 ? _lastUploadedWidth : inputFormatSnapshot.Width);
        int inputHeight = hasFrame ? frame.Height : (_lastUploadedHeight > 0 ? _lastUploadedHeight : inputFormatSnapshot.Height);
        PixelFormat inputPixelFormat = hasFrame
            ? frame.PixelFormat
            : (_hasUploadedFrame ? inputFormatSnapshot.PixelFormat : _outputFormat.PixelFormat);

        double displayFps = _hudFps;
        if (displayFps < 0 || double.IsNaN(displayFps) || double.IsInfinity(displayFps))
            displayFps = 0;
        double contentFps = _hudUniqueFps;
        if (contentFps < 0 || double.IsNaN(contentFps) || double.IsInfinity(contentFps))
            contentFps = 0;
        double inputFps = inputFormatSnapshot.FrameRate;

        var stats = new HudStats
        {
            Width = _outputFormat.Width,
            Height = _outputFormat.Height,
            PixelFormat = _outputFormat.PixelFormat,
            Fps = displayFps,
            InputWidth = inputWidth,
            InputHeight = inputHeight,
            InputFps = inputFps,
            InputPixelFormat = inputPixelFormat,
            PresentedFrames = Interlocked.Read(ref _presentedFrames),
            BlackFrames = Interlocked.Read(ref _blackFrames),
            DroppedFrames = Interlocked.Read(ref _catchupSkips),
            ClockPosition = hudClockPosition,
            ClockName = clockName,
            Drift = drift,
            ExtraLine1 = contentFps > 0
                ? $"fps content: {contentFps:F1}  display: {displayFps:F1}"
                : $"fps content: n/a  display: {displayFps:F1}",
            ExtraLine2 = $"reuse: {Interlocked.Read(ref _textureReuseDraws)}  uploads: {Interlocked.Read(ref _textureUploads)}",
        };

        _renderer!.DrawHud(stats.ToLines(), fb, viewportWidth, viewportHeight);
    }

    /// <summary>
    /// §3.40h / A8 — resolve the presentation clock and compute the tick's
    /// clock position. The raw clock position is returned for scheduling, while
    /// a HUD-only display position is normalised to 0 for override clocks so
    /// absolute domains remain readable in the overlay.
    /// Returns the clock, raw position, override flag, and HUD display position.
    /// </summary>
    private (IMediaClock Clock, TimeSpan Position, bool HasOverride, TimeSpan HudPosition) ResolvePresentationClock()
    {
        var externalClock = _presentationClockOverride;
        var presentationClock = externalClock ?? _clock;
        if (presentationClock == null)
            throw new InvalidOperationException("Presentation clock is not available.");

        var clockPosition = presentationClock.Position;
        var hudClockPosition = clockPosition;
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

        return (presentationClock, clockPosition, externalClock != null, hudClockPosition);
    }

    /// <summary>
    /// §3.40h / A8 — pull the next frame from <see cref="PresentCallback"/>
    /// and, if it is behind the clock, walk forward up to
    /// <see cref="_maxCatchupPullsPerRender"/> times to catch up. Returns
    /// <see langword="false"/> when no frame is available for this tick.
    /// </summary>
    private bool TryPullFrameWithCatchUp(TimeSpan clockPosition, out VideoFrame vf)
    {
        vf = default;

        var presentCb = PresentCallback;
        if (presentCb is null)
            return false;

        if (!presentCb.TryPresentNext(clockPosition, out VideoFrame cbFrame))
            return false;

        vf = cbFrame;

        // If decode/render falls behind, skip stale frames up to a bounded budget.
        for (int i = 0; i < _maxCatchupPullsPerRender; i++)
        {
            if (vf.Pts + TimeSpan.FromTicks(Volatile.Read(ref _catchupLagThresholdTicks)) >= clockPosition)
                break;

            if (!presentCb.TryPresentNext(clockPosition, out VideoFrame nvf))
                break;

            if (nvf.Pts == vf.Pts &&
                nvf.Width == vf.Width &&
                nvf.Height == vf.Height &&
                nvf.Data.Equals(vf.Data))
                break;

            vf = nvf;
            Interlocked.Increment(ref _catchupSkips);
        }

        return true;
    }

    /// <summary>
    /// §3.40h / A8 — upload (or reuse) the texture for <paramref name="vf"/>
    /// and advance the internal <see cref="VideoPtsClock"/> when the
    /// presentation clock override is not in control. Renderer natively
    /// handles all GPU-uploadable formats (Rgba32, Bgra32, Nv12, Yuv420p,
    /// Yuv422p10, Uyvy422, P010, Yuv444p, Gray8); unknown formats render
    /// black upstream.
    /// </summary>
    private void PresentFrame(in VideoFrame vf, int fb, int viewportWidth, int viewportHeight, bool hasOverride)
    {
        ApplyAutoYuvHintsIfNeeded(vf.Width, vf.Height);

        // §3.33 / A2 — identity-based reuse gate: ReferenceEquals on MemoryOwner
        // plus Pts/W/H rules out the ArrayPool-rental false-match on
        // `ReadOnlyMemory<byte>.Equals` (structural over array ref + offset + length).
        bool sameAsUploaded = _hasUploadedFrame &&
                              vf.Width == _lastUploadedWidth &&
                              vf.Height == _lastUploadedHeight &&
                              vf.Pts == _lastUploadedPts &&
                              ReferenceEquals(vf.MemoryOwner, _lastUploadedMemoryOwner);

        if (sameAsUploaded)
        {
            _renderer!.DrawLastTexture(fb, viewportWidth, viewportHeight);
            Interlocked.Increment(ref _textureReuseDraws);
        }
        else
        {
            _renderer!.UploadAndDraw(vf, fb, viewportWidth, viewportHeight);
            _hasUploadedFrame = true;
            _lastUploadedWidth = vf.Width;
            _lastUploadedHeight = vf.Height;
            _lastUploadedPts = vf.Pts;
            _lastUploadedData = vf.Data;
            _lastUploadedMemoryOwner = vf.MemoryOwner;
            Interlocked.Increment(ref _textureUploads);
        }

        if (!hasOverride)
            _clock!.UpdateFromFrame(vf.Pts);
        Interlocked.Increment(ref _presentedFrames);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // §3.40k — stash the current render-scaling once on attach; further
        // updates arrive via OnPropertyChanged(BoundsProperty).
        UpdateRenderScalingFromUIThread();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ = StopAsync();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            // §3.40k — keep the cached render-scaling in sync with UI state.
            UpdateRenderScalingFromUIThread();
            // §3.36 — request a render tick on layout changes so the viewport
            // is re-drawn (the conditional re-arm in OnOpenGlRender's finally
            // would otherwise never fire without an upload).
            if (_isRunning)
                ScheduleNextFrameRendering(forceImmediate: true);
        }
    }

    private void ScheduleNextFrameRendering(bool forceImmediate = false)
    {
        if (!_isRunning || _disposed)
            return;

        if (forceImmediate || !LimitRenderToInputFps || !TryGetInputFrameIntervalTicks(out long intervalTicks))
        {
            Volatile.Write(ref _renderPacingIntervalTicks, 0);
            Volatile.Write(ref _nextRenderRequestDueTicks, 0);
            QueueRenderRequest(TimeSpan.Zero);
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long previousInterval = Volatile.Read(ref _renderPacingIntervalTicks);
        if (previousInterval != intervalTicks)
        {
            Volatile.Write(ref _renderPacingIntervalTicks, intervalTicks);
            Volatile.Write(ref _nextRenderRequestDueTicks,
                RenderCadenceHelper.InitialDue(now, intervalTicks, immediateFirstTick: false));
        }

        long due = Volatile.Read(ref _nextRenderRequestDueTicks);
        if (due <= 0)
            due = RenderCadenceHelper.InitialDue(now, intervalTicks, immediateFirstTick: false);
        due = RenderCadenceHelper.NormalizeDue(
            due, now, intervalTicks, RenderCadenceHelper.LateTickPolicy.WaitForNextBoundary);
        TimeSpan delay = RenderCadenceHelper.ComputeDelay(now, due);

        if (QueueRenderRequest(delay))
            Volatile.Write(ref _nextRenderRequestDueTicks, due + intervalTicks);
    }

    private bool QueueRenderRequest(TimeSpan delay)
    {
        if (Interlocked.CompareExchange(ref _renderRequestScheduled, 1, 0) != 0)
            return false;

        if (delay <= TimeSpan.Zero)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _renderRequestScheduled, 0);
                if (_isRunning && !_disposed)
                    RequestNextFrameRendering();
            }, DispatcherPriority.Render);
            return true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                long due = System.Diagnostics.Stopwatch.GetTimestamp() +
                           (long)Math.Ceiling(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                while (true)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long remaining = due - now;
                    if (remaining <= 0)
                        break;

                    double remainingMs = remaining * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (remainingMs >= 2.0)
                    {
                        // Leave a small tail for sub-ms spin/yield correction so
                        // timer quantization does not systematically run fast.
                        double sleepMs = Math.Min(5.0, Math.Max(1.0, remainingMs - 1.0));
                        await Task.Delay(TimeSpan.FromMilliseconds(sleepMs)).ConfigureAwait(false);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref _renderRequestScheduled, 0);
                    if (_isRunning && !_disposed)
                        RequestNextFrameRendering();
                }, DispatcherPriority.Render);
            }
        });

        return true;
    }

    private bool TryGetInputFrameIntervalTicks(out long intervalTicks)
    {
        VideoFormat format;
        lock (_stateLock)
            format = _inputFormat;

        return RenderCadenceHelper.TryGetFrameIntervalTicks(format.FrameRate, out intervalTicks);
    }

    private void UpdateRenderScalingFromUIThread()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = 1.0;
        Interlocked.Exchange(ref _renderScalingBits, BitConverter.DoubleToInt64Bits(scale));
    }

    public void Dispose()
    {
        if (_disposed) return;

        // §3.40j / A12 — Dispose during visual-tree attachment races the
        // compositor's render tick. Require the user to detach the control
        // first; fail fast instead of crashing the compositor thread.
        if (VisualRoot is not null)
            throw new InvalidOperationException(
                "Dispose AvaloniaOpenGlVideoEndpoint only after detaching it from its parent visual tree (§3.40j / A12).");

        _disposed = true;

        Log.LogInformation("Disposing AvaloniaOpenGlVideoEndpoint: presented={Presented}, black={Black}, uploads={Uploads}, catchupSkips={CatchupSkips}, renderExceptions={RenderExceptions}",
            Interlocked.Read(ref _presentedFrames), Interlocked.Read(ref _blackFrames),
            Interlocked.Read(ref _textureUploads), Interlocked.Read(ref _catchupSkips),
            Interlocked.Read(ref _renderExceptions));

        lock (_cloneLock)
        {
            for (int i = 0; i < _clones.Length; i++)
                _clones[i].Dispose();
            _clones = [];
        }

        _ = StopAsync();
        // §3.40i / A11 — do NOT dispose _renderer here. We asserted above that
        // the control is detached from the visual tree, which will have queued
        // (or already fired) OnOpenGlDeinit; that callback is the only place
        // GL deletions run under the correct context. Disposing here would
        // either race the compositor or run glDelete* without a current context.
        _clock?.Dispose();
    }

    /// <summary>
    /// Creates a clone endpoint that can be registered on the router and wired
    /// into a per-source route, mirroring this parent's video output into a
    /// secondary control. See `Doc/Clone-Sinks.md` for the full wiring contract
    /// (§3.40a / S2, S4).
    /// <para>
    /// The clone is a standalone <see cref="IVideoEndpoint"/> — the router
    /// delivers frames to it through the normal
    /// <see cref="IVideoEndpoint.ReceiveFrame(in VideoFrameHandle)"/> path;
    /// this parent does <b>not</b> tee frames internally. The returned clone
    /// is tracked here so a parent <see cref="Dispose"/> cascades disposal as
    /// a safety net; callers with finer control should
    /// <c>RemoveRoute</c> + <c>UnregisterEndpoint</c> + <c>clone.Dispose()</c>
    /// in that order before disposing the parent.
    /// </para>
    /// </summary>
    public AvaloniaOpenGlVideoCloneEndpoint CreateCloneSink(string? name = null)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Call Open() before creating clone sinks.");

        var clone = new AvaloniaOpenGlVideoCloneEndpoint(name);
        lock (_cloneLock)
        {
            var old = _clones;
            var neo = new AvaloniaOpenGlVideoCloneEndpoint[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = clone;
            _clones = neo;
        }

        return clone;
    }

    private void ApplyAutoYuvHintsIfNeeded(int width, int height)
    {
        if (_renderer == null || _hasYuvHintsOverride)
            return;

        int resolvedWidth = width > 0 ? width : 1280;
        int resolvedHeight = height > 0 ? height : 720;
        var resolvedRange = YuvAutoPolicy.ResolveRange(YuvColorRange.Auto);
        var resolvedMatrix = YuvAutoPolicy.ResolveMatrix(YuvColorMatrix.Auto, resolvedWidth, resolvedHeight);

        if (_lastAutoRange == resolvedRange && _lastAutoMatrix == resolvedMatrix)
            return;

        _renderer.SetYuvHints(resolvedMatrix, resolvedRange == YuvColorRange.Limited);
        _lastAutoRange = resolvedRange;
        _lastAutoMatrix = resolvedMatrix;
    }

    /// <summary>
    /// §3.34 / A5 — applies user-pinned YUV hints to the renderer under the
    /// GL context. Called at the top of <see cref="OnOpenGlRender"/> when a
    /// setter has flipped <see cref="_yuvHintsDirty"/>. Auto-mode hints are
    /// handled separately by <see cref="ApplyAutoYuvHintsIfNeeded"/>.
    /// </summary>
    private void ApplyPendingYuvHints()
    {
        if (_renderer == null)
            return;

        if (_hasYuvHintsOverride)
            _renderer.SetYuvHints(_yuvColorMatrix, _yuvLimitedRange);
        else
            _renderer.ResetYuvHintsToAuto();
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
}
