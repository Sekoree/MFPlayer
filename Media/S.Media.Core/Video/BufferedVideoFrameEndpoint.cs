using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Endpoint buffer that supports push and pull flows.
/// </summary>
public sealed class BufferedVideoFrameEndpoint : IVideoFrameEndpoint, IVideoFramePullSource
{
    private readonly Queue<VideoFrame> _queue = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly int _capacity;
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

        _capacity = Math.Max(1, capacity);
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

        lock (_gate)
        {
            if (_queue.Count >= _capacity)
            {
                var dropped = _queue.Dequeue();
                dropped.MemoryOwner?.Dispose();
                // No net change in count — a reader is already owed a signal for this slot,
                // so we don't Release() here; the slot is being replaced, not added.
            }
            else
            {
                // Net new item — wake a waiting reader.
                _available.Release();
            }

            _queue.Enqueue(frame);
        }
    }

    public async ValueTask<VideoFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        if (_disposed || !_running)
            return null;

        try { await _available.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }

        lock (_gate)
            return _queue.Count > 0 ? _queue.Dequeue() : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                var frame = _queue.Dequeue();
                frame.MemoryOwner?.Dispose();
            }
        }

        _available.Release();
        _available.Dispose();
    }
}

