using System.Buffers;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Avalonia;

/// <summary>
/// Clone/preview sink for Avalonia. Can be registered on a mixer to mirror video
/// without creating an extra decoder instance.
/// </summary>
public sealed class AvaloniaOpenGlVideoCloneSink : OpenGlControlBase, IVideoSink, IVideoSinkFormatCapabilities
{
    private readonly Lock _gate = new();
    private readonly BasicPixelFormatConverter _converter = new();
    private AvaloniaGlRenderer? _renderer;
    private VideoFrame? _latestFrame;
    private bool _disposed;
    private bool _running;

    public new string Name { get; }
    public bool IsRunning => _running;
    public IReadOnlyList<PixelFormat> PreferredPixelFormats { get; } = [PixelFormat.Rgba32, PixelFormat.Bgra32];

    public AvaloniaOpenGlVideoCloneSink(string? name = null)
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

        lock (_gate)
        {
            _latestFrame?.MemoryOwner?.Dispose();
            _latestFrame = copied;
        }

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

        VideoFrame? frame;
        lock (_gate)
            frame = _latestFrame;

        if (!frame.HasValue)
        {
            _renderer.DrawBlack(fb, viewportWidth, viewportHeight);
        }
        else
        {
            var vf = frame.Value;
            if (vf.PixelFormat == PixelFormat.Bgra32)
            {
                // Convert quickly for renderer expectations.
                var rgba = _converter.Convert(vf, PixelFormat.Rgba32);
                _renderer.UploadAndDraw(rgba, fb, viewportWidth, viewportHeight);
                if (!ReferenceEquals(rgba.MemoryOwner, vf.MemoryOwner))
                    rgba.MemoryOwner?.Dispose();
            }
            else
            {
                _renderer.UploadAndDraw(vf, fb, viewportWidth, viewportHeight);
            }
        }

        if (_running)
            RequestNextFrameRendering();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        lock (_gate)
        {
            _latestFrame?.MemoryOwner?.Dispose();
            _latestFrame = null;
        }

        _renderer?.Dispose();
        _renderer = null;
        _converter.Dispose();
    }

    private sealed class ArrayPoolOwner<T> : IDisposable
    {
        private T[]? _buffer;
        public ArrayPoolOwner(T[] buffer) => _buffer = buffer;

        public void Dispose()
        {
            var b = Interlocked.Exchange(ref _buffer, null);
            if (b != null)
                ArrayPool<T>.Shared.Return(b);
        }
    }
}

