using System.Collections.Concurrent;
using NDILib;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// NDI video sink that sends frames to an <see cref="NDISender"/> on a background thread.
/// Supports all NDI-native pixel formats: BGRA32, RGBA32, NV12, UYVY422, and YUV420p (sent
/// as NDI I420). BGRA/RGBA are converted by the mixer if needed; YUV formats are forwarded
/// raw (<see cref="BypassMixerConversion"/> is true), so the source channel must already
/// produce the requested YUV format.
/// </summary>
public sealed class NDIVideoSink : IVideoSink, IVideoSinkFormatCapabilities
{
    private readonly struct PendingFrame
    {
        public readonly byte[] Buffer;
        public readonly int Width;
        public readonly int Height;
        public readonly long PtsTicks;
        public readonly PixelFormat PixelFormat;

        public PendingFrame(byte[] buffer, int width, int height, long ptsTicks, PixelFormat pixelFormat)
        {
            Buffer = buffer; Width = width; Height = height;
            PtsTicks = ptsTicks; PixelFormat = pixelFormat;
        }
    }

    // Supported pixel formats in descending preference per target type.
    private static readonly IReadOnlyList<PixelFormat> s_bgraPrefs  = [PixelFormat.Bgra32,  PixelFormat.Rgba32];
    private static readonly IReadOnlyList<PixelFormat> s_rgbaPrefs  = [PixelFormat.Rgba32,  PixelFormat.Bgra32];
    private static readonly IReadOnlyList<PixelFormat> s_nv12Prefs  = [PixelFormat.Nv12];
    private static readonly IReadOnlyList<PixelFormat> s_uyvyPrefs  = [PixelFormat.Uyvy422];
    private static readonly IReadOnlyList<PixelFormat> s_i420Prefs  = [PixelFormat.Yuv420p];

    private readonly NDISender _sender;
    private readonly VideoFormat _targetFormat;
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly ConcurrentQueue<PendingFrame> _pending = new();
    private readonly int _maxPendingFrames;
    private int _pendingFrames;

    private Thread? _writeThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private bool _disposed;

    private long _poolMissDrops;
    private long _capacityMissDrops;
    private long _formatDrops;
    private long _queueDrops;
    private long _passthroughFrames;

    public string Name { get; }
    public bool IsRunning => _running;

    /// <inheritdoc cref="IVideoSinkFormatCapabilities.PreferredPixelFormats"/>
    public IReadOnlyList<PixelFormat> PreferredPixelFormats => _targetFormat.PixelFormat switch
    {
        PixelFormat.Bgra32   => s_bgraPrefs,
        PixelFormat.Rgba32   => s_rgbaPrefs,
        PixelFormat.Nv12     => s_nv12Prefs,
        PixelFormat.Uyvy422  => s_uyvyPrefs,
        PixelFormat.Yuv420p  => s_i420Prefs,
        _                    => s_rgbaPrefs,
    };

    /// <summary>
    /// True for YUV target formats: the mixer bypasses its own conversion and delivers
    /// raw source frames. The source channel must already be in the requested YUV format.
    /// False for BGRA32/RGBA32: the mixer converts between the two as needed.
    /// </summary>
    public bool BypassMixerConversion => _targetFormat.PixelFormat
        is not (PixelFormat.Bgra32 or PixelFormat.Rgba32);

    public long PoolMissDrops     => Interlocked.Read(ref _poolMissDrops);
    public long CapacityMissDrops => Interlocked.Read(ref _capacityMissDrops);
    public long FormatDrops       => Interlocked.Read(ref _formatDrops);
    public long QueueDrops        => Interlocked.Read(ref _queueDrops);

    public VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        long queueDrops = Interlocked.Read(ref _queueDrops);
        long dropped    = Interlocked.Read(ref _poolMissDrops)
                        + Interlocked.Read(ref _capacityMissDrops)
                        + Interlocked.Read(ref _formatDrops)
                        + queueDrops;

        return new VideoEndpointDiagnosticsSnapshot(
            PassthroughFrames: Interlocked.Read(ref _passthroughFrames),
            ConvertedFrames:   0,
            DroppedFrames:     dropped,
            QueueDepth:        Volatile.Read(ref _pendingFrames),
            QueueDrops:        queueDrops);
    }

    public NDIVideoSink(
        NDISender sender,
        VideoFormat targetFormat,
        int poolCount = 4,
        int maxPendingFrames = 6,
        NdiEndpointPreset preset = NdiEndpointPreset.Balanced,
        string? name = null)
    {
        _sender = sender;

        // Accept all NDI-native formats; normalise unsupported formats to RGBA32.
        var px = targetFormat.PixelFormat is
            PixelFormat.Bgra32 or PixelFormat.Rgba32 or
            PixelFormat.Nv12   or PixelFormat.Uyvy422 or PixelFormat.Yuv420p
                ? targetFormat.PixelFormat
                : PixelFormat.Rgba32;
        _targetFormat = targetFormat with { PixelFormat = px };

        var presetOptions = NdiVideoPresetOptions.For(preset);
        if (poolCount <= 0)       poolCount       = presetOptions.PoolCount;
        if (maxPendingFrames <= 0) maxPendingFrames = presetOptions.MaxPendingFrames;
        _maxPendingFrames = maxPendingFrames;

        Name = name ?? "NDIVideoSink";

        int w = _targetFormat.Width  > 0 ? _targetFormat.Width  : 1280;
        int h = _targetFormat.Height > 0 ? _targetFormat.Height : 720;
        int bytes = BytesPerFrame(px, w, h);
        for (int i = 0; i < Math.Max(1, poolCount); i++)
            _pool.Enqueue(new byte[bytes]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Total byte count for one frame of the given format.</summary>
    private static int BytesPerFrame(PixelFormat fmt, int w, int h) => fmt switch
    {
        PixelFormat.Bgra32  or PixelFormat.Rgba32  => w * h * 4,
        PixelFormat.Uyvy422                         => w * h * 2,
        PixelFormat.Nv12    or PixelFormat.Yuv420p  => w * h * 3 / 2,
        _                                           => w * h * 4,
    };

    /// <summary>Y-plane (or packed-plane) line stride in bytes.</summary>
    private static int LineStride(PixelFormat fmt, int w) => fmt switch
    {
        PixelFormat.Bgra32  or PixelFormat.Rgba32  => w * 4,
        PixelFormat.Uyvy422                         => w * 2,
        PixelFormat.Nv12    or PixelFormat.Yuv420p  => w,      // Y plane stride; chroma follows
        _                                           => w * 4,
    };

    /// <summary>Maps our PixelFormat to the NDI FourCC. Yuv420p is sent as I420.</summary>
    private static NdiFourCCVideoType ToFourCC(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Bgra32  => NdiFourCCVideoType.Bgra,
        PixelFormat.Rgba32  => NdiFourCCVideoType.Rgba,
        PixelFormat.Nv12    => NdiFourCCVideoType.Nv12,
        PixelFormat.Uyvy422 => NdiFourCCVideoType.Uyvy,
        PixelFormat.Yuv420p => NdiFourCCVideoType.I420,  // NDI I420 == planar YUV 4:2:0
        _                   => NdiFourCCVideoType.Rgba,
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;
        _writeThread = new Thread(WriteLoop)
        {
            Name         = $"{Name}.WriteThread",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal
        };
        _writeThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        _cts?.Cancel();
        var t = _writeThread;
        if (t == null) return Task.CompletedTask;
        return Task.Run(() => t.Join(TimeSpan.FromSeconds(3)), ct);
    }

    // ── ReceiveFrame — RT thread, must not block or allocate ──────────────

    public void ReceiveFrame(in VideoFrame frame)
    {
        if (!_running) return;

        if (frame.PixelFormat != _targetFormat.PixelFormat)
        {
            Interlocked.Increment(ref _formatDrops);
            return;
        }

        int bytes = BytesPerFrame(frame.PixelFormat, frame.Width, frame.Height);
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

        var src  = frame.Data.Span;
        int copy = Math.Min(src.Length, bytes);
        src[..copy].CopyTo(dst.AsSpan(0, copy));

        if (Volatile.Read(ref _pendingFrames) >= _maxPendingFrames)
        {
            _pool.Enqueue(dst);
            Interlocked.Increment(ref _queueDrops);
            return;
        }

        _pending.Enqueue(new PendingFrame(dst, frame.Width, frame.Height, frame.Pts.Ticks, frame.PixelFormat));
        Interlocked.Increment(ref _pendingFrames);
        Interlocked.Increment(ref _passthroughFrames);
    }

    // ── Write thread ──────────────────────────────────────────────────────

    private unsafe void WriteLoop()
    {
        var token  = _cts!.Token;
        int fpsNum = _targetFormat.FrameRateNumerator   > 0 ? _targetFormat.FrameRateNumerator   : 30000;
        int fpsDen = _targetFormat.FrameRateDenominator > 0 ? _targetFormat.FrameRateDenominator : 1001;

        while (!token.IsCancellationRequested)
        {
            if (!_pending.TryDequeue(out var pf))
            {
                Thread.Yield();
                continue;
            }

            Interlocked.Decrement(ref _pendingFrames);

            try
            {
                fixed (byte* p = pf.Buffer)
                {
                    var vf = new NdiVideoFrameV2
                    {
                        Xres               = pf.Width,
                        Yres               = pf.Height,
                        FourCC             = ToFourCC(pf.PixelFormat),
                        FrameRateN         = fpsNum,
                        FrameRateD         = fpsDen,
                        PictureAspectRatio = pf.Height > 0 ? (float)pf.Width / pf.Height : 1f,
                        FrameFormatType    = NdiFrameFormatType.Progressive,
                        Timecode           = pf.PtsTicks,
                        PData              = (nint)p,
                        LineStrideInBytes  = LineStride(pf.PixelFormat, pf.Width),
                        PMetadata          = nint.Zero,
                        Timestamp          = pf.PtsTicks
                    };

                    _sender.SendVideo(vf);
                }
            }
            catch (Exception ex)
            {
                // Log and continue — do not let a native NDI exception propagate up
                // to terminate the process (unhandled exception on a background thread).
                if (!(ex is OperationCanceledException))
                    Console.Error.WriteLine($"[{Name}] NDI SendVideo exception: {ex.Message}");
            }
            finally
            {
                // Always return the send buffer so the pool does not starve.
                _pool.Enqueue(pf.Buffer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running  = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(2));
    }
}

