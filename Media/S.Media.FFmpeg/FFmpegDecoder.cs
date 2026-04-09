using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;

namespace S.Media.FFmpeg;

/// <summary>
/// Options for <see cref="FFmpegDecoder.Open"/>.
/// </summary>
public sealed class FFmpegDecoderOptions
{
    /// <summary>Packet queue capacity per stream. Default 64.</summary>
    public int PacketQueueDepth { get; init; } = 64;

    /// <summary>Audio channel ring buffer depth in chunks. Default 16.</summary>
    public int AudioBufferDepth { get; init; } = 16;

    /// <summary>Video channel ring buffer depth in frames. Default 4.</summary>
    public int VideoBufferDepth { get; init; } = 4;

    /// <summary>
    /// Number of codec threads per stream. 0 = FFmpeg auto-detect (recommended).
    /// Set to 1 to disable multithreaded decoding.
    /// </summary>
    public int DecoderThreadCount { get; init; }

    /// <summary>
    /// Hardware device type for accelerated video decoding.
    /// Examples: <c>"vaapi"</c> (Linux), <c>"cuda"</c>/<c>"nvdec"</c> (NVIDIA),
    /// <c>"dxva2"</c>/<c>"d3d11va"</c> (Windows), <c>"videotoolbox"</c> (macOS).
    /// <see langword="null"/> or empty disables hardware acceleration.
    /// </summary>
    public string? HardwareDeviceType { get; init; }
}

/// <summary>
/// Internal data passed from the demux thread to a per-stream decode thread.
/// When <see cref="IsPooled"/> is true, <see cref="Data"/> was rented from
/// <see cref="ArrayPool{T}.Shared"/> and MUST be returned by the decode thread after use.
/// <see cref="ActualLength"/> holds the valid byte count (<see cref="Data"/>.Length may be larger).
/// </summary>
internal sealed class EncodedPacket
{
    public byte[]  Data;
    public int     ActualLength;  // valid bytes in Data (may be < Data.Length when pooled)
    public bool    IsPooled;      // true → decode thread must return Data to ArrayPool<byte>.Shared
    public long    Pts;
    public long    Dts;
    public long    Duration;
    public int     Flags;
    public int     SeekEpoch;
    public long    SeekPositionTicks;
    public bool    IsFlush; // sentinel to signal seek flush

    public EncodedPacket(byte[] data, int actualLength, long pts, long dts, long duration, int flags,
                         bool isPooled, int seekEpoch = 0, long seekPositionTicks = 0)
    {
        Data         = data;
        ActualLength = actualLength;
        Pts          = pts;
        Dts          = dts;
        Duration     = duration;
        Flags        = flags;
        SeekEpoch    = seekEpoch;
        SeekPositionTicks = seekPositionTicks;
        IsFlush      = false;
        IsPooled     = isPooled;
    }

    public static EncodedPacket Flush(int seekEpoch, long seekPositionTicks)
        => new([], 0, 0, 0, 0, 0, false, seekEpoch, seekPositionTicks) { IsFlush = true };
}

/// <summary>
/// Opens a media file or URL, discovers audio/video streams and exposes them as
/// <see cref="FFmpegAudioChannel"/> / <see cref="FFmpegVideoChannel"/> instances.
/// A single demux thread reads packets and routes them to per-stream bounded queues.
/// Back-pressure is applied via async write — no silent packet drops.
/// </summary>
public sealed unsafe class FFmpegDecoder : IDisposable
{
    internal enum DemuxReadResult
    {
        Packet,
        Retry,
        Eof,
        Cancelled
    }

    private AVFormatContext*         _fmt;
    private AVBufferRef*             _hwDeviceCtx;  // null when sw-only
    private Task?                    _demuxTask;
    private CancellationTokenSource  _cts = new();
    private bool                     _disposed;
    private FFmpegDecoderOptions     _options = new();
    private int                      _seekEpoch;
    private long                     _seekPositionTicks;
    private int                      _started;
    private readonly object          _formatIoGate = new();

    // Per stream-index → bounded packet channel
    private readonly Dictionary<int, Channel<EncodedPacket>> _queues = new();

    public IReadOnlyList<FFmpegAudioChannel> AudioChannels { get; private set; }
        = Array.Empty<FFmpegAudioChannel>();
    public IReadOnlyList<FFmpegVideoChannel> VideoChannels { get; private set; }
        = Array.Empty<FFmpegVideoChannel>();

    private FFmpegDecoder() { }

    /// <summary>Opens a local file or URL and creates channel objects for each stream.</summary>
    /// <param name="path">File path or URL (anything avformat_open_input accepts).</param>
    /// <param name="options">Decoder options; <see langword="null"/> uses defaults.</param>
    public static FFmpegDecoder Open(string path, FFmpegDecoderOptions? options = null)
    {
        FFmpegLoader.EnsureLoaded();
        var dec = new FFmpegDecoder();
        dec._options = options ?? new FFmpegDecoderOptions();
        dec.Initialise(path);
        return dec;
    }

    private void Initialise(string path)
    {
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_open_input failed: {ret}");
        _fmt = fmt;

        ret = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info failed: {ret}");

        // Optionally create a hardware device context for video decoding.
        if (!string.IsNullOrEmpty(_options.HardwareDeviceType))
            TryCreateHwDevice(_options.HardwareDeviceType);

        var audio = new List<FFmpegAudioChannel>();
        var video = new List<FFmpegVideoChannel>();

        for (int i = 0; i < (int)_fmt->nb_streams; i++)
        {
            var stream    = _fmt->streams[i];
            var codecPars = stream->codecpar;

            // Skip attached pictures (e.g. cover art embedded in FLAC/MP3/OGG files).
            // These appear as video streams with AV_DISPOSITION_ATTACHED_PIC (0x0400).
            if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                continue;

            var q = Channel.CreateBounded<EncodedPacket>(
                new BoundedChannelOptions(_options.PacketQueueDepth)
                {
                    FullMode     = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
            _queues[i] = q;

            if (codecPars->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                audio.Add(new FFmpegAudioChannel(i, stream, q.Reader,
                    threadCount: _options.DecoderThreadCount,
                    bufferDepth: _options.AudioBufferDepth));
            }
            else if (codecPars->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                video.Add(new FFmpegVideoChannel(i, stream, q.Reader,
                    hwDeviceCtx: _hwDeviceCtx,
                    threadCount: _options.DecoderThreadCount,
                    bufferDepth: _options.VideoBufferDepth));
            }
        }

        AudioChannels = audio;
        VideoChannels = video;
    }

    private void TryCreateHwDevice(string deviceType)
    {
        var type = ffmpeg.av_hwdevice_find_type_by_name(deviceType);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            Console.Error.WriteLine(
                $"[FFmpegDecoder] Unknown hw device type '{deviceType}', falling back to software.");
            return;
        }

        AVBufferRef* ctx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&ctx, type, null, null, 0);
        if (ret < 0)
        {
            Console.Error.WriteLine(
                $"[FFmpegDecoder] av_hwdevice_ctx_create('{deviceType}') failed ({ret}), falling back to software.");
            return;
        }
        _hwDeviceCtx = ctx;
    }

    /// <summary>
    /// Starts the demux thread and all channel decode threads.
    /// Call after opening; add channels to mixers first.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return; // idempotent start

        foreach (var ch in AudioChannels) ch.StartDecoding();
        foreach (var ch in VideoChannels) ch.StartDecoding();

        _demuxTask = FFmpegDemuxWorker.RunAsync(this, _cts.Token);
    }

    /// <summary>Seeks all streams to <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        long ts = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        int epoch;

        // av_seek_frame and av_read_frame share AVFormatContext internals.
        // Serialize them to avoid rapid-seek races against the demux loop.
        lock (_formatIoGate)
        {
            int ret = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                Console.Error.WriteLine($"[FFmpegDecoder] av_seek_frame failed ({ret}) at {position}.");
                return;
            }

            epoch = Interlocked.Increment(ref _seekEpoch);

            // Publish seek target so regular packets in this epoch still carry the
            // correct position if a best-effort flush control packet is dropped.
            Volatile.Write(ref _seekPositionTicks, position.Ticks);
        }

        // In-band flush packet: decode threads perform avcodec_flush_buffers on their own context.
        var flush = EncodedPacket.Flush(epoch, position.Ticks);
        int droppedControlPackets = 0;
        foreach (var (_, q) in _queues)
            if (!WriteControlPacket(q.Writer, flush))
                droppedControlPackets++;

        if (droppedControlPackets > 0)
            Console.Error.WriteLine($"[FFmpegDecoder] Seek control packet dropped for {droppedControlPackets} stream(s).");

        // Clear already-decoded channel buffers immediately on the caller thread.
        foreach (var ch in AudioChannels) ch.Seek(position);
        foreach (var ch in VideoChannels) ch.Seek(position);
    }

    private bool WriteControlPacket(ChannelWriter<EncodedPacket> writer, EncodedPacket packet)
    {
        try
        {
            // Keep seek control-path non-blocking: best effort only.
            return writer.TryWrite(packet);
        }
        catch (ChannelClosedException) { return false; }
    }

    internal void ReportDemuxLoopError(Exception ex)
    {
        Console.Error.WriteLine($"[FFmpegDecoder] demux-loop error: {ex}");
    }

    // ── Demux helpers used by FFmpegDemuxWorker ───────────────────────────

    internal unsafe nint AllocateDemuxPacket() => (nint)ffmpeg.av_packet_alloc();

    internal unsafe void FreeDemuxPacket(nint pktHandle)
    {
        if (pktHandle == nint.Zero) return;
        var pkt = (AVPacket*)pktHandle;
        ffmpeg.av_packet_free(&pkt);
    }

    internal unsafe DemuxReadResult TryReadNextPacket(
        nint pktHandle,
        out ChannelWriter<EncodedPacket>? writer,
        out EncodedPacket? packet,
        CancellationToken token)
    {
        writer = null;
        packet = null;

        if (token.IsCancellationRequested)
            return DemuxReadResult.Cancelled;

        if (pktHandle == nint.Zero)
            return DemuxReadResult.Eof;

        var pkt = (AVPacket*)pktHandle;
        int packetEpoch;
        long seekPositionTicks;
        lock (_formatIoGate)
        {
            int ret = ffmpeg.av_read_frame(_fmt, pkt);
            if (ret == ffmpeg.AVERROR_EOF)
                return DemuxReadResult.Eof;
            if (ret < 0)
                return DemuxReadResult.Retry;

            packetEpoch = Volatile.Read(ref _seekEpoch);
            seekPositionTicks = Volatile.Read(ref _seekPositionTicks);
        }

        if (!_queues.TryGetValue(pkt->stream_index, out var q))
        {
            ffmpeg.av_packet_unref(pkt);
            return DemuxReadResult.Retry;
        }

        byte[] data;
        bool isPooled;
        int actualLen = pkt->size;
        long pts = pkt->pts;
        long dts = pkt->dts;
        long duration = pkt->duration;
        int flags = pkt->flags;
        if (actualLen > 0)
        {
            data = ArrayPool<byte>.Shared.Rent(actualLen);
            isPooled = true;
            Marshal.Copy((nint)pkt->data, data, 0, actualLen);
        }
        else
        {
            data = [];
            isPooled = false;
            actualLen = 0;
        }

        ffmpeg.av_packet_unref(pkt);

        packet = new EncodedPacket(data, actualLen, pts, dts, duration, flags,
                                   isPooled, packetEpoch, seekPositionTicks);
        writer = q.Writer;
        return DemuxReadResult.Packet;
    }

    internal void CompletePacketQueues()
    {
        foreach (var (_, q) in _queues)
            q.Writer.TryComplete();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        foreach (var ch in AudioChannels) ch.Dispose();
        foreach (var ch in VideoChannels) ch.Dispose();

        if (_demuxTask != null)
        {
            try { _demuxTask.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException ex)
            {
                ReportDemuxLoopError(ex.Flatten());
            }
        }

        if (_hwDeviceCtx != null)
            fixed (AVBufferRef** pp = &_hwDeviceCtx)
                ffmpeg.av_buffer_unref(pp);

        if (_fmt != null)
            fixed (AVFormatContext** pp = &_fmt)
                ffmpeg.avformat_close_input(pp);
    }
}

