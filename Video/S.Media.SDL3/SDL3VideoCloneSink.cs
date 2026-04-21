using System.Buffers;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;
using SDL = global::SDL3.SDL;

namespace S.Media.SDL3;

/// <summary>
/// Parent-owned SDL3 clone sink that mirrors frames into its own window.
/// Instances are created via <see cref="SDL3VideoOutput.CreateCloneSink"/>.
/// </summary>
public sealed class SDL3VideoCloneSink : IVideoEndpoint, IFormatCapabilities<PixelFormat>
{
    private sealed class ArrayPoolFrameOwner : IMemoryOwner<byte>
    {
        private byte[]? _buffer;

        public ArrayPoolFrameOwner(byte[] buffer) => _buffer = buffer;

        public Memory<byte> Memory => _buffer ?? Memory<byte>.Empty;

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly VideoFormat _format;
    private readonly int _width;
    private readonly int _height;
    private readonly VideoFrameSlot _latestFrame = new();

    private nint _window;
    private nint _glContext;
    private GLRenderer? _renderer;
    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private bool _closeRequested;
    private bool _disposed;
    private bool _running;

    public string Name { get; }
    public bool IsRunning => _running;

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } =
        [PixelFormat.Bgra32, PixelFormat.Rgba32, PixelFormat.Nv12, PixelFormat.Yuv420p, PixelFormat.Yuv422p10];

    public PixelFormat? PreferredFormat => PixelFormat.Bgra32;

    internal SDL3VideoCloneSink(VideoFormat format, string? title, int width, int height)
    {
        _format = format;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        Name = title ?? "SDL3CloneSink";

        SDL3VideoOutput.AcquireSdlVideo();

        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

        _window = SDL.CreateWindow(Name, _width, _height, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity);
        if (_window == nint.Zero)
        {
            SDL3VideoOutput.ReleaseSdlVideo();
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");
        }

        _glContext = SDL.GLCreateContext(_window);
        if (_glContext == nint.Zero)
        {
            SDL.DestroyWindow(_window);
            _window = nint.Zero;
            SDL3VideoOutput.ReleaseSdlVideo();
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");
        }

        SDL.GLSetSwapInterval(1);

        _renderer = new GLRenderer();
        SDL.GetWindowSizeInPixels(_window, out int pixelW, out int pixelH);
        _renderer.Initialise(pixelW, pixelH);
        _renderer.SetVideoSize(Math.Max(1, _format.Width), Math.Max(1, _format.Height));

        SDL.GLMakeCurrent(_window, nint.Zero);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _closeRequested = false;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = $"{Name}.Render"
        };
        _renderThread.Start();

        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_running)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            _running = false;
            _cts?.Cancel();
            _renderThread?.Join(TimeSpan.FromSeconds(2));
        }, ct);
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
        var copied = new VideoFrame(
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            rented.AsMemory(0, bytes),
            frame.Pts,
            new ArrayPoolFrameOwner(rented));

        _latestFrame.Set(copied);
    }

    private void RenderLoop()
    {
        if (!SDL.GLMakeCurrent(_window, _glContext))
            return;

        var token = _cts!.Token;
        while (!token.IsCancellationRequested && !_closeRequested)
        {
            while (SDL.PollEvent(out var evt))
            {
                var eventType = (SDL.EventType)evt.Type;
                if (eventType is SDL.EventType.Quit or SDL.EventType.WindowCloseRequested)
                {
                    _closeRequested = true;
                    break;
                }

                if (eventType is SDL.EventType.WindowResized or SDL.EventType.WindowPixelSizeChanged)
                {
                    SDL.GetWindowSizeInPixels(_window, out int w, out int h);
                    _renderer?.SetViewport(w, h);
                }
            }

            if (_closeRequested)
                break;

            var frame = _latestFrame.Peek();

            if (frame.HasValue)
                _renderer?.UploadAndDraw(frame.Value);
            else
                _renderer?.DrawBlack();

            SDL.GLSwapWindow(_window);
        }

        SDL.GLMakeCurrent(_window, nint.Zero);
        _running = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _ = StopAsync();

        _latestFrame.Clear();

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

        _cts?.Dispose();
        _cts = null;
        _renderThread = null;
        _renderer = null;

        SDL3VideoOutput.ReleaseSdlVideo();
    }
}

