using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IVideoChannel"/> that pulls video from an NDI source via
/// <see cref="NDIFrameSync.CaptureVideo"/>. Frames are exposed as BGRA32 <see cref="VideoFrame"/> records.
/// Implements <see cref="IVideoColorMatrixHint"/> so the renderer can automatically apply
/// the correct YUV color-space when the NDI sender embeds <c>ndi_color_space</c> XML metadata.
/// </summary>
internal sealed class NDIVideoChannel : IVideoChannel, IVideoColorMatrixHint
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIVideoChannel));

    private readonly NDIFrameSync  _frameSync;
    private readonly Lock          _frameSyncGate;
    private readonly NDIClock      _clock;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    // Lock-free ring: unbounded channel (writes always succeed) + manual capacity enforcement.
    // "Drop oldest" is implemented by draining one frame from the reader before each write
    // when the counter has reached _ringCapacity, allowing the evicted MemoryOwner to be
    // disposed correctly — something BoundedChannelFullMode.DropOldest cannot do.
    private readonly ChannelReader<VideoFrame> _ringReader;
    private readonly ChannelWriter<VideoFrame> _ringWriter;
    private readonly int _ringCapacity;
    private readonly bool _preferLowLatency;
    private readonly int _waitPollMs;
    private long _framesInRing;

    private bool _disposed;
    private VideoFormat _sourceFormat;
    private readonly Lock _formatLock = new();
    private readonly HashSet<NDIFourCCVideoType> _unsupportedFourCcLogged = [];
    private long _positionTicks;
    private long _framesDequeued;

    // IVideoColorMatrixHint — updated from NDI frame metadata on the capture thread;
    // read by the render thread; Volatile ensures visibility without a lock.
    private volatile int _suggestedMatrix = (int)YuvColorMatrix.Auto;
    private volatile int _suggestedRange  = (int)YuvColorRange.Auto;

    /// <inheritdoc/>
    public YuvColorMatrix SuggestedYuvColorMatrix => (YuvColorMatrix)_suggestedMatrix;
    /// <inheritdoc/>
    public YuvColorRange  SuggestedYuvColorRange  => (YuvColorRange)_suggestedRange;

    // Synthetic PTS fallback when NDI timestamps are undefined (0, negative, or MaxValue).
    // Uses a monotonic stopwatch so the mixer can pace frames correctly.
    private readonly Stopwatch _syntheticClock = new();
    private bool _syntheticClockStarted;
    private bool _hasLastPts;
    private double _lastPtsSeconds;

    public Guid  Id      { get; } = Guid.NewGuid();
    public bool  IsOpen  => !_disposed;
    public bool  CanSeek => false;
    public int   BufferDepth     => _ringCapacity;
    public int   BufferAvailable => (int)Math.Max(0, Interlocked.Read(ref _framesInRing));

#pragma warning disable CS0067  // NDI streams have no defined EOF; event may be used in future
    public event EventHandler? EndOfStream;
#pragma warning restore CS0067
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <inheritdoc/>
    public VideoFormat SourceFormat { get { lock (_formatLock) return _sourceFormat; } }

    /// <inheritdoc/>
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));

    public NDIVideoChannel(NDIFrameSync frameSync, NDIClock clock, Lock? frameSyncGate = null, int bufferDepth = 4, bool preferLowLatency = false)
    {
        _frameSync = frameSync;
        _frameSyncGate = frameSyncGate ?? new Lock();
        _clock     = clock;
        _ringCapacity = Math.Max(1, bufferDepth);
        _preferLowLatency = preferLowLatency;
        _waitPollMs = _preferLowLatency ? 2 : 10;

        var ring = Channel.CreateUnbounded<VideoFrame>(
            new UnboundedChannelOptions
            {
                SingleReader = true,   // only FillBuffer reads — enables the faster lock-free path
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        _ringReader = ring.Reader;
        _ringWriter = ring.Writer;

        Log.LogInformation("Created NDIVideoChannel: bufferDepth={BufferDepth}", _ringCapacity);
    }

    public void StartCapture()
    {
        Log.LogInformation("Starting NDIVideoChannel capture thread");
        _captureThread = new Thread(CaptureLoop)
        {
            Name         = "NDIVideoChannel.Capture",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    /// <summary>
    /// Waits asynchronously until the ring contains at least <paramref name="minFrames"/> captured
    /// video frames. Call this after <see cref="StartCapture"/> and before starting playback to
    /// align the video pre-buffer depth with the audio pre-buffer, preventing A/V drift at startup.
    /// </summary>
    public async Task WaitForBufferAsync(int minFrames, CancellationToken ct = default)
    {
        long target = Math.Clamp(minFrames, 1, _ringCapacity);
        while (Interlocked.Read(ref _framesInRing) < target && !ct.IsCancellationRequested)
            await Task.Delay(_waitPollMs, ct).ConfigureAwait(false);
    }

    private void CaptureLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                NDIVideoFrameV2 frame;
                lock (_frameSyncGate)
                    _frameSync.CaptureVideo(out frame);

                // When PData is null the framesync has no frame yet — nothing to free.
                if (frame.PData == nint.Zero)
                {
                    Thread.Sleep(5);
                    continue;
                }

                // Guard: framesync returned a frame buffer but dimensions are not ready.
                // We MUST still free it; skipping FreeVideo leaks the internal NDI buffer.
                if (frame.Xres == 0 || frame.Yres == 0)
                {
                    lock (_frameSyncGate)
                        _frameSync.FreeVideo(frame);
                    Thread.Sleep(5);
                    continue;
                }

                _clock.UpdateFromFrame(frame.Timestamp);

                if (!TryCopyFrameToTightBuffer(frame, out var pixFmt, out var rented, out var totalBytes))
                {
                    lock (_frameSyncGate)
                        _frameSync.FreeVideo(frame);
                    continue;
                }

                lock (_frameSyncGate)
                    _frameSync.FreeVideo(frame); // release NDI buffer as soon as data is copied
                var owner  = new NDIVideoFrameOwner(rented);

                // Use the NDI timestamp when available; fall back to a local monotonic
                // clock when the source provides undefined timestamps (0, negative, or
                // long.MaxValue / NDIlib_recv_timestamp_undefined).
                // Without a valid advancing PTS the VideoMixer's drop-lag logic treats
                // every frame as stale and drops them all, causing a frozen first frame.
                bool hasValidTimestamp = frame.Timestamp > 0 && frame.Timestamp != long.MaxValue;
                double tsSecs = hasValidTimestamp
                    ? frame.Timestamp / 10_000_000.0
                    : GetSyntheticPtsSeconds();

                // Some NDI sources emit non-monotonic or discontinuous timestamps
                // around reconnect/format transitions. Those values make the mixer
                // treat new frames as stale and pin output to an old frame.
                if (_hasLastPts)
                {
                    const double MaxForwardJumpSeconds = 0.75;
                    if (tsSecs <= _lastPtsSeconds || (tsSecs - _lastPtsSeconds) > MaxForwardJumpSeconds)
                        tsSecs = GetSyntheticPtsSeconds();
                }

                _lastPtsSeconds = tsSecs;
                _hasLastPts = true;

                var vf = new VideoFrame(
                    frame.Xres, frame.Yres,
                    pixFmt,
                    rented.AsMemory(0, totalBytes),
                    TimeSpan.FromSeconds(tsSecs),
                    owner);

                // Update source format from the live stream dimensions / pixel format.
                int fpsNum = frame.FrameRateN > 0 ? frame.FrameRateN : 30000;
                int fpsDen = frame.FrameRateD > 0 ? frame.FrameRateD : 1001;
                lock (_formatLock)
                    _sourceFormat = new VideoFormat(frame.Xres, frame.Yres, pixFmt, fpsNum, fpsDen);

                // Extract YUV color-space hints from NDI frame metadata (if present).
                // NDI does not have a structured color-space field; senders embed optional
                // XML such as <ndi_color_space colorspace="BT.709" range="Limited"/>.
                var (matrix, range) = ParseNdiColorMeta(frame.Metadata);
                _suggestedMatrix = (int)matrix;
                _suggestedRange  = (int)range;

                EnqueueFrame(vf);

                // Throttle: sleep for roughly ¼ of a frame interval so the capture loop
                // wakes up ~4× per frame, keeping end-to-end video latency under ~5 ms
                // while still being gentle on the CPU (a 1 ms floor prevents busy-spinning
                // on high-frame-rate or unknown-rate sources).
                double fpsNow;
                lock (_formatLock) fpsNow = _sourceFormat.FrameRate;
                int sleepMs = fpsNow > 0
                    ? Math.Max(1, (int)(250.0 / fpsNow))   // ¼ frame interval, min 1 ms
                    : 4;

                // OPT-7: In low-latency mode, use Stopwatch-based spin-wait instead of
                // Thread.Sleep(1) which has 1–4 ms granularity on Linux.  This yields
                // microsecond-accurate wakeup, cutting up to ~3 ms of jitter per frame.
                if (_preferLowLatency)
                {
                    long waitTicks = Stopwatch.Frequency * sleepMs / 1000;
                    long deadline = Stopwatch.GetTimestamp() + waitTicks;
                    // Coarse sleep for anything > ~3 ms to keep CPU gentle.
                    if (sleepMs > 3)
                        Thread.Sleep(sleepMs - 3);
                    // Spin-wait the remaining tail for microsecond accuracy.
                    while (Stopwatch.GetTimestamp() < deadline)
                        Thread.SpinWait(20);
                }
                else
                {
                    Thread.Sleep(sleepMs);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // Swallow per-frame errors so a transient bad frame does not
                // crash the background thread (and consequently the process).
                Log.LogWarning(ex, "NDIVideoChannel capture-loop error, retrying");
                Thread.Sleep(10);
            }
        }
    }

    private double GetSyntheticPtsSeconds()
    {
        if (!_syntheticClockStarted)
        {
            _syntheticClock.Start();
            _syntheticClockStarted = true;
        }

        return _syntheticClock.Elapsed.TotalSeconds;
    }

    private bool TryCopyFrameToTightBuffer(
        in NDIVideoFrameV2 frame,
        out PixelFormat pixelFormat,
        out byte[] rented,
        out int totalBytes)
    {
        pixelFormat = default;
        rented = Array.Empty<byte>();
        totalBytes = 0;

        if (!TryMapFourCc(frame.FourCC, out pixelFormat))
        {
            if (_unsupportedFourCcLogged.Add(frame.FourCC))
            {
                Log.LogWarning("NDIVideoChannel unsupported FourCC={FourCC} ({FourCCInt}) x={Width} y={Height} stride={Stride}; frame dropped",
                    frame.FourCC, (uint)frame.FourCC, frame.Xres, frame.Yres, frame.LineStrideInBytes);
            }
            return false;
        }

        return pixelFormat switch
        {
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => CopyPacked(frame, bytesPerPixel: 4, out rented, out totalBytes),
            PixelFormat.Uyvy422 => CopyPacked(frame, bytesPerPixel: 2, out rented, out totalBytes),
            PixelFormat.Nv12 => CopyNv12(frame, out rented, out totalBytes),
            PixelFormat.Yuv420p => CopyI420(frame, out rented, out totalBytes),
            _ => false,
        };
    }

    private static bool TryMapFourCc(NDIFourCCVideoType fourCc, out PixelFormat pixelFormat)
    {
        pixelFormat = fourCc switch
        {
            NDIFourCCVideoType.Bgra => PixelFormat.Bgra32,
            NDIFourCCVideoType.Bgrx => PixelFormat.Bgra32,
            NDIFourCCVideoType.Rgba => PixelFormat.Rgba32,
            NDIFourCCVideoType.Rgbx => PixelFormat.Rgba32,
            NDIFourCCVideoType.Uyvy => PixelFormat.Uyvy422,
            // Local preview path ignores UYVA alpha and consumes the packed UYVY color plane.
            NDIFourCCVideoType.Uyva => PixelFormat.Uyvy422,
            NDIFourCCVideoType.Nv12 => PixelFormat.Nv12,
            NDIFourCCVideoType.I420 => PixelFormat.Yuv420p,
            NDIFourCCVideoType.Yv12 => PixelFormat.Yuv420p,
            _ => default,
        };

        return fourCc is NDIFourCCVideoType.Bgra
            or NDIFourCCVideoType.Bgrx
            or NDIFourCCVideoType.Rgba
            or NDIFourCCVideoType.Rgbx
            or NDIFourCCVideoType.Uyvy
            or NDIFourCCVideoType.Uyva
            or NDIFourCCVideoType.Nv12
            or NDIFourCCVideoType.I420
            or NDIFourCCVideoType.Yv12;
    }

    private static bool CopyPacked(in NDIVideoFrameV2 frame, int bytesPerPixel, out byte[] rented, out int totalBytes)
    {
        int rowBytes = checked(frame.Xres * bytesPerPixel);
        int srcStride = frame.LineStrideInBytes > 0 ? frame.LineStrideInBytes : rowBytes;
        totalBytes = checked(rowBytes * frame.Yres);
        rented = ArrayPool<byte>.Shared.Rent(totalBytes);

        if (srcStride == rowBytes)
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.PData, rented, 0, totalBytes);
            return true;
        }

        for (int y = 0; y < frame.Yres; y++)
        {
            nint src = frame.PData + (y * srcStride);
            System.Runtime.InteropServices.Marshal.Copy(src, rented, y * rowBytes, rowBytes);
        }
        return true;
    }

    private static bool CopyNv12(in NDIVideoFrameV2 frame, out byte[] rented, out int totalBytes)
    {
        int w = frame.Xres;
        int h = frame.Yres;
        int yRowBytes = w;
        int uvRowBytes = w;
        int chromaRows = (h + 1) / 2;
        int yStride = frame.LineStrideInBytes > 0 ? frame.LineStrideInBytes : yRowBytes;
        int uvStride = yStride;

        totalBytes = checked((yRowBytes * h) + (uvRowBytes * chromaRows));
        rented = ArrayPool<byte>.Shared.Rent(totalBytes);

        for (int y = 0; y < h; y++)
        {
            nint src = frame.PData + (y * yStride);
            System.Runtime.InteropServices.Marshal.Copy(src, rented, y * yRowBytes, yRowBytes);
        }

        int dstUvStart = yRowBytes * h;
        nint srcUvBase = frame.PData + (yStride * h);
        for (int y = 0; y < chromaRows; y++)
        {
            nint src = srcUvBase + (y * uvStride);
            System.Runtime.InteropServices.Marshal.Copy(src, rented, dstUvStart + (y * uvRowBytes), uvRowBytes);
        }

        return true;
    }

    private static bool CopyI420(in NDIVideoFrameV2 frame, out byte[] rented, out int totalBytes)
    {
        int w = frame.Xres;
        int h = frame.Yres;
        int yRowBytes = w;
        int uvRowBytes = Math.Max(1, (w + 1) / 2);
        int chromaRows = Math.Max(1, (h + 1) / 2);
        int yStride = frame.LineStrideInBytes > 0 ? frame.LineStrideInBytes : yRowBytes;
        int uvStride = Math.Max(uvRowBytes, yStride / 2);

        int yBytes = yRowBytes * h;
        int uvBytes = uvRowBytes * chromaRows;
        totalBytes = checked(yBytes + uvBytes + uvBytes);
        rented = ArrayPool<byte>.Shared.Rent(totalBytes);

        for (int y = 0; y < h; y++)
        {
            nint src = frame.PData + (y * yStride);
            System.Runtime.InteropServices.Marshal.Copy(src, rented, y * yRowBytes, yRowBytes);
        }

        nint srcPlane0 = frame.PData + (yStride * h);
        nint srcPlane1 = srcPlane0 + (uvStride * chromaRows);

        int dstUStart = yBytes;
        int dstVStart = yBytes + uvBytes;

        bool yv12 = frame.FourCC == NDIFourCCVideoType.Yv12;
        nint srcUBase = yv12 ? srcPlane1 : srcPlane0;
        nint srcVBase = yv12 ? srcPlane0 : srcPlane1;

        for (int y = 0; y < chromaRows; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(srcUBase + (y * uvStride), rented, dstUStart + (y * uvRowBytes), uvRowBytes);
            System.Runtime.InteropServices.Marshal.Copy(srcVBase + (y * uvStride), rented, dstVStart + (y * uvRowBytes), uvRowBytes);
        }

        return true;
    }

    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        int filled = 0;
        for (int i = 0; i < frameCount; i++)
        {
            if (!_ringReader.TryRead(out var vf)) break;
            Interlocked.Decrement(ref _framesInRing);
            dest[i] = vf;
            Volatile.Write(ref _positionTicks, vf.Pts.Ticks);
            Interlocked.Increment(ref _framesDequeued);
            filled++;
        }
        if (filled == 0 && Interlocked.Read(ref _framesDequeued) > 0)
            RaiseBufferUnderrun();
        return filled;
    }

    private void RaiseBufferUnderrun()
    {
        var handler = BufferUnderrun;
        if (handler == null) return;
        var pos = Position;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h, p) = ((NDIVideoChannel, EventHandler<BufferUnderrunEventArgs>, TimeSpan))s!;
            h(self, new BufferUnderrunEventArgs(p, 0));
        }, (this, handler, pos));
    }

    public void Seek(TimeSpan position) { /* NDI live sources cannot seek */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing NDIVideoChannel");
        _cts.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();
        while (_ringReader.TryRead(out var frame))
            frame.MemoryOwner?.Dispose();
    }

    private void EnqueueFrame(in VideoFrame frame)
    {
        // Drop oldest (with proper disposal) when at capacity.
        if (Interlocked.Read(ref _framesInRing) >= _ringCapacity)
        {
            if (_ringReader.TryRead(out var dropped))
            {
                Interlocked.Decrement(ref _framesInRing);
                dropped.MemoryOwner?.Dispose();
            }
        }

        // TryWrite on an unbounded channel never fails.
        _ringWriter.TryWrite(frame);
        Interlocked.Increment(ref _framesInRing);
    }

    /// <summary>
    /// Parses the optional NDI frame metadata XML for YUV color-space information.
    /// NDI carries this as XML text in the <c>PMetadata</c> field, e.g.:
    /// <c>&lt;ndi_color_space colorspace="BT.709" range="Limited"/&gt;</c>.
    /// Returns <see cref="YuvColorMatrix.Auto"/> / <see cref="YuvColorRange.Auto"/> when
    /// no metadata is present or the tag is absent — the renderer will fall back to the
    /// resolution-based heuristic in <see cref="YuvAutoPolicy"/>.
    /// </summary>
    private static (YuvColorMatrix Matrix, YuvColorRange Range) ParseNdiColorMeta(string? xml)
    {
        if (xml is null) return (YuvColorMatrix.Auto, YuvColorRange.Auto);

        var matrix = xml.Contains("BT.2020", StringComparison.OrdinalIgnoreCase)
            ? YuvColorMatrix.Bt2020
            : xml.Contains("BT.709", StringComparison.OrdinalIgnoreCase)
                ? YuvColorMatrix.Bt709
                : xml.Contains("BT.601", StringComparison.OrdinalIgnoreCase)
                    ? YuvColorMatrix.Bt601
                    : YuvColorMatrix.Auto;

        var range = xml.Contains("Full", StringComparison.OrdinalIgnoreCase)
            ? YuvColorRange.Full
            : xml.Contains("Limited", StringComparison.OrdinalIgnoreCase)
                ? YuvColorRange.Limited
                : YuvColorRange.Auto;

        return (matrix, range);
    }
}

/// <summary>
/// Wraps an <see cref="ArrayPool{T}"/> rental for a video frame byte buffer.
/// Passed as <see cref="VideoFrame.MemoryOwner"/>; the consumer must call
/// <see cref="Dispose"/> once the frame data is no longer needed.
/// </summary>
internal sealed class NDIVideoFrameOwner(byte[] array) : IDisposable
{
    private byte[]? _array = array;

    public void Dispose()
    {
        var arr = Interlocked.Exchange(ref _array, null);
        if (arr is not null) ArrayPool<byte>.Shared.Return(arr);
    }
}

