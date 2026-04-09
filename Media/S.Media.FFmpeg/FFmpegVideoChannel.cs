using System.Buffers;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Decodes a single video stream into <see cref="VideoFrame"/> objects via a background
/// thread. Each frame is pixel-format-converted to <see cref="Core.Media.PixelFormat.Bgra32"/>
/// by default. Frames are exposed through the <see cref="IMediaChannel{VideoFrame}"/> pull interface.
/// </summary>
public sealed unsafe class FFmpegVideoChannel : IVideoChannel
{
    private readonly int                          _streamIndex;
    private readonly AVStream*                    _stream;
    private readonly ChannelReader<EncodedPacket> _packetReader;
    private readonly AVBufferRef*                 _hwDeviceCtx;   // null = sw only
    private readonly int                          _threadCount;

    private AVCodecContext* _codecCtx;
    private SwsContext*     _sws;
    private AVFrame*        _frame;
    private AVFrame*        _rgbFrame;
    private AVFrame*        _swFrame;   // temporary CPU-side frame when using hw decode
    private AVPacket*       _pkt;
    private int             _swsBufSize; // byte size of one converted frame

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
    public PixelFormat TargetPixelFormat { get; }

    /// <summary>Video format of the stream (may be updated after first decoded frame).</summary>
    public VideoFormat Format { get; private set; }

    /// <inheritdoc/>
    public VideoFormat SourceFormat => Format;

    /// <inheritdoc/>
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
    private long _positionTicks;

    internal FFmpegVideoChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 AVBufferRef*   hwDeviceCtx       = null,
                                 PixelFormat    targetPixelFormat = PixelFormat.Bgra32,
                                 int            threadCount       = 0,
                                 int            bufferDepth       = 4)
    {
        _streamIndex        = streamIndex;
        _stream             = stream;
        _packetReader       = packetReader;
        _hwDeviceCtx        = hwDeviceCtx;
        _threadCount        = threadCount;
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
        if (_threadCount >= 0)
            _codecCtx->thread_count = _threadCount;

        // Attach hardware device context if provided.
        if (_hwDeviceCtx != null)
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);

        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

        _frame    = ffmpeg.av_frame_alloc();
        _rgbFrame = ffmpeg.av_frame_alloc();
        _swFrame  = ffmpeg.av_frame_alloc();
        _pkt      = ffmpeg.av_packet_alloc();
    }

    private SwsContext* GetSws(int w, int h, AVPixelFormat srcFmt)
    {
        if (_sws != null) return _sws;
        var dstFmt = MapPixelFormat(TargetPixelFormat);
        _sws = ffmpeg.sws_getContext(w, h, srcFmt, w, h, dstFmt,
            2 /* SWS_BILINEAR */, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getContext failed.");

        _swsBufSize = ffmpeg.av_image_get_buffer_size(dstFmt, w, h, 1);
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
                _pkt->data     = ep.ActualLength > 0 ? p : null;
                _pkt->size     = ep.ActualLength;
                _pkt->pts      = ep.Pts;
                _pkt->dts      = ep.Dts;
                _pkt->duration = ep.Duration;
            }

            if (ffmpeg.avcodec_send_packet(_codecCtx, _pkt) < 0) continue;

            // Return the rented packet buffer now that it's been handed to the codec.
            if (ep.IsPooled)
                ArrayPool<byte>.Shared.Return(ep.Data);

            while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
            {
                // If the frame is in hw memory, transfer to CPU before converting.
                AVFrame* decodedFrame = _frame;
                bool transferred = false;
                if (_hwDeviceCtx != null && _frame->hw_frames_ctx != null)
                {
                    if (ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0) >= 0)
                    {
                        _swFrame->pts = _frame->pts;
                        decodedFrame  = _swFrame;
                        transferred   = true;
                    }
                }

                var vf = ConvertFrame(decodedFrame);
                if (vf.HasValue)
                {
                    var w = _ringWriter.WriteAsync(vf.Value, token);
                    if (!w.IsCompletedSuccessfully)
                    {
                        try { w.AsTask().GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                ffmpeg.av_frame_unref(_frame);
                if (transferred) ffmpeg.av_frame_unref(_swFrame);
            }
        }
        _ringWriter.TryComplete();
    }

    private VideoFrame? ConvertFrame(AVFrame* frame)
    {
        int w = frame->width, h = frame->height;
        if (w == 0 || h == 0) return null;

        var sws = GetSws(w, h, (AVPixelFormat)frame->format);

        double tbSeconds = _stream->time_base.num / (double)_stream->time_base.den;
        var pts = SafePts(frame->pts, tbSeconds);

        // Rent a buffer from the pool. The VideoFrame carries an ArrayPoolOwner so
        // the consumer can return it by calling frame.MemoryOwner?.Dispose().
        var rented = ArrayPool<byte>.Shared.Rent(_swsBufSize);
        var owner  = new ArrayPoolOwner<byte>(rented);

        // Build source data/stride arrays from the AVFrame (native pointers — safe
        // because FFmpeg owns the underlying allocation for the duration of this call).
        var srcDataArr   = new byte*[4] { frame->data[0], frame->data[1], frame->data[2], frame->data[3] };
        var srcStrideArr = new int[4]   { frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3] };

        fixed (byte* pBuf = rented)
        {
            var dstDataArr   = new byte*[4] { pBuf, null, null, null };
            var dstStrideArr = new int[4]   { w * BytesPerPixel(TargetPixelFormat), 0, 0, 0 };
            ffmpeg.sws_scale(sws, srcDataArr, srcStrideArr, 0, h, dstDataArr, dstStrideArr);
        }

        return new VideoFrame(w, h, TargetPixelFormat, rented.AsMemory(0, _swsBufSize), pts, owner);
    }

    /// <summary>
    /// Converts an FFmpeg PTS tick value to a <see cref="TimeSpan"/>, guarding against
    /// <c>AV_NOPTS_VALUE</c> (long.MinValue) and values that would overflow TimeSpan.
    /// </summary>
    internal static TimeSpan SafePts(long pts, double tbSeconds)
    {
        // AV_NOPTS_VALUE == unchecked((long)0x8000000000000000) == long.MinValue
        if (pts == long.MinValue || tbSeconds <= 0 || double.IsInfinity(tbSeconds))
            return TimeSpan.Zero;

        double seconds = pts * tbSeconds;
        if (double.IsNaN(seconds) || seconds < 0)
            return TimeSpan.Zero;
        if (seconds > TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.MaxValue;

        return TimeSpan.FromSeconds(seconds);
    }

    private static int BytesPerPixel(PixelFormat pf) => pf switch
    {
        PixelFormat.Yuv422p10 => 4, // packed as 2 bytes/component → stride = w*4 for luma plane
        PixelFormat.Nv12      => 1, // multi-plane; stride applies to luma only
        PixelFormat.Yuv420p   => 1,
        _                     => 4  // BGRA32, RGBA32, UYVY422 all 4 bytes/px
    };

    // ── IMediaChannel<VideoFrame> pull ────────────────────────────────────

    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        int filled = 0;
        for (int i = 0; i < frameCount; i++)
        {
            if (!_ringReader.TryRead(out var vf)) break;
            dest[i] = vf;
            Volatile.Write(ref _positionTicks, vf.Pts.Ticks);
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

    private static AVPixelFormat MapPixelFormat(PixelFormat pf) => pf switch
    {
        PixelFormat.Bgra32    => AVPixelFormat.AV_PIX_FMT_BGRA,
        PixelFormat.Rgba32    => AVPixelFormat.AV_PIX_FMT_RGBA,
        PixelFormat.Nv12      => AVPixelFormat.AV_PIX_FMT_NV12,
        PixelFormat.Yuv420p   => AVPixelFormat.AV_PIX_FMT_YUV420P,
        PixelFormat.Uyvy422   => AVPixelFormat.AV_PIX_FMT_UYVY422,
        PixelFormat.Yuv422p10 => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        _                     => AVPixelFormat.AV_PIX_FMT_BGRA
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
        if (_swFrame  != null) fixed (AVFrame**        pp = &_swFrame)  ffmpeg.av_frame_free(pp);
        if (_pkt      != null) fixed (AVPacket**       pp = &_pkt)      ffmpeg.av_packet_free(pp);
        if (_codecCtx != null) fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
        if (_sws      != null) ffmpeg.sws_freeContext(_sws);
    }
}

