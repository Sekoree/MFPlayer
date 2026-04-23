using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Decodes a single video stream into <see cref="VideoFrame"/> objects via a background
/// thread. Each frame is pixel-format-converted to <see cref="Core.Media.PixelFormat.Bgra32"/>
/// by default. Frames are exposed through the <see cref="IMediaChannel{VideoFrame}"/> pull interface.
/// </summary>
internal sealed unsafe class FFmpegVideoChannel : IVideoChannel, IVideoColorMatrixHint, IDecodableChannel
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegVideoChannel));
    private readonly AVStream*                    _stream;
    private readonly int                          _streamIndex;
    private readonly ChannelReader<EncodedPacket> _packetReader;
    private readonly AVBufferRef*                 _hwDeviceCtx;   // null = sw only
    private readonly int                          _threadCount;
    private readonly Func<int>                    _latestSeekEpochProvider;
    private readonly VideoFormat                  _nativeSourceFormat;

    private AVCodecContext* _codecCtx;
    private SwsContext*     _sws;
    private AVFrame*        _frame;
    private AVFrame*        _swFrame;   // temporary CPU-side frame when using hw decode
    private AVPacket*       _pkt;
    private int             _swsBufSize; // byte size of one converted frame
    private readonly byte*[] _srcDataArr = new byte*[4];
    private readonly int[] _srcStrideArr = new int[4];
    private readonly byte*[] _dstDataArr = new byte*[4];
    private readonly int[] _dstStrideArr = new int[4];

    private Task?                    _decodeTask;
    private CancellationTokenSource  _cts = new();

    // Per-subscriber fan-out: each subscriber owns a bounded queue. The decoder
    // publishes every converted frame to all active subscribers, ref-counting the
    // underlying pool rental so slow consumers don't leak and fast consumers don't
    // starve. Replaces the old single-ring / multi-reader model that caused the
    // pull/push frame-race bug documented in Doc/SDL3-Playback-Investigation.md.
    private readonly Lock _subsLock = new();
    private ImmutableArray<FFmpegVideoSubscription> _subs = ImmutableArray<FFmpegVideoSubscription>.Empty;
    // Lazy default subscription for legacy FillBuffer callers (tests, mostly).
    // AVRouter now uses Subscribe() explicitly.
    private FFmpegVideoSubscription? _defaultSub;

    private bool _disposed;
    private readonly int _bufferDepth;
    private long _framesDequeued;   // tracks whether any frame has ever been pulled
    private int  _hwTransferErrorLogged; // §3.6 / B22: log-once guard for av_hwframe_transfer_data failure

    public Guid  Id      { get; } = Guid.NewGuid();
    public bool  IsOpen  => !_disposed;
    public bool  CanSeek => true;

    public int BufferDepth     => _bufferDepth;
    public int BufferAvailable
    {
        get
        {
            // Report the max queued count across active subscriptions — best proxy for
            // "how much is buffered ahead" in a fan-out world.
            // §3.8 — local snapshot of the immutable array so a concurrent
            // Subscribe/Unsubscribe that swaps `_subs` cannot tear this loop.
            var subs = _subs;
            int max = 0;
            foreach (var s in subs)
            {
                int c = s.Count;
                if (c > max) max = c;
            }
            return max;
        }
    }

    /// <summary>
    /// §2.8 — raised on the demux worker thread once the last decoded frame has
    /// been published to subscribers. Handlers must not block; chain via
    /// <c>Task.Run</c>.
    /// </summary>
    public event EventHandler? EndOfStream;
    /// <summary>
    /// §2.8 — raised on the <see cref="ThreadPool"/> (detached from the render
    /// thread so handler work cannot stall the GPU upload path).
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>Target pixel format. Defaults to Bgra32.</summary>
    public PixelFormat TargetPixelFormat { get; }

    /// <summary>Video format of the stream (may be updated after first decoded frame).</summary>
    public VideoFormat Format { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the <em>native</em> codec pixel format read from the stream parameters,
    /// independent of <see cref="TargetPixelFormat"/> (which controls what format decoded
    /// frames are actually converted to).  Use this value to drive routing policy decisions
    /// such as <c>LocalVideoOutputRoutingPolicy.SelectLeaderPixelFormat</c>.
    /// </remarks>
    public VideoFormat SourceFormat => _nativeSourceFormat;

    /// <inheritdoc/>
    public YuvColorMatrix SuggestedYuvColorMatrix { get; }

    /// <inheritdoc/>
    public YuvColorRange SuggestedYuvColorRange { get; }

    /// <inheritdoc/>
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
    private long _positionTicks;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the last presented PTS plus one nominal frame period derived from the
    /// stream frame rate.  Used by <see cref="S.Media.Core.Routing.AVRouter.GetAvDrift"/>
    /// to interpolate video position between frame-boundary PTS updates — without this,
    /// any A/V drift readout sawtooths by ±½ frame period even on a perfectly synced stream.
    /// </remarks>
    public TimeSpan NextExpectedPts
    {
        get
        {
            long baseTicks = Volatile.Read(ref _positionTicks);
            int num = Format.FrameRateNumerator;
            int den = Format.FrameRateDenominator;
            if (num <= 0 || den <= 0) return TimeSpan.FromTicks(baseTicks);
            long framePeriodTicks = TimeSpan.TicksPerSecond * den / num;
            return TimeSpan.FromTicks(baseTicks + framePeriodTicks);
        }
    }

    internal FFmpegVideoChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 AVBufferRef*   hwDeviceCtx       = null,
                                 PixelFormat?   targetPixelFormat = PixelFormat.Bgra32,
                                 int            threadCount       = 0,
                                 int            bufferDepth       = 8,
                                 Func<int>?     latestSeekEpochProvider = null)
    {
        _streamIndex        = streamIndex;
        _stream             = stream;
        _packetReader       = packetReader;
        _hwDeviceCtx        = hwDeviceCtx;
        _threadCount        = threadCount;
        _bufferDepth        = Math.Max(1, bufferDepth);
        _latestSeekEpochProvider = latestSeekEpochProvider ?? (() => 0);

        var cp = stream->codecpar;

        // Native source format: the pixel format stored in the container/codec parameters.
        // This is used by SourceFormat (routing decisions) and is independent of TargetPixelFormat.
        var nativeAvFmt     = (AVPixelFormat)cp->format;
        var nativePixelFmt  = MapNativePixelFormat(nativeAvFmt);
        _nativeSourceFormat = new VideoFormat(cp->width, cp->height, nativePixelFmt,
            stream->r_frame_rate.num, stream->r_frame_rate.den);

        // Resolved target: null means "use native format" (no software conversion).
        TargetPixelFormat = targetPixelFormat ?? nativePixelFmt;
        Format = new VideoFormat(cp->width, cp->height, TargetPixelFormat,
            stream->r_frame_rate.num, stream->r_frame_rate.den);

        SuggestedYuvColorMatrix = MapSuggestedYuvColorMatrix((AVColorSpace)cp->color_space);
        SuggestedYuvColorRange = MapSuggestedYuvColorRange((AVColorRange)cp->color_range);


        OpenCodec();
    }

    public int StreamIndex => _streamIndex;

    internal bool IsHardwareAccelerated => _codecCtx != null && _codecCtx->hw_device_ctx != null;

    internal string DecoderName => ffmpeg.avcodec_get_name(_codecCtx != null ? _codecCtx->codec_id : _stream->codecpar->codec_id);

    public int LatestSeekEpoch => _latestSeekEpochProvider();

    private void OpenCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (codec == null) throw new MediaOpenException("Video codec not found.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecCtx, _stream->codecpar);

        if (_threadCount >= 0)
            _codecCtx->thread_count = _threadCount;

        // Prefer both frame + slice threading for heavy software decode workloads.
        if (_hwDeviceCtx == null && _threadCount != 1)
            _codecCtx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

        // Attach hardware device context if provided.
        if (_hwDeviceCtx != null)
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);

        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new MediaOpenException($"avcodec_open2 failed: {ret}");

        string codecName = ffmpeg.avcodec_get_name(_codecCtx->codec_id);
        Log.LogInformation("Video stream={StreamIndex} codec={CodecName} threads(req={ReqThreads}, eff={EffThreads}) type(req={ReqType}, active={ActiveType})",
            _streamIndex, codecName, _threadCount, _codecCtx->thread_count, _codecCtx->thread_type, _codecCtx->active_thread_type);

        _frame    = ffmpeg.av_frame_alloc();
        _swFrame  = ffmpeg.av_frame_alloc();
        _pkt      = ffmpeg.av_packet_alloc();
    }

    private SwsContext* GetSws(int w, int h, AVPixelFormat srcFmt)
    {
        var dstFmt = MapPixelFormat(TargetPixelFormat);
        // swsBilinear = 2 (SWS_BILINEAR in libswscale/swscale.h). Used only for pixel-format
        // conversion (not a resize), so the interpolation quality has no visual effect here.
        const int swsBilinear = 2;
        _sws = ffmpeg.sws_getCachedContext(_sws, w, h, srcFmt, w, h, dstFmt,
            swsBilinear, null, null, null);
        if (_sws == null) throw new MediaDecodeException("sws_getCachedContext failed.");

        _swsBufSize = ffmpeg.av_image_get_buffer_size(dstFmt, w, h, 1);
        return _sws;
    }

    internal void StartDecoding(ConcurrentQueue<EncodedPacket>? packetPool = null)
    {
        _decodeTask = FFmpegDecodeWorkers.RunAsync(this, _packetReader, _cts.Token, packetPool);
    }

    public void ApplySeekEpoch(long seekPositionTicks)
    {
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        // §3.8 — local snapshot so concurrent Subscribe/Unsubscribe does not tear the loop.
        var subs = _subs;
        foreach (var s in subs) s.Flush();
        // Reset the dequeued counter so the post-seek empty ring does not spuriously
        // fire BufferUnderrun before the new epoch's first frame arrives.
        Interlocked.Exchange(ref _framesDequeued, 0);
        Volatile.Write(ref _positionTicks, seekPositionTicks);
    }

    private VideoFrame? ConvertFrame(AVFrame* frame)
    {
        int w = frame->width, h = frame->height;
        if (w == 0 || h == 0) return null;

        var sws = GetSws(w, h, (AVPixelFormat)frame->format);

        double tbSeconds = _stream->time_base.num / (double)_stream->time_base.den;
        var pts = SafePts(frame->pts, tbSeconds);

        // Rent a buffer from the pool. The VideoFrame carries an ArrayPoolOwner
        // wrapped in a ref-counted buffer so multiple subscribers share a single
        // rental; the rental returns to the pool when the last subscriber releases.
        var rented = ArrayPool<byte>.Shared.Rent(_swsBufSize);
        var owner  = new RefCountedVideoBuffer(new ArrayPoolOwner<byte>(rented));

        _srcDataArr[0] = frame->data[0];
        _srcDataArr[1] = frame->data[1];
        _srcDataArr[2] = frame->data[2];
        _srcDataArr[3] = frame->data[3];
        _srcStrideArr[0] = frame->linesize[0];
        _srcStrideArr[1] = frame->linesize[1];
        _srcStrideArr[2] = frame->linesize[2];
        _srcStrideArr[3] = frame->linesize[3];

        fixed (byte* pBuf = rented)
        {
            // Planar formats require separate data pointers for each plane.
            // Packed formats (Bgra32, Rgba32, Uyvy422) use a single plane pointer.
            switch (TargetPixelFormat)
            {
                case PixelFormat.Yuv420p:
                {
                    // Plane layout: [Y (w×h)] [U ((w+1)/2 × (h+1)/2)] [V ((w+1)/2 × (h+1)/2)]
                    // FFmpeg rounds odd dimensions up for chroma, so using (w+1)/2 avoids
                    // an sws_scale out-of-bounds write when width or height is odd.
                    int cW = (w + 1) / 2;
                    int cH = (h + 1) / 2;
                    int ySize  = w * h;
                    int uvSize = cW * cH;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + ySize;
                    _dstDataArr[2] = pBuf + ySize + uvSize;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = w;
                    _dstStrideArr[1] = cW;
                    _dstStrideArr[2] = cW;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                case PixelFormat.Nv12:
                {
                    // Plane layout: [Y (w×h)] [UV interleaved (w × (h+1)/2)]
                    int ySize = w * h;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + ySize;
                    _dstDataArr[2] = null;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = w;
                    _dstStrideArr[1] = w;
                    _dstStrideArr[2] = 0;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                case PixelFormat.Yuv422p10:
                {
                    // Each luma/chroma sample is 2 bytes (10-bit stored in 16-bit LE).
                    // Plane layout: [Y (w×2 × h)] [U ((w/2)×2 × h)] [V ((w/2)×2 × h)]
                    int yStride  = w * 2;             // 2 bytes per Y sample
                    int uvStride = w;                 // (w/2) samples × 2 bytes = w bytes
                    int ySize    = yStride  * h;
                    int uvSize   = uvStride * h;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + ySize;
                    _dstDataArr[2] = pBuf + ySize + uvSize;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = yStride;
                    _dstStrideArr[1] = uvStride;
                    _dstStrideArr[2] = uvStride;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                case PixelFormat.Yuv444p:
                {
                    // Plane layout: [Y (w×h)] [U (w×h)] [V (w×h)] — full chroma resolution
                    int planeSize = w * h;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + planeSize;
                    _dstDataArr[2] = pBuf + planeSize * 2;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = w;
                    _dstStrideArr[1] = w;
                    _dstStrideArr[2] = w;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                case PixelFormat.P010:
                {
                    // Semi-planar 10-bit 4:2:0: Y plane (16-bit/sample), interleaved UV plane (16-bit each)
                    // Y: w×h×2 bytes, stride = w*2
                    // UV: (w/2)×(h/2) pairs × 4 bytes/pair, stride = w*2 bytes/row
                    int yStride = w * 2;
                    int uvStride = w * 2; // (w/2) UV pairs × 4 bytes = w*2 bytes/row
                    int ySize = yStride * h;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + ySize;
                    _dstDataArr[2] = null;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = yStride;
                    _dstStrideArr[1] = uvStride;
                    _dstStrideArr[2] = 0;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                case PixelFormat.Yuv420p10:
                {
                    // Planar 10-bit 4:2:0: Y, U, V planes, each stored as 16-bit LE samples.
                    // Chroma dimensions round up for odd width/height (FFmpeg convention),
                    // so use (w+1)/2 and (h+1)/2 to size buffers correctly.
                    int cW = (w + 1) / 2;
                    int cH = (h + 1) / 2;
                    int yStride = w * 2;            // 2 bytes per Y sample
                    int uvStride = cW * 2;          // 2 bytes per chroma sample
                    int ySize = yStride * h;
                    int uvSize = uvStride * cH;
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = pBuf + ySize;
                    _dstDataArr[2] = pBuf + ySize + uvSize;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = yStride;
                    _dstStrideArr[1] = uvStride;
                    _dstStrideArr[2] = uvStride;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
                default:
                {
                    // Packed formats: Bgra32, Rgba32, Uyvy422 — all data in one plane.
                    _dstDataArr[0] = pBuf;
                    _dstDataArr[1] = null;
                    _dstDataArr[2] = null;
                    _dstDataArr[3] = null;
                    _dstStrideArr[0] = w * BytesPerPixel(TargetPixelFormat);
                    _dstStrideArr[1] = 0;
                    _dstStrideArr[2] = 0;
                    _dstStrideArr[3] = 0;
                    ffmpeg.sws_scale(sws, _srcDataArr, _srcStrideArr, 0, h, _dstDataArr, _dstStrideArr);
                    break;
                }
            }
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
        PixelFormat.Rgb24     => 3,
        PixelFormat.Bgr24     => 3,
        PixelFormat.Gray8     => 1,
        PixelFormat.P010      => 2, // 16-bit luma stride
        PixelFormat.Yuv420p10 => 2, // 16-bit luma stride
        PixelFormat.Yuv444p   => 1,
        _                     => 4  // BGRA32, RGBA32, UYVY422 all 4 bytes/px
    };

    // ── IMediaChannel<VideoFrame> pull (legacy shim) ──────────────────────

    /// <summary>
    /// Legacy <c>FillBuffer</c> API — delegates to a lazily-created default subscription.
    /// New callers should use <see cref="Subscribe"/> directly for proper fan-out. Position
    /// / dequeue tracking is handled inside the subscription via
    /// <see cref="NotifyFrameDelivered"/>.
    /// </summary>
    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        var sub = EnsureDefaultSubscription();
        int got = sub.FillBuffer(dest, frameCount);
        if (got == 0 && Interlocked.Read(ref _framesDequeued) > 0)
            RaiseBufferUnderrun();
        return got;
    }

    /// <inheritdoc/>
    public IVideoSubscription Subscribe(VideoSubscriptionOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sub = new FFmpegVideoSubscription(this, options);
        lock (_subsLock)
        {
            _subs = _subs.Add(sub);
        }
        return sub;
    }

    private FFmpegVideoSubscription EnsureDefaultSubscription()
    {
        if (_defaultSub is not null) return _defaultSub;
        lock (_subsLock)
        {
            // §3.55 / review Concurrency #10 — double-dispose guard.
            // Without this check a concurrent Dispose can null-out `_defaultSub`
            // between the outer fast-path read and the lock, and a new
            // subscription would then be created against an already-disposed
            // channel, leaking resources and producing an ObjectDisposed at
            // first frame.  Re-check under the lock after acquiring it.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _defaultSub ??= (FFmpegVideoSubscription)Subscribe(
                new VideoSubscriptionOptions(_bufferDepth, VideoOverflowPolicy.Wait, "default"));
        }
        return _defaultSub;
    }

    /// <summary>
    /// Called by a subscription whenever it hands a frame to its consumer, so that
    /// <see cref="Position"/> reflects the last-delivered PTS regardless of which
    /// fan-out path (legacy <see cref="FillBuffer"/>, <see cref="Subscribe"/>-based
    /// pull, or router push) is driving playback.
    /// </summary>
    internal void NotifyFrameDelivered(long ptsTicks)
    {
        if (ptsTicks < 0) return;
        Volatile.Write(ref _positionTicks, ptsTicks);
        Interlocked.Increment(ref _framesDequeued);
    }

    internal void RemoveSubscription(FFmpegVideoSubscription sub)
    {
        lock (_subsLock)
        {
            _subs = _subs.Remove(sub);
            if (ReferenceEquals(_defaultSub, sub))
                _defaultSub = null;
        }
    }

    private void RaiseBufferUnderrun()
    {
        var handler = BufferUnderrun;
        if (handler == null) return;
        var pos = Position;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h, p) = ((FFmpegVideoChannel, EventHandler<BufferUnderrunEventArgs>, TimeSpan))s!;
            h(self, new BufferUnderrunEventArgs(p, 0));
        }, (this, handler, pos));
    }

    public void Seek(TimeSpan position)
    {
        // §3.8 — snapshot the immutable array so a concurrent Subscribe/Unsubscribe
        // that swaps `_subs` cannot tear this loop.
        var subs = _subs;
        foreach (var s in subs) s.Flush();
        Interlocked.Exchange(ref _framesDequeued, 0);
        Volatile.Write(ref _positionTicks, position.Ticks);
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
        PixelFormat.P010      => AVPixelFormat.AV_PIX_FMT_P010LE,
        PixelFormat.Yuv420p10 => AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
        PixelFormat.Yuv444p   => AVPixelFormat.AV_PIX_FMT_YUV444P,
        PixelFormat.Rgb24     => AVPixelFormat.AV_PIX_FMT_RGB24,
        PixelFormat.Bgr24     => AVPixelFormat.AV_PIX_FMT_BGR24,
        PixelFormat.Gray8     => AVPixelFormat.AV_PIX_FMT_GRAY8,
        _                     => AVPixelFormat.AV_PIX_FMT_BGRA
    };

    /// <summary>
    /// Maps a native FFmpeg pixel format to the nearest <see cref="PixelFormat"/> enum value.
    /// Used to populate <see cref="SourceFormat"/> from codec parameters so that routing
    /// policies can select efficient pipeline formats without any test decoding.
    /// Unknown formats fall back to <see cref="PixelFormat.Bgra32"/>.
    /// </summary>
    private static PixelFormat MapNativePixelFormat(AVPixelFormat avFmt) => avFmt switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P     => PixelFormat.Yuv420p,
        AVPixelFormat.AV_PIX_FMT_NV12        => PixelFormat.Nv12,
        AVPixelFormat.AV_PIX_FMT_YUV422P10LE => PixelFormat.Yuv422p10,
        AVPixelFormat.AV_PIX_FMT_UYVY422     => PixelFormat.Uyvy422,
        AVPixelFormat.AV_PIX_FMT_BGRA        => PixelFormat.Bgra32,
        AVPixelFormat.AV_PIX_FMT_RGBA        => PixelFormat.Rgba32,
        AVPixelFormat.AV_PIX_FMT_P010LE      => PixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => PixelFormat.Yuv420p10,
        AVPixelFormat.AV_PIX_FMT_YUV444P     => PixelFormat.Yuv444p,
        AVPixelFormat.AV_PIX_FMT_RGB24       => PixelFormat.Rgb24,
        AVPixelFormat.AV_PIX_FMT_BGR24       => PixelFormat.Bgr24,
        AVPixelFormat.AV_PIX_FMT_GRAY8       => PixelFormat.Gray8,
        _                                    => PixelFormat.Bgra32
    };

    internal static YuvColorMatrix MapSuggestedYuvColorMatrix(AVColorSpace colorSpace) => colorSpace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => YuvColorMatrix.Bt709,
        AVColorSpace.AVCOL_SPC_BT470BG => YuvColorMatrix.Bt601,
        AVColorSpace.AVCOL_SPC_SMPTE170M => YuvColorMatrix.Bt601,
        AVColorSpace.AVCOL_SPC_FCC => YuvColorMatrix.Bt601,
        _ => YuvColorMatrix.Auto
    };

    internal static YuvColorRange MapSuggestedYuvColorRange(AVColorRange colorRange) => colorRange switch
    {
        AVColorRange.AVCOL_RANGE_JPEG => YuvColorRange.Full,
        AVColorRange.AVCOL_RANGE_MPEG => YuvColorRange.Limited,
        _ => YuvColorRange.Auto
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing FFmpegVideoChannel stream={StreamIndex}", _streamIndex);
        _cts.Cancel();
        if (_decodeTask != null)
        {
            try { _decodeTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException ex)
            {
                Log.LogError(ex, "Video stream={StreamIndex} decode task fault during dispose", _streamIndex);
            }
        }
        CompleteDecodeLoop();

        // Flush and dispose all subscriptions so any remaining queued frames'
        // refcounts are released (and the pool rentals returned).
        ImmutableArray<FFmpegVideoSubscription> subsSnap;
        lock (_subsLock)
        {
            subsSnap = _subs;
            _subs = ImmutableArray<FFmpegVideoSubscription>.Empty;
            _defaultSub = null;
        }
        foreach (var s in subsSnap) s.Dispose();

        if (_frame    != null) fixed (AVFrame**        pp = &_frame)    ffmpeg.av_frame_free(pp);
        if (_swFrame  != null) fixed (AVFrame**        pp = &_swFrame)  ffmpeg.av_frame_free(pp);
        if (_pkt      != null) fixed (AVPacket**       pp = &_pkt)      ffmpeg.av_packet_free(pp);
        if (_codecCtx != null) fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
        if (_sws      != null) ffmpeg.sws_freeContext(_sws);
    }

    public bool DecodePacketAndEnqueue(EncodedPacket ep, CancellationToken token)
    {
        int sendRet;
        fixed (byte* p = ep.Data)
        {
            _pkt->data     = ep.ActualLength > 0 ? p : null;
            _pkt->size     = ep.ActualLength;
            _pkt->pts      = ep.Pts;
            _pkt->dts      = ep.Dts;
            _pkt->duration = ep.Duration;

            // Keep ep.Data pinned while libavcodec reads the packet.
            sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _pkt);
        }

        if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            return true;
        if (sendRet == ffmpeg.AVERROR_EOF)
            return false;
        if (sendRet < 0)
        {
            Log.LogWarning("Video stream={StreamIndex} avcodec_send_packet failed: {ErrorCode}", _streamIndex, sendRet);
            return true;
        }

        while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
        {
            // If the frame is in hw memory, transfer to CPU before converting.
            AVFrame* decodedFrame = _frame;
            bool transferred = false;
            if (_hwDeviceCtx != null && _frame->hw_frames_ctx != null)
            {
                int transferRet = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
                if (transferRet >= 0)
                {
                    _swFrame->pts = _frame->pts;
                    decodedFrame  = _swFrame;
                    transferred   = true;
                }
                else
                {
                    // §3.6 / B22: transfer failed — skip this frame entirely rather
                    // than silently presenting the raw hw-memory-backed _frame (which
                    // would either crash sws_scale or produce garbage pixels). Log
                    // once per channel so the operator sees the failure without
                    // spamming the log on every failed frame.
                    if (Interlocked.Exchange(ref _hwTransferErrorLogged, 1) == 0)
                        Log.LogWarning(
                            "Video stream={StreamIndex}: av_hwframe_transfer_data failed ({ReturnCode}); skipping frame. " +
                            "Subsequent transfer failures on this channel will not be logged.",
                            _streamIndex, transferRet);
                    ffmpeg.av_frame_unref(_frame);
                    continue;
                }
            }

            var vf = ConvertFrame(decodedFrame);
            if (vf.HasValue)
            {
                PublishToSubscribers(vf.Value, token);
            }
            ffmpeg.av_frame_unref(_frame);
            if (transferred) ffmpeg.av_frame_unref(_swFrame);
        }

        return true;
    }

    /// <summary>
    /// Fan-out publish: each active subscription gets a retained reference to the
    /// same pooled buffer. A subscriber on <see cref="VideoOverflowPolicy.Wait"/>
    /// may block this call; a subscriber on <c>DropOldest</c> / <c>DropNewest</c>
    /// cannot stall the decoder.
    /// </summary>
    private void PublishToSubscribers(VideoFrame frame, CancellationToken token)
    {
        var subs = _subs; // immutable snapshot — tear-free read
        if (subs.Length == 0)
        {
            // No subscribers — release the buffer immediately.
            frame.MemoryOwner?.Dispose();
            return;
        }

        // The rental already holds one ref (created by RefCountedVideoBuffer ctor).
        // Acquire (subs.Length - 1) additional refs so each sub holds one.
        if (frame.MemoryOwner is RefCountedVideoBuffer rb)
            for (int i = 1; i < subs.Length; i++) rb.Retain();

        for (int i = 0; i < subs.Length; i++)
        {
            bool published;
            try
            {
                // §3.9 — isolate TryPublish exceptions: a single misbehaving
                // subscription must not leak its ref-count share nor skip
                // publish for the remaining subs. On any exception we treat
                // the publish as failed (release this sub's ref) and carry
                // on with the next sub.
                published = subs[i].TryPublish(frame, token);
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Video subscription TryPublish threw; treating as failed publish");
                published = false;
            }

            if (!published)
            {
                // Publish failed (sub is disposed / cancelled / threw) — release this sub's ref.
                frame.MemoryOwner?.Dispose();
            }
        }
    }

    public void CompleteDecodeLoop()
    {
        // §3.8 — local snapshot (ImmutableArray is tear-free but we keep the pattern uniform).
        var subs = _subs;
        foreach (var s in subs) s.CompleteWriter();
    }

    public void RaiseEndOfStream()
    {
        var handler = EndOfStream;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h) = ((FFmpegVideoChannel, EventHandler))s!;
            h(self, EventArgs.Empty);
        }, (this, handler));
    }
}

