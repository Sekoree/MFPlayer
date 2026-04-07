using NDILib;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IMediaChannel{VideoFrame}"/> that pulls video from an NDI source via
/// <see cref="NDIFrameSync.CaptureVideo"/>. Frames are exposed as BGRA32 <see cref="VideoFrame"/> records.
/// </summary>
public sealed class NdiVideoChannel : IMediaChannel<VideoFrame>
{
    private readonly NDIFrameSync  _frameSync;
    private readonly NdiClock      _clock;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    private readonly System.Threading.Channels.Channel<VideoFrame>       _ring;
    private readonly System.Threading.Channels.ChannelReader<VideoFrame> _ringReader;
    private readonly System.Threading.Channels.ChannelWriter<VideoFrame> _ringWriter;

    private bool _disposed;

    public Guid  Id      { get; } = Guid.NewGuid();
    public bool  IsOpen  => !_disposed;
    public bool  CanSeek => false;

    public NdiVideoChannel(NDIFrameSync frameSync, NdiClock clock, int bufferDepth = 4)
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
            Name         = "NdiVideoChannel.Capture",
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
            _frameSync.CaptureVideo(out var frame);

            if (frame.Xres == 0 || frame.Yres == 0) { Thread.Sleep(1); continue; }

            _clock.UpdateFromFrame(frame.Timestamp);

            // NDI BGRA is already BGRA32 — copy the data.
            int size = frame.Xres * frame.Yres * 4;
            var buf  = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(frame.PData, buf, 0, size);

            double tsSecs = frame.Timestamp / 10_000_000.0;
            var vf = new VideoFrame(
                frame.Xres, frame.Yres,
                Core.Media.PixelFormat.Bgra32,
                buf,
                TimeSpan.FromSeconds(tsSecs));

            _ringWriter.TryWrite(vf);
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

