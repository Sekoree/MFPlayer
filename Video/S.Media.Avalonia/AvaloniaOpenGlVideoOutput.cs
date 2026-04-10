using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
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
    public readonly record struct DiagnosticsSnapshot(
        long RenderCalls,
        long PresentedFrames,
        long BlackFrames,
        long RenderExceptions,
        long InitCalls,
        long DeinitCalls);

    private readonly object _stateLock = new();
    private AvaloniaGlRenderer? _renderer;
    private VideoMixer? _mixer;
    private VideoPtsClock? _clock;
    private VideoFormat _outputFormat;
    private bool _isOpen;
    private bool _isRunning;
    private bool _disposed;

    private long _renderCalls;
    private long _presentedFrames;
    private long _blackFrames;
    private long _renderExceptions;
    private long _initCalls;
    private long _deinitCalls;

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        RenderCalls: Interlocked.Read(ref _renderCalls),
        PresentedFrames: Interlocked.Read(ref _presentedFrames),
        BlackFrames: Interlocked.Read(ref _blackFrames),
        RenderExceptions: Interlocked.Read(ref _renderExceptions),
        InitCalls: Interlocked.Read(ref _initCalls),
        DeinitCalls: Interlocked.Read(ref _deinitCalls));

    public VideoFormat OutputFormat => _outputFormat;

    public IVideoMixer Mixer => _mixer ?? throw new InvalidOperationException("Call Open() first.");

    public IMediaClock Clock => _clock ?? throw new InvalidOperationException("Call Open() first.");

    public bool IsRunning => _isRunning;

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
            _clock = new VideoPtsClock(sampleRate: _outputFormat.FrameRate > 0 ? _outputFormat.FrameRate : 30);
            _isOpen = true;
        }

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
        RequestNextFrameRendering();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _isRunning = false;
        _clock?.Stop();
        return Task.CompletedTask;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Interlocked.Increment(ref _initCalls);
        _renderer ??= new AvaloniaGlRenderer();
        _renderer.Initialise(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Interlocked.Increment(ref _deinitCalls);
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnOpenGlLost()
    {
        _renderer?.Dispose();
        _renderer = null;
        base.OnOpenGlLost();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Increment(ref _renderCalls);

        if (_renderer == null || _mixer == null || _clock == null)
            return;

        double scale = (VisualRoot as IRenderRoot)?.RenderScaling ?? 1.0;
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

            var frame = _mixer.PresentNextFrame(_clock.Position);
            if (frame.HasValue)
            {
                _renderer.UploadAndDraw(frame.Value, fb, viewportWidth, viewportHeight);
                _clock.UpdateFromFrame(frame.Value.Pts);
                frame.Value.MemoryOwner?.Dispose();
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
                Console.Error.WriteLine($"[AvaloniaOpenGlVideoOutput] render exception (count={ec}): {ex}");
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

        _ = StopAsync();
        _renderer?.Dispose();
        _renderer = null;
        _mixer?.Dispose();
        _clock?.Dispose();
    }
}
