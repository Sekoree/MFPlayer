using System.Buffers;
using NDILib;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IMediaChannel{VideoFrame}"/> that pulls video from an NDI source via
/// <see cref="NDIFrameSync.CaptureVideo"/>. Frames are exposed as BGRA32 <see cref="VideoFrame"/> records.
/// </summary>
public sealed class NDIVideoChannel : IMediaChannel<VideoFrame>
{
    private readonly NDIFrameSync  _frameSync;
    private readonly NDIClock      _clock;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    private readonly System.Threading.Channels.Channel<VideoFrame>       _ring;
    private readonly System.Threading.Channels.ChannelReader<VideoFrame> _ringReader;
    private readonly System.Threading.Channels.ChannelWriter<VideoFrame> _ringWriter;

    private bool _disposed;

    public Guid  Id      { get; } = Guid.NewGuid();
    public bool  IsOpen  => !_disposed;
    public bool  CanSeek => false;

    public NDIVideoChannel(NDIFrameSync frameSync, NDIClock clock, int bufferDepth = 4)
    {
        _frameSync = frameSync;
        _clock     = clock;

        _ring = System.Threading.Channels.Channel.CreateBounded<VideoFrame>(
            new System.Threading.Channels.BoundedChannelOptions(bufferDepth)
            {
                FullMode     = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        _ringReader = _ring.Reader;
        _ringWriter = _ring.Writer;
    }

    public void StartCapture()
    {
        _captureThread = new Thread(CaptureLoop)
        {
            Name         = "NDIVideoChannel.Capture",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private unsafe void CaptureLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _frameSync.CaptureVideo(out var frame);

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
                    _frameSync.FreeVideo(frame);
                    Thread.Sleep(5);
                    continue;
                }

                _clock.UpdateFromFrame(frame.Timestamp);

                // Use LineStrideInBytes for the correct total buffer size.
                // Hardcoding `Xres * Yres * 4` is WRONG for non-BGRA formats such as
                // UYVY (2 bytes/pixel), which is what NDI sends when ColorFormat = Fastest.
                // Reading past the end of the native buffer causes a SIGSEGV.
                int stride     = frame.LineStrideInBytes > 0
                    ? frame.LineStrideInBytes
                    : frame.Xres * 4; // BGRA32 fallback
                int totalBytes = frame.Yres * stride;

                // Rent from ArrayPool to avoid a per-frame heap allocation.
                // The consumer must call frame.MemoryOwner?.Dispose() when done.
                var rented = ArrayPool<byte>.Shared.Rent(totalBytes);
                var owner  = new NDIVideoFrameOwner(rented);
                System.Runtime.InteropServices.Marshal.Copy(frame.PData, rented, 0, totalBytes);

                _frameSync.FreeVideo(frame); // release NDI buffer as soon as data is copied

                // Map NDI FourCC to our PixelFormat enum.
                var pixFmt = frame.FourCC switch
                {
                    NdiFourCCVideoType.Bgra => Core.Media.PixelFormat.Bgra32,
                    NdiFourCCVideoType.Bgrx => Core.Media.PixelFormat.Bgra32,
                    NdiFourCCVideoType.Rgba => Core.Media.PixelFormat.Rgba32,
                    NdiFourCCVideoType.Rgbx => Core.Media.PixelFormat.Rgba32,
                    NdiFourCCVideoType.Uyvy => Core.Media.PixelFormat.Uyvy422,
                    NdiFourCCVideoType.Nv12 => Core.Media.PixelFormat.Nv12,
                    _                       => Core.Media.PixelFormat.Bgra32,
                };

                double tsSecs = frame.Timestamp > 0 && frame.Timestamp != long.MaxValue
                    ? frame.Timestamp / 10_000_000.0
                    : 0.0;

                var vf = new VideoFrame(
                    frame.Xres, frame.Yres,
                    pixFmt,
                    rented.AsMemory(0, totalBytes),
                    TimeSpan.FromSeconds(tsSecs),
                    owner);

                _ringWriter.TryWrite(vf);

                // Throttle to avoid calling CaptureVideo thousands of times per second.
                // At 30 fps video a ~16 ms sleep is enough; 8 ms gives headroom for 60 fps.
                Thread.Sleep(8);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Swallow per-frame errors so a transient bad frame does not
                // crash the background thread (and consequently the process).
                Thread.Sleep(10);
            }
        }
    }

    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        int filled = 0;
        for (int i = 0; i < frameCount; i++)
        {
            if (!_ringReader.TryRead(out var vf)) break;
            dest[i] = vf;
            filled++;
        }
        return filled;
    }

    public void Seek(TimeSpan position) { /* NDI live sources cannot seek */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();
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

