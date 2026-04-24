using System.Buffers;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;

namespace S.Media.Avalonia;

/// <summary>
/// Clone/preview sink for Avalonia. Can be registered on a router to mirror video
/// without creating an extra decoder instance.
/// </summary>
public sealed class AvaloniaOpenGlVideoCloneEndpoint : OpenGlControlBase, IVideoEndpoint, IFormatCapabilities<PixelFormat>
{
    private readonly VideoFrameSlot _latestFrame = new();
    private AvaloniaGlRenderer? _renderer;
    private bool _disposed;
    private bool _running;

    public new string Name { get; }
    public bool IsRunning => _running;
    // §3.37 / A9 — clone sink now mirrors the parent endpoint's GPU upload
    // formats directly; no CPU scalar converter path.
    public IReadOnlyList<PixelFormat> SupportedFormats { get; } =
    [
        PixelFormat.Rgba32,
        PixelFormat.Bgra32,
        PixelFormat.Nv12,
        PixelFormat.Yuv420p,
        PixelFormat.Yuv422p10,
        PixelFormat.Uyvy422,
        PixelFormat.P010,
        PixelFormat.Yuv444p,
        PixelFormat.Gray8
    ];
    public PixelFormat? PreferredFormat => PixelFormat.Bgra32;

    internal AvaloniaOpenGlVideoCloneEndpoint(string? name = null)
    {
        Name = name ?? "AvaloniaCloneSink";
        Width = double.NaN;
        Height = double.NaN;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        RequestNextFrameRendering();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public void ReceiveFrame(in VideoFrame frame)
    {
        if (!_running || _disposed)
            return;

        int bytes = frame.Data.Length;
        if (bytes <= 0)
            return;

        var rented = ArrayPool<byte>.Shared.Rent(bytes);
        frame.Data.Span.CopyTo(rented.AsSpan(0, bytes));
        var copied = new VideoFrame(frame.Width, frame.Height, frame.PixelFormat, rented.AsMemory(0, bytes), frame.Pts, new ArrayPoolOwner<byte>(rented));

        _latestFrame.Set(copied);

        RequestNextFrameRendering();
    }

    /// <summary>
    /// §3.38 / A3 — ref-counted zero-copy fast path. Ref-counted frames are
    /// retained and parked in <see cref="_latestFrame"/> without copying the
    /// pixels. <see cref="VideoFrameSlot.Set"/> releases the previously-held
    /// frame's ref when a new one arrives. Non-ref-counted frames fall back to
    /// the copy path in the legacy overload.
    /// </summary>
    public void ReceiveFrame(in VideoFrameHandle handle)
    {
        if (!_running || _disposed)
            return;

        if (!handle.IsRefCounted)
        {
            var legacy = handle.Frame;
            ReceiveFrame(in legacy);
            return;
        }

        if (handle.Frame.Data.Length <= 0)
            return;

        handle.Retain();
        _latestFrame.Set(handle.Frame);

        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _renderer ??= new AvaloniaGlRenderer();
        _renderer.Initialise(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer == null)
            return;

        double scale = (VisualRoot as IRenderRoot)?.RenderScaling ?? 1.0;
        int viewportWidth = (int)Math.Max(1, Math.Round(Bounds.Width * scale));
        int viewportHeight = (int)Math.Max(1, Math.Round(Bounds.Height * scale));

        var frame = _latestFrame.Peek();

        if (!frame.HasValue)
        {
            _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
        }
        else
        {
            var vf = frame.Value;
            _renderer.UploadAndDraw(vf, fb, viewportWidth, viewportHeight);
        }

        if (_running)
            RequestNextFrameRendering();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        _latestFrame.Clear();

        _renderer?.Dispose();
        _renderer = null;
    }

}
