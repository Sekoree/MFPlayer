using System.Collections.Concurrent;
using NDILib;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Minimal NDI video sink skeleton for the multi-target video mixer path.
/// ReceiveFrame is non-blocking and allocation-free on the hot path by using
/// a preallocated byte-buffer pool and a background sender thread.
/// </summary>
public sealed class NDIVideoSink : IVideoSink
{
    private readonly struct PendingFrame
    {
        public readonly byte[] Buffer;
        public readonly int Width;
        public readonly int Height;
        public readonly long PtsTicks;

        public PendingFrame(byte[] buffer, int width, int height, long ptsTicks)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            PtsTicks = ptsTicks;
        }
    }

    private readonly NDISender _sender;
    private readonly VideoFormat _targetFormat;
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly ConcurrentQueue<PendingFrame> _pending = new();

    private Thread? _writeThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private bool _disposed;

    private long _poolMissDrops;
    private long _capacityMissDrops;
    private long _formatDrops;

    public string Name { get; }
    public bool IsRunning => _running;
    public long PoolMissDrops => Interlocked.Read(ref _poolMissDrops);
    public long CapacityMissDrops => Interlocked.Read(ref _capacityMissDrops);
    public long FormatDrops => Interlocked.Read(ref _formatDrops);

    public NDIVideoSink(
        NDISender sender,
        VideoFormat targetFormat,
        int poolCount = 4,
        string? name = null)
    {
        _sender = sender;
        _targetFormat = targetFormat with { PixelFormat = PixelFormat.Rgba32 };
        Name = name ?? "NDIVideoSink";

        int width = _targetFormat.Width > 0 ? _targetFormat.Width : 1280;
        int height = _targetFormat.Height > 0 ? _targetFormat.Height : 720;
        int bytes = width * height * 4;
        for (int i = 0; i < Math.Max(1, poolCount); i++)
            _pool.Enqueue(new byte[bytes]);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;
        _writeThread = new Thread(WriteLoop)
        {
            Name = $"{Name}.WriteThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _writeThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    // RT/non-blocking path
    public void ReceiveFrame(in VideoFrame frame)
    {
        if (!_running) return;

        if (frame.PixelFormat != PixelFormat.Rgba32)
        {
            Interlocked.Increment(ref _formatDrops);
            return;
        }

        int bytes = frame.Width * frame.Height * 4;
        if (bytes <= 0)
        {
            Interlocked.Increment(ref _capacityMissDrops);
            return;
        }

        if (!_pool.TryDequeue(out var dst))
        {
            Interlocked.Increment(ref _poolMissDrops);
            return;
        }

        if (dst.Length < bytes)
        {
            _pool.Enqueue(dst);
            Interlocked.Increment(ref _capacityMissDrops);
            return;
        }

        var src = frame.Data.Span;
        int copy = Math.Min(src.Length, bytes);
        src[..copy].CopyTo(dst.AsSpan(0, copy));
        _pending.Enqueue(new PendingFrame(dst, frame.Width, frame.Height, frame.Pts.Ticks));
    }

    private unsafe void WriteLoop()
    {
        var token = _cts!.Token;
        int fpsNum = _targetFormat.FrameRateNumerator > 0 ? _targetFormat.FrameRateNumerator : 30000;
        int fpsDen = _targetFormat.FrameRateDenominator > 0 ? _targetFormat.FrameRateDenominator : 1001;

        while (!token.IsCancellationRequested)
        {
            if (!_pending.TryDequeue(out var pf))
            {
                Thread.Yield();
                continue;
            }

            fixed (byte* p = pf.Buffer)
            {
                var vf = new NdiVideoFrameV2
                {
                    Xres = pf.Width,
                    Yres = pf.Height,
                    FourCC = NdiFourCCVideoType.Rgba,
                    FrameRateN = fpsNum,
                    FrameRateD = fpsDen,
                    PictureAspectRatio = pf.Height > 0 ? (float)pf.Width / pf.Height : 1f,
                    FrameFormatType = NdiFrameFormatType.Progressive,
                    Timecode = pf.PtsTicks,
                    PData = (nint)p,
                    LineStrideInBytes = pf.Width * 4,
                    PMetadata = nint.Zero,
                    Timestamp = pf.PtsTicks
                };

                _sender.SendVideo(vf);
            }

            _pool.Enqueue(pf.Buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(2));
    }
}

