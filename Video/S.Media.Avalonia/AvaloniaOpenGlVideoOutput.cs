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
public sealed class AvaloniaOpenGlVideoOutput : OpenGlControlBase, IPullVideoEndpoint, IClockCapableEndpoint
{
    private static readonly ILogger Log = AvaloniaVideoLogging.GetLogger(nameof(AvaloniaOpenGlVideoOutput));

    string IMediaEndpoint.Name => Name ?? nameof(AvaloniaOpenGlVideoOutput);

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

    // Per-frame auto-hint tracking (render thread only — no lock needed).
    private YuvColorMatrix _lastAutoMatrix = YuvColorMatrix.Auto;
    private YuvColorRange  _lastAutoRange  = YuvColorRange.Auto;

    private TimeSpan _catchupLagThreshold = TimeSpan.FromMilliseconds(45);
    private int _maxCatchupPullsPerRender = 6;

    private readonly Lock _cloneLock = new();
    private AvaloniaOpenGlVideoCloneSink[] _clones = [];

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
        _renderer?.SetYuvHints(matrix, limitedRange);
    }

    /// <summary>Resets YUV hints to auto-detect from frame resolution.</summary>
    public void ResetYuvHints()
    {
        _hasYuvHintsOverride = false;
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
        get => _catchupLagThreshold;
        set => _catchupLagThreshold = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
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
    /// Opens the output pipeline. The title parameter is ignored for embedded controls.
    /// </summary>
    public void Open(string title, int width, int height, VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_isOpen)
                throw new InvalidOperationException("Output is already open.");

            _outputFormat = format;
            _clock = new VideoPtsClock(frameRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);
            _isOpen = true;
        }

        Log.LogInformation("Opened AvaloniaOpenGlVideoOutput: {Width}x{Height} px={PixelFormat}, fps={FrameRate}",
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
        Log.LogInformation("AvaloniaOpenGlVideoOutput started");
        RequestNextFrameRendering();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        Log.LogInformation("Stopping AvaloniaOpenGlVideoOutput");
        _isRunning = false;
        _clock?.Stop();
        // Release the last-uploaded frame reference so the ArrayPool rental can be returned.
        _hasUploadedFrame = false;
        _lastUploadedData = default;
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
        _hasUploadedFrame = false;
        _lastUploadedData = default;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Interlocked.Increment(ref _deinitCalls);
        _renderer?.Dispose();
        _renderer = null;
        _hasUploadedFrame = false;
        _lastUploadedData = default;
    }

    protected override void OnOpenGlLost()
    {
        _renderer?.Dispose();
        _renderer = null;
        _hasUploadedFrame = false;
        _lastUploadedData = default;
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Increment(ref _renderCalls);

        if (_renderer == null || _clock == null)
            return;

        double scale = VisualRoot?.RenderScaling ?? 1.0;
        int viewportWidth = (int)Math.Max(1, Math.Round(Bounds.Width * scale));
        int viewportHeight = (int)Math.Max(1, Math.Round(Bounds.Height * scale));

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
                if (presentCb.TryPresentNext(clockPosition, out var cbFrame))
                    frame = cbFrame;
            }

                if (frame.HasValue)
                {
                    var vf = frame.Value;

                    // If decode/render falls behind, skip stale frames up to a bounded budget.
                    for (int i = 0; i < _maxCatchupPullsPerRender; i++)
                    {
                        if (vf.Pts + _catchupLagThreshold >= clockPosition)
                            break;

                        VideoFrame? next = null;
                        if (presentCb is not null)
                        {
                            next = presentCb.TryPresentNext(clockPosition, out var nf) ? nf : null;
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
                bool sameAsUploaded = _hasUploadedFrame &&
                                      vf.Width == _lastUploadedWidth &&
                                      vf.Height == _lastUploadedHeight &&
                                      vf.Pts == _lastUploadedPts &&
                                      vf.Data.Equals(_lastUploadedData);

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
                    Interlocked.Increment(ref _textureUploads);
                }


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
            if (ec <= 3 || ec % 100 == 0)
                Log.LogError(ex, "Render exception (count={Count})", ec);
        }
        finally
        {
            if (_isRunning)
                RequestNextFrameRendering();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ = StopAsync();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _isRunning)
            RequestNextFrameRendering();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Log.LogInformation("Disposing AvaloniaOpenGlVideoOutput: presented={Presented}, black={Black}, uploads={Uploads}, catchupSkips={CatchupSkips}, renderExceptions={RenderExceptions}",
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
        _renderer?.Dispose();
        _renderer = null;
        _clock?.Dispose();
    }

    public AvaloniaOpenGlVideoCloneSink CreateCloneSink(string? name = null)
    {
        if (!_isOpen)
            throw new InvalidOperationException("Call Open() before creating clone sinks.");

        var clone = new AvaloniaOpenGlVideoCloneSink(name);
        lock (_cloneLock)
        {
            var old = _clones;
            var neo = new AvaloniaOpenGlVideoCloneSink[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = clone;
            _clones = neo;
        }

        return clone;
    }
}
