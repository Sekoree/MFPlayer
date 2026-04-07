using System.Threading.Channels;
using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Media;

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
    public int DecoderThreadCount { get; init; } = 0;

    /// <summary>
    /// Hardware device type for accelerated video decoding.
    /// Examples: <c>"vaapi"</c> (Linux), <c>"cuda"</c>/<c>"nvdec"</c> (NVIDIA),
    /// <c>"dxva2"</c>/<c>"d3d11va"</c> (Windows), <c>"videotoolbox"</c> (macOS).
    /// <see langword="null"/> or empty disables hardware acceleration.
    /// </summary>
    public string? HardwareDeviceType { get; init; } = null;
}

/// <summary>
/// Internal data passed from the demux thread to a per-stream decode thread.
/// </summary>
internal sealed class EncodedPacket
{
    public byte[]  Data;
    public long    Pts;
    public long    Dts;
    public long    Duration;
    public int     Flags;
    public bool    IsFlush; // sentinel to signal seek flush

    public EncodedPacket(byte[] data, long pts, long dts, long duration, int flags)
    {
        Data     = data;
        Pts      = pts;
        Dts      = dts;
        Duration = duration;
        Flags    = flags;
        IsFlush  = false;
    }

    public static EncodedPacket Flush() => new([], 0, 0, 0, 0) { IsFlush = true };
}

/// <summary>
/// Opens a media file or URL, discovers audio/video streams and exposes them as
/// <see cref="FFmpegAudioChannel"/> / <see cref="FFmpegVideoChannel"/> instances.
/// A single demux thread reads packets and routes them to per-stream bounded queues.
/// Back-pressure is applied via async write — no silent packet drops.
/// </summary>
public sealed unsafe class FFmpegDecoder : IDisposable
{
    private AVFormatContext*         _fmt;
    private AVBufferRef*             _hwDeviceCtx;  // null when sw-only
    private Thread?                  _demuxThread;
    private CancellationTokenSource  _cts = new();
    private bool                     _disposed;
    private FFmpegDecoderOptions     _options = new();

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
                    SingleWriter = true
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
        foreach (var ch in AudioChannels) ch.StartDecoding();
        foreach (var ch in VideoChannels) ch.StartDecoding();

        _demuxThread = new Thread(DemuxLoop)
        {
            Name         = "FFmpegDecoder.Demux",
            IsBackground = true,
            Priority     = ThreadPriority.Normal
        };
        _demuxThread.Start();
    }

    /// <summary>Seeks all streams to <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        long ts = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);

        // Flush all queues and signal channels.
        foreach (var (_, q) in _queues)
            while (q.Reader.TryRead(out _)) { }

        foreach (var ch in AudioChannels) ch.FlushAfterSeek();
        foreach (var ch in VideoChannels) ch.FlushAfterSeek();
    }

    // ── Demux thread ──────────────────────────────────────────────────────

    private void DemuxLoop()
    {
        var token = _cts.Token;
        var pkt   = ffmpeg.av_packet_alloc();
        try
        {
            while (!token.IsCancellationRequested)
            {
                int ret = ffmpeg.av_read_frame(_fmt, pkt);
                if (ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) continue;

                if (_queues.TryGetValue(pkt->stream_index, out var q))
                {
                    // Copy data so we can unref the packet immediately.
                    byte[] data = new byte[pkt->size];
                    if (pkt->size > 0)
                        System.Runtime.InteropServices.Marshal.Copy((nint)pkt->data, data, 0, pkt->size);

                    var ep = new EncodedPacket(data, pkt->pts, pkt->dts, pkt->duration, pkt->flags);

                    // Apply back-pressure via async write — no silent packet drops.
                    var write = q.Writer.WriteAsync(ep, token);
                    if (!write.IsCompletedSuccessfully)
                    {
                        try { write.AsTask().GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { break; }
                    }
                }

                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            // Signal EOF to all queues.
            foreach (var (_, q) in _queues)
                q.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        foreach (var ch in AudioChannels) ch.Dispose();
        foreach (var ch in VideoChannels) ch.Dispose();

        _demuxThread?.Join(TimeSpan.FromSeconds(3));

        if (_hwDeviceCtx != null)
            fixed (AVBufferRef** pp = &_hwDeviceCtx)
                ffmpeg.av_buffer_unref(pp);

        if (_fmt != null)
            fixed (AVFormatContext** pp = &_fmt)
                ffmpeg.avformat_close_input(pp);
    }
}

