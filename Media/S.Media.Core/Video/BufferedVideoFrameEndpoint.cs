using System.Threading.Channels;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Endpoint buffer that supports push and pull flows.
/// </summary>
public sealed class BufferedVideoFrameEndpoint : IVideoFrameEndpoint, IVideoFramePullSource
{
    private readonly Channel<VideoFrame> _channel;
    private bool _disposed;
    private volatile bool _running;

    public string Name { get; }
    public bool IsRunning => _running;
    public IReadOnlyList<PixelFormat> SupportedPixelFormats { get; }

    public BufferedVideoFrameEndpoint(
        string name,
        IReadOnlyList<PixelFormat>? supportedPixelFormats = null,
        int capacity = 4)
    {
        Name = string.IsNullOrWhiteSpace(name) ? nameof(BufferedVideoFrameEndpoint) : name;
        SupportedPixelFormats = supportedPixelFormats is { Count: > 0 }
            ? supportedPixelFormats
            : [PixelFormat.Rgba32, PixelFormat.Bgra32];

        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        return Task.CompletedTask;
    }

    public void WriteFrame(in VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_running)
            return;

        _channel.Writer.TryWrite(frame);
    }

    public async ValueTask<VideoFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        if (_disposed || !_running)
            return null;

        try
        {
            return await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var frame))
            frame.MemoryOwner?.Dispose();
    }
}

