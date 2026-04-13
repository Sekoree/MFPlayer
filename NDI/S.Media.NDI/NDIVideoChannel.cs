using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IVideoChannel"/> that pulls video from an NDI source via
/// <see cref="NDIFrameSync.CaptureVideo"/>. Frames are exposed as BGRA32 <see cref="VideoFrame"/> records.
/// </summary>
internal sealed class NDIVideoChannel : IVideoChannel
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIVideoChannel));

    private readonly NDIFrameSync  _frameSync;
    private readonly Lock          _frameSyncGate;
    private readonly NDIClock      _clock;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    private readonly Queue<VideoFrame> _ring = new();
    private readonly Lock _ringGate = new();
    private readonly int _ringCapacity;

    private bool _disposed;
    private VideoFormat _sourceFormat;
    private readonly Lock _formatLock = new();
    private readonly HashSet<NDIFourCCVideoType> _unsupportedFourCcLogged = [];
    private long _positionTicks;

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
    public int   BufferAvailable { get { lock (_ringGate) return _ring.Count; } }

#pragma warning disable CS0067  // NDI streams have no defined EOF; event may be used in future
    public event EventHandler? EndOfStream;
#pragma warning restore CS0067

    /// <inheritdoc/>
    public VideoFormat SourceFormat { get { lock (_formatLock) return _sourceFormat; } }

    /// <inheritdoc/>
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));

    public NDIVideoChannel(NDIFrameSync frameSync, NDIClock clock, Lock? frameSyncGate = null, int bufferDepth = 4)
    {
        _frameSync = frameSync;
        _frameSyncGate = frameSyncGate ?? new Lock();
        _clock     = clock;
        _ringCapacity = Math.Max(1, bufferDepth);
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

                EnqueueFrame(vf);

                // Throttle to avoid calling CaptureVideo thousands of times per second.
                // At 30 fps video a ~16 ms sleep is enough; 8 ms gives headroom for 60 fps.
                Thread.Sleep(8);
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
            if (!TryDequeueFrame(out var vf)) break;
            dest[i] = vf;
            Volatile.Write(ref _positionTicks, vf.Pts.Ticks);
            filled++;
        }
        return filled;
    }

    public void Seek(TimeSpan position) { /* NDI live sources cannot seek */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing NDIVideoChannel");
        _cts.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        lock (_ringGate)
        {
            while (_ring.Count > 0)
            {
                var frame = _ring.Dequeue();
                frame.MemoryOwner?.Dispose();
            }
        }
    }

    private void EnqueueFrame(in VideoFrame frame)
    {
        lock (_ringGate)
        {
            if (_ring.Count >= _ringCapacity)
            {
                var dropped = _ring.Dequeue();
                dropped.MemoryOwner?.Dispose();
            }

            _ring.Enqueue(frame);
        }
    }

    private bool TryDequeueFrame(out VideoFrame frame)
    {
        lock (_ringGate)
        {
            if (_ring.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _ring.Dequeue();
            return true;
        }
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

