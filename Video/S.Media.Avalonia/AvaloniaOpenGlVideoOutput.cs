using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Avalonia;

/// <summary>
/// Avalonia embedded video output based on OpenGlControlBase.
/// Host apps place this control in their visual tree and wire channels via Mixer.
/// </summary>
public sealed class AvaloniaOpenGlVideoOutput : OpenGlControlBase, IVideoOutput
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

    private readonly object _stateLock = new();
    private AvaloniaGlRenderer? _renderer;
    private VideoMixer? _mixer;
    private volatile IVideoMixer? _activeMixer;
    private VideoPtsClock? _clock;
    private VideoFormat _outputFormat;
    private readonly BasicPixelFormatConverter _converter = new();
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

    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    public bool IsRunning => _isRunning;

    public void OverridePresentationMixer(IVideoMixer mixer) => _activeMixer = mixer;

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

            _outputFormat = format with { PixelFormat = PixelFormat.Rgba32 };
            _mixer = new VideoMixer(_outputFormat);
            _activeMixer = _mixer;
            _clock = new VideoPtsClock(sampleRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);
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
        return Task.CompletedTask;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Interlocked.Increment(ref _initCalls);
        _renderer ??= new AvaloniaGlRenderer();
        _renderer.Initialise(gl);
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

        var mixer = _activeMixer ?? _mixer;
        if (_renderer == null || mixer == null || _clock == null)
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

            var clockPosition = _clock.Position;
            var frame = mixer.PresentNextFrame(clockPosition);
            if (frame.HasValue)
            {
                var vf = frame.Value;

                // If decode/render falls behind, skip stale frames up to a bounded budget.
                for (int i = 0; i < _maxCatchupPullsPerRender; i++)
                {
                    if (vf.Pts + _catchupLagThreshold >= clockPosition)
                        break;

                    var next = mixer.PresentNextFrame(clockPosition);
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

                // Avalonia renderer expects RGBA8 texture uploads; convert at the output
                // boundary so the mixer can stay raw-frame only.
                VideoFrame renderFrame = vf;
                IDisposable? tempOwner = null;
                if (vf.PixelFormat != PixelFormat.Rgba32)
                {
                    var converted = _converter.Convert(vf, PixelFormat.Rgba32);
                    renderFrame = converted;
                    tempOwner = !ReferenceEquals(converted.MemoryOwner, vf.MemoryOwner)
                        ? converted.MemoryOwner
                        : null;
                }

                bool sameAsUploaded = _hasUploadedFrame &&
                                      renderFrame.Width == _lastUploadedWidth &&
                                      renderFrame.Height == _lastUploadedHeight &&
                                      renderFrame.Pts == _lastUploadedPts &&
                                      renderFrame.Data.Equals(_lastUploadedData);

                if (sameAsUploaded)
                {
                    _renderer.DrawLastTexture(fb, viewportWidth, viewportHeight);
                    Interlocked.Increment(ref _textureReuseDraws);
                }
                else
                {
                    _renderer.UploadAndDraw(renderFrame, fb, viewportWidth, viewportHeight);
                    _hasUploadedFrame = true;
                    _lastUploadedWidth = renderFrame.Width;
                    _lastUploadedHeight = renderFrame.Height;
                    _lastUploadedPts = renderFrame.Pts;
                    _lastUploadedData = renderFrame.Data;
                    Interlocked.Increment(ref _textureUploads);
                }

                tempOwner?.Dispose();

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
        _mixer?.Dispose();
        _clock?.Dispose();
        _converter.Dispose();
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
