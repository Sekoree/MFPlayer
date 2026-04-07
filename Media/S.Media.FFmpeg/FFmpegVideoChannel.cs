using System.Threading.Channels;
using FFmpeg.AutoGen;
using S.Media.Core.Media;

namespace S.Media.FFmpeg;

/// <summary>
/// Decodes a single video stream into <see cref="VideoFrame"/> objects via a background
/// thread. Each frame is pixel-format-converted to <see cref="Core.Media.PixelFormat.Bgra32"/>
/// by default. Frames are exposed through the <see cref="IMediaChannel{VideoFrame}"/> pull interface.
/// </summary>
public sealed unsafe class FFmpegVideoChannel : IMediaChannel<VideoFrame>
{
    private readonly int                          _streamIndex;
    private readonly AVStream*                    _stream;
    private readonly ChannelReader<EncodedPacket> _packetReader;

    private AVCodecContext* _codecCtx;
    private SwsContext*     _sws;
    private AVFrame*        _frame;
    private AVFrame*        _rgbFrame;
    private AVPacket*       _pkt;
    private byte[]          _frameBuffer = [];

    private Thread?                  _decodeThread;
    private CancellationTokenSource  _cts = new();

    private readonly Channel<VideoFrame>       _ring;
    private readonly ChannelReader<VideoFrame> _ringReader;
    private readonly ChannelWriter<VideoFrame> _ringWriter;

    private bool _disposed;

    public Guid  Id      { get; } = Guid.NewGuid();
    public bool  IsOpen  => !_disposed;
    public bool  CanSeek => true;

    /// <summary>Target pixel format. Defaults to Bgra32.</summary>
    public Core.Media.PixelFormat TargetPixelFormat { get; }

    /// <summary>Video format of the stream (may be updated after first decoded frame).</summary>
    public VideoFormat Format { get; private set; }

    internal FFmpegVideoChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 Core.Media.PixelFormat targetPixelFormat = Core.Media.PixelFormat.Bgra32,
                                 int bufferDepth = 4)
    {
        _streamIndex        = streamIndex;
        _stream             = stream;
        _packetReader       = packetReader;
        TargetPixelFormat   = targetPixelFormat;

        var cp = stream->codecpar;
        Format = new VideoFormat(cp->width, cp->height, targetPixelFormat,
            (int)stream->r_frame_rate.num, (int)stream->r_frame_rate.den);

        _ring = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(bufferDepth)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        _ringReader = _ring.Reader;
        _ringWriter = _ring.Writer;

        OpenCodec();
    }

    private void OpenCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Video codec not found.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecCtx, _stream->codecpar);
        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

        _frame    = ffmpeg.av_frame_alloc();
        _rgbFrame = ffmpeg.av_frame_alloc();
        _pkt      = ffmpeg.av_packet_alloc();
    }

    private SwsContext* GetSws(int w, int h, AVPixelFormat srcFmt)
    {
        if (_sws != null) return _sws;
        var dstFmt = MapPixelFormat(TargetPixelFormat);
        _sws = ffmpeg.sws_getContext(w, h, srcFmt, w, h, dstFmt,
            2 /* SWS_BILINEAR */, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getContext failed.");

        int bufSize = ffmpeg.av_image_get_buffer_size(dstFmt, w, h, 1);
        _frameBuffer = new byte[bufSize];
        return _sws;
    }

    internal void StartDecoding()
    {
        _decodeThread = new Thread(DecodeLoop)
        {
            Name         = $"FFmpegVideo[{_streamIndex}].Decode",
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal
        };
        _decodeThread.Start();
    }

    private void DecodeLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            EncodedPacket? ep;
            try { ep = _packetReader.ReadAsync(token).AsTask().GetAwaiter().GetResult(); }
            catch { break; }

            if (ep.IsFlush)
            {
                ffmpeg.avcodec_flush_buffers(_codecCtx);
                continue;
            }

            fixed (byte* p = ep.Data)
            {
                _pkt->data     = ep.Data.Length > 0 ? p : null;
                _pkt->size     = ep.Data.Length;
                _pkt->pts      = ep.Pts;
                _pkt->dts      = ep.Dts;
                _pkt->duration = ep.Duration;
            }

            if (ffmpeg.avcodec_send_packet(_codecCtx, _pkt) < 0) continue;

            while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
            {
                var vf = ConvertFrame();
                if (vf.HasValue)
                    _ringWriter.WriteAsync(vf.Value, token).AsTask().GetAwaiter().GetResult();
                ffmpeg.av_frame_unref(_frame);
            }
        }
        _ringWriter.TryComplete();
    }

    private VideoFrame? ConvertFrame()
    {
        int w = _frame->width, h = _frame->height;
        if (w == 0 || h == 0) return null;

        var sws = GetSws(w, h, (AVPixelFormat)_frame->format);
        var buf = new byte[_frameBuffer.Length];

        // Build managed arrays for sws_scale (AutoGen expects byte*[] and int[])
        var srcData   = new byte*[4];
        var srcStride = new int[4];
        var dstData   = new byte*[4];
        var dstStride = new int[4];

        for (uint i = 0; i < 4; i++)
        {
            srcData[i]   = _frame->data[i];
            srcStride[i] = _frame->linesize[i];
        }

        fixed (byte* pBuf = buf)
        {
            dstData[0]   = pBuf;
            dstStride[0] = w * 4; // 4 bytes/pixel for BGRA/RGBA
            ffmpeg.sws_scale(sws, srcData, srcStride, 0, h, dstData, dstStride);
        }

        double tbSeconds = _stream->time_base.num / (double)_stream->time_base.den;
        var pts = TimeSpan.FromSeconds(_frame->pts * tbSeconds);

        return new VideoFrame(w, h, TargetPixelFormat, buf, pts);
    }

    // ── IMediaChannel<VideoFrame> pull ────────────────────────────────────

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

    public void Seek(TimeSpan position)
    {
        while (_ringReader.TryRead(out _)) { }
    }

    internal void FlushAfterSeek()
    {
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        Seek(TimeSpan.Zero);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static AVPixelFormat MapPixelFormat(Core.Media.PixelFormat pf) => pf switch
    {
        Core.Media.PixelFormat.Bgra32  => AVPixelFormat.AV_PIX_FMT_BGRA,
        Core.Media.PixelFormat.Rgba32  => AVPixelFormat.AV_PIX_FMT_RGBA,
        Core.Media.PixelFormat.Nv12    => AVPixelFormat.AV_PIX_FMT_NV12,
        Core.Media.PixelFormat.Yuv420p => AVPixelFormat.AV_PIX_FMT_YUV420P,
        Core.Media.PixelFormat.Uyvy422 => AVPixelFormat.AV_PIX_FMT_UYVY422,
        _                              => AVPixelFormat.AV_PIX_FMT_BGRA
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _decodeThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();

        if (_frame    != null) fixed (AVFrame**        pp = &_frame)    ffmpeg.av_frame_free(pp);
        if (_rgbFrame != null) fixed (AVFrame**        pp = &_rgbFrame) ffmpeg.av_frame_free(pp);
        if (_pkt      != null) fixed (AVPacket**       pp = &_pkt)      ffmpeg.av_packet_free(pp);
        if (_codecCtx != null) fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
        if (_sws      != null) ffmpeg.sws_freeContext(_sws);
    }
}

