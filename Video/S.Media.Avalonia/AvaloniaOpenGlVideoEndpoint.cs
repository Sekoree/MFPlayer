using System.Buffers;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
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
public class AvaloniaOpenGlVideoEndpoint : OpenGlControlBase, IPullVideoEndpoint, IClockCapableEndpoint, IVideoColorMatrixReceiver
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
    private VideoFormat _outputFormat;
    private bool _hasYuvHintsOverride;
    private bool _yuvBt709;
    private bool _yuvLimitedRange;
    private YuvColorMatrix _yuvColorMatrix = YuvColorMatrix.Auto;
    private volatile int _scalingFilter = (int)ScalingFilter.Bicubic;
    private bool _isOpen;
    private bool _isRunning;
    private bool _disposed;

    private long _renderCalls;
    private long _presentedFrames;
    private long _blackFrames;
    private long _renderExceptions;
    private long _initCalls;
    private long _deinitCalls;
    private long _textureUploads;
    private long _textureReuseDraws;
    private long _catchupSkips;

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
    /// When <see langword="true"/>, a diagnostic HUD overlay is rendered on top of the video.
    /// Thread-safe: can be toggled from any thread at any time.
    /// </summary>
    public bool ShowHud { get; set; }

    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    public bool IsRunning => _isRunning;


    // ── IPullVideoEndpoint ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public IVideoPresentCallback? PresentCallback { get; set; }

    // ── IVideoEndpoint (push — unused for pull endpoints) ───────────────────

    void IVideoEndpoint.ReceiveFrame(in VideoFrame frame) { }

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
        _renderer?.SetYuvHints(bt709, limitedRange);
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
        _renderer?.SetYuvHints(matrix, limitedRange);
    }

    /// <summary>Resets YUV hints to auto-detect from frame resolution.</summary>
    public void ResetYuvHints()
    {
        _hasYuvHintsOverride = false;
        _lastAutoMatrix = YuvColorMatrix.Auto;
        _lastAutoRange = YuvColorRange.Auto;
        _renderer?.ResetYuvHintsToAuto();
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
            _renderer?.SetYuvHints(newMatrix, newLimited);
            _yuvColorMatrix = newMatrix;
            _yuvLimitedRange = newLimited;
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
    /// Overrides the render-loop presentation clock.
    /// Set this to an audio hardware clock to keep video pacing aligned with audio.
    /// </summary>
    public void OverridePresentationClock(IMediaClock? clock)
    {
        _presentationClockOverride = clock;
        Volatile.Write(ref _hasPresentationClockOrigin, 0);
        Volatile.Write(ref _presentationClockOriginTicks, 0);
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
        if (!_isOpen)
            throw new InvalidOperationException("Call Open() before Start.");

        _clock!.Start();
        _isRunning = true;
        Volatile.Write(ref _hasPresentationClockOrigin, 0);
        Volatile.Write(ref _presentationClockOriginTicks, 0);
        Log.LogInformation("AvaloniaOpenGlVideoEndpoint started");
        RequestNextFrameRendering();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        Log.LogInformation("Stopping AvaloniaOpenGlVideoEndpoint");
        _isRunning = false;
        _clock?.Stop();
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

        // §3.36 — track whether we actually uploaded a frame this tick so the
        // finally block can decide whether to re-arm the render timer.
        bool uploadedThisTick = false;

        try
        {
            if (!_isRunning)
            {
                _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
                Interlocked.Increment(ref _blackFrames);
                return;
            }

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

            var frame = (VideoFrame?)null;

            var presentCb = PresentCallback;
            if (presentCb is not null)
            {
                if (presentCb.TryPresentNext(clockPosition, out VideoFrame cbFrame))
                    frame = cbFrame;
            }

                if (frame.HasValue)
                {
                    var vf = frame.Value;
                    ApplyAutoYuvHintsIfNeeded(vf.Width, vf.Height);

                    // If decode/render falls behind, skip stale frames up to a bounded budget.
                    for (int i = 0; i < _maxCatchupPullsPerRender; i++)
                    {
                        if (vf.Pts + TimeSpan.FromTicks(Volatile.Read(ref _catchupLagThresholdTicks)) >= clockPosition)
                            break;

                        VideoFrame? next = null;
                        if (presentCb is not null)
                        {
                            next = presentCb.TryPresentNext(clockPosition, out VideoFrame nf) ? nf : null;
                        }
                        if (!next.HasValue)
                            break;

                        var nvf = next.Value;
                        if (nvf.Pts == vf.Pts &&
                            nvf.Width == vf.Width &&
                            nvf.Height == vf.Height &&
                            nvf.Data.Equals(vf.Data))
                            break;

                        vf = nvf;
                        Interlocked.Increment(ref _catchupSkips);
                    }


                // Renderer natively handles all GPU-uploadable formats (Rgba32, Bgra32, Nv12,
                // Yuv420p, Yuv422p10, Uyvy422, P010, Yuv444p, Gray8). Unknown formats render black.
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
                    _renderer.DrawLastTexture(fb, viewportWidth, viewportHeight);
                    Interlocked.Increment(ref _textureReuseDraws);
                }
                else
                {
                    _renderer.UploadAndDraw(vf, fb, viewportWidth, viewportHeight);
                    _hasUploadedFrame = true;
                    _lastUploadedWidth = vf.Width;
                    _lastUploadedHeight = vf.Height;
                    _lastUploadedPts = vf.Pts;
                    _lastUploadedData = vf.Data;
                    _lastUploadedMemoryOwner = vf.MemoryOwner;
                    Interlocked.Increment(ref _textureUploads);
                }

                uploadedThisTick = true;

                if (_presentationClockOverride == null)
                    _clock.UpdateFromFrame(vf.Pts);
                Interlocked.Increment(ref _presentedFrames);
            }
            else
            {
                _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
                Interlocked.Increment(ref _blackFrames);
            }
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
            if (_isRunning && (uploadedThisTick || LiveMode))
                RequestNextFrameRendering();
        }
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
                RequestNextFrameRendering();
        }
    }

    private void UpdateRenderScalingFromUIThread()
    {
        double scale = VisualRoot?.RenderScaling ?? 1.0;
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
}
