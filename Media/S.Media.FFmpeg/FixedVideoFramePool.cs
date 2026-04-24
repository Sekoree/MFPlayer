using System.Collections.Concurrent;

namespace S.Media.FFmpeg;

/// <summary>
/// Per-channel fixed-size frame buffer pool used by <see cref="FFmpegVideoChannel"/>.
/// <para>
/// §8.1 — avoids <c>ArrayPool&lt;byte&gt;.Shared</c> churn for large 4K-class frame
/// buffers by keeping a bounded queue of same-sized arrays scoped to one channel.
/// This keeps LOH traffic predictable and avoids cross-component contention on the
/// shared pool.
/// </para>
/// </summary>
internal sealed class FixedVideoFramePool : IDisposable
{
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly Lock _resizeLock = new();
    private readonly int _maxRetained;

    private int _bufferSize;
    private int _retainedCount;
    private volatile bool _disposed;

    public FixedVideoFramePool(int maxRetained)
    {
        _maxRetained = Math.Max(1, maxRetained);
    }

    public byte[] Rent(int requiredSize)
    {
        if (requiredSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSize));

        EnsureSize(requiredSize);
        if (_queue.TryDequeue(out var buffer))
        {
            Interlocked.Decrement(ref _retainedCount);
            return buffer;
        }

        return new byte[requiredSize];
    }

    public void Return(byte[]? buffer)
    {
        if (_disposed || buffer is null)
            return;

        int expected = Volatile.Read(ref _bufferSize);
        if (expected <= 0 || buffer.Length != expected)
            return;

        while (true)
        {
            int current = Volatile.Read(ref _retainedCount);
            if (current >= _maxRetained)
                return;

            if (Interlocked.CompareExchange(ref _retainedCount, current + 1, current) == current)
                break;
        }

        _queue.Enqueue(buffer);
    }

    private void EnsureSize(int requiredSize)
    {
        if (Volatile.Read(ref _bufferSize) == requiredSize)
            return;

        lock (_resizeLock)
        {
            if (_bufferSize == requiredSize)
                return;

            DrainQueue();
            Volatile.Write(ref _bufferSize, requiredSize);
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _retainedCount);

        if (Volatile.Read(ref _retainedCount) < 0)
            Interlocked.Exchange(ref _retainedCount, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        DrainQueue();
    }
}

/// <summary>
/// Returns a fixed-size frame buffer back to <see cref="FixedVideoFramePool"/> once.
/// </summary>
internal sealed class FixedVideoFrameOwner : IDisposable
{
    private FixedVideoFramePool? _pool;
    private byte[]? _buffer;

    public FixedVideoFrameOwner(FixedVideoFramePool pool, byte[] buffer)
    {
        _pool = pool;
        _buffer = buffer;
    }

    public void Dispose()
    {
        var pool = Interlocked.Exchange(ref _pool, null);
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (pool is null || buffer is null)
            return;

        pool.Return(buffer);
    }
}
