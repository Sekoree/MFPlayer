using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core.Media;

namespace S.Media.FFmpeg;

/// <summary>
/// Options for <see cref="FFmpegDecoder"/>.
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
    /// Enables automatic hardware decode device probing.
    /// When <see langword="true"/> (default), the decoder probes the OS-preferred hardware
    /// acceleration device (e.g. VAAPI on Linux, D3D11VA on Windows, VideoToolbox on macOS)
    /// and falls back to software decode if none is available.
    /// Set to <see langword="false"/> to force software-only decoding.
    /// </summary>
    public bool PreferHardwareDecoding { get; init; } = true;

    /// <summary>
    /// Whether audio streams are opened/decoded. Default: <see langword="true"/>.
    /// Disable for video-only playback scenarios.
    /// </summary>
    public bool EnableAudio { get; init; } = true;

    /// <summary>
    /// Whether video streams are opened/decoded. Default: <see langword="true"/>.
    /// Disable for audio-only playback scenarios.
    /// </summary>
    public bool EnableVideo { get; init; } = true;

    /// <summary>
    /// Output pixel format produced by video channels. Default is Bgra32.
    /// Set to <see langword="null"/> to use the source's native pixel format automatically
    /// (no software conversion in the decoder — the pipeline operates in the source format).
    /// Set to <see cref="PixelFormat.Rgba32"/> for renderers that use RGBA uploads.
    /// </summary>
    public PixelFormat? VideoTargetPixelFormat { get; init; } = PixelFormat.Bgra32;
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

    /// <summary>Re-initialises this instance for reuse from a pool, avoiding a heap allocation.</summary>
    public void Reset(byte[] data, int actualLength, long pts, long dts, long duration, int flags,
                      bool isPooled, int seekEpoch, long seekPositionTicks)
    {
        Data              = data;
        ActualLength      = actualLength;
        Pts               = pts;
        Dts               = dts;
        Duration          = duration;
        Flags             = flags;
        SeekEpoch         = seekEpoch;
        SeekPositionTicks = seekPositionTicks;
        IsFlush           = false;
        IsPooled          = isPooled;
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
    public readonly record struct VideoChannelDiagnostics(
        int StreamIndex,
        string DecoderName,
        bool IsHardwareAccelerated,
        PixelFormat TargetPixelFormat);

    public readonly record struct DiagnosticsSnapshot(
        bool PreferHardwareDecoding,
        string? ActiveHardwareDeviceType,
        int VideoChannelCount,
        int HardwareAcceleratedVideoChannelCount,
        IReadOnlyList<VideoChannelDiagnostics> VideoChannels);

    internal enum DemuxReadResult
    {
        Packet,
        Retry,
        Eof,
        Cancelled
    }

    private AVFormatContext*         _fmt;
    private AVBufferRef*             _hwDeviceCtx;  // null when sw-only
    private StreamAvioContext?       _avioCtx;      // non-null when opened from a Stream
    private Task?                    _demuxTask;
    private CancellationTokenSource  _cts = new();
    private bool                     _disposed;
    private FFmpegDecoderOptions     _options = new();
    private int                      _seekEpoch;
    private long                     _seekPositionTicks;
    private long                     _seekControlDropLogCount;
    private int                      _started;
    private readonly object          _formatIoGate = new();
    private string?                  _activeHwDeviceType;
    private readonly ILogger         _log;

    // Per stream-index → bounded packet channel
    private readonly Dictionary<int, Channel<EncodedPacket>> _queues = new();

    // Pool of EncodedPacket objects to avoid per-packet heap allocations during demuxing.
    // Demux thread borrows; decode threads return via ReturnPacketToPool().
    internal readonly ConcurrentQueue<EncodedPacket> PacketPool = new();

    public IReadOnlyList<FFmpegAudioChannel> AudioChannels { get; private set; }
        = Array.Empty<FFmpegAudioChannel>();
    public IReadOnlyList<FFmpegVideoChannel> VideoChannels { get; private set; }
        = Array.Empty<FFmpegVideoChannel>();

    private FFmpegDecoder()
    {
        _log = FFmpegLogging.GetLogger(nameof(FFmpegDecoder));
    }

    /// <summary>Opens a local file or URL and creates channel objects for each stream.</summary>
    /// <param name="path">File path or URL (anything avformat_open_input accepts).</param>
    /// <param name="options">Decoder options; <see langword="null"/> uses defaults.</param>
    public static FFmpegDecoder Open(string path, FFmpegDecoderOptions? options = null)
    {
        FFmpegLoader.EnsureLoaded();
        var dec = new FFmpegDecoder();
        dec._options = NormalizeOptions(options ?? new FFmpegDecoderOptions());
        dec._log.LogInformation("Opening media from path: {Path}", path);
        dec._log.LogDebug("Options: QueueDepth={QueueDepth} AudioBuf={AudioBuf} VideoBuf={VideoBuf} Threads={Threads} HW={HW} Audio={Audio} Video={Video} PixFmt={PixFmt}",
            dec._options.PacketQueueDepth, dec._options.AudioBufferDepth, dec._options.VideoBufferDepth,
            dec._options.DecoderThreadCount, dec._options.PreferHardwareDecoding,
            dec._options.EnableAudio, dec._options.EnableVideo, dec._options.VideoTargetPixelFormat);
        dec.InitialiseFromPath(path);
        return dec;
    }

    /// <summary>
    /// Opens a media source from an arbitrary <see cref="Stream"/> (MemoryStream, FileStream,
    /// HTTP response stream, etc.) and creates channel objects for each stream.
    /// </summary>
    /// <param name="stream">
    /// The source stream. Must support <see cref="Stream.Read(byte[], int, int)"/>.
    /// Seekable streams enable seeking in the resulting decoder; non-seekable streams
    /// allow forward-only playback.
    /// </param>
    /// <param name="options">Decoder options; <see langword="null"/> uses defaults.</param>
    /// <param name="leaveOpen">
    /// When <see langword="false"/> (default), <see cref="Dispose"/> closes the stream.
    /// When <see langword="true"/>, the caller retains ownership of the stream.
    /// </param>
    public static FFmpegDecoder Open(Stream stream, FFmpegDecoderOptions? options = null,
                                     bool leaveOpen = false)
    {
        FFmpegLoader.EnsureLoaded();
        var dec = new FFmpegDecoder();
        dec._options = NormalizeOptions(options ?? new FFmpegDecoderOptions());
        dec._log.LogInformation("Opening media from Stream (CanSeek={CanSeek}, LeaveOpen={LeaveOpen})", stream.CanSeek, leaveOpen);
        dec._log.LogDebug("Options: QueueDepth={QueueDepth} AudioBuf={AudioBuf} VideoBuf={VideoBuf} Threads={Threads} HW={HW} Audio={Audio} Video={Video} PixFmt={PixFmt}",
            dec._options.PacketQueueDepth, dec._options.AudioBufferDepth, dec._options.VideoBufferDepth,
            dec._options.DecoderThreadCount, dec._options.PreferHardwareDecoding,
            dec._options.EnableAudio, dec._options.EnableVideo, dec._options.VideoTargetPixelFormat);
        dec.InitialiseFromStream(stream, leaveOpen);
        return dec;
    }

    private static FFmpegDecoderOptions NormalizeOptions(FFmpegDecoderOptions options)
    {
        return new FFmpegDecoderOptions
        {
            PacketQueueDepth = options.PacketQueueDepth,
            AudioBufferDepth = options.AudioBufferDepth,
            VideoBufferDepth = options.VideoBufferDepth,
            DecoderThreadCount = options.DecoderThreadCount < 0 ? 0 : options.DecoderThreadCount,
            PreferHardwareDecoding = options.PreferHardwareDecoding,
            EnableAudio = options.EnableAudio,
            EnableVideo = options.EnableVideo,
            VideoTargetPixelFormat = options.VideoTargetPixelFormat
        };
    }

    private void InitialiseFromPath(string path)
    {
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_open_input failed: {ret}");
        _fmt = fmt;
        _log.LogDebug("avformat_open_input succeeded for path: {Path}", path);

        DiscoverStreams();
    }

    private void InitialiseFromStream(Stream stream, bool leaveOpen)
    {
        _avioCtx = new StreamAvioContext(stream, leaveOpen);
        _log.LogDebug("StreamAvioContext created, CanSeek={CanSeek}", stream.CanSeek);

        AVFormatContext* fmt = ffmpeg.avformat_alloc_context();
        if (fmt == null)
        {
            _avioCtx.Dispose();
            _avioCtx = null;
            throw new InvalidOperationException("avformat_alloc_context returned null.");
        }

        fmt->pb = _avioCtx.Context;

        int ret = ffmpeg.avformat_open_input(&fmt, string.Empty, null, null);
        if (ret < 0)
        {
            _avioCtx.Dispose();
            _avioCtx = null;
            throw new InvalidOperationException($"avformat_open_input (stream) failed: {ret}");
        }

        _fmt = fmt;
        _log.LogDebug("avformat_open_input succeeded for Stream source");
        DiscoverStreams();
    }

    private void DiscoverStreams()
    {
        int ret = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info failed: {ret}");

        _log.LogDebug("Found {StreamCount} streams in container", (int)_fmt->nb_streams);

        if (_options.PreferHardwareDecoding)
            TryCreateDefaultHwDevice();

        var audio = new List<FFmpegAudioChannel>();
        var video = new List<FFmpegVideoChannel>();

        for (int i = 0; i < (int)_fmt->nb_streams; i++)
        {
            var stream    = _fmt->streams[i];
            var codecPars = stream->codecpar;

            if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                _log.LogDebug("Stream {Index}: skipping attached picture", i);
                continue;
            }

            if (codecPars->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                if (!_options.EnableAudio)
                {
                    _log.LogDebug("Stream {Index}: audio stream skipped (EnableAudio=false)", i);
                    continue;
                }

                var q = Channel.CreateBounded<EncodedPacket>(
                    new BoundedChannelOptions(_options.PacketQueueDepth)
                    {
                        FullMode     = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    });
                _queues[i] = q;

                audio.Add(new FFmpegAudioChannel(i, stream, q.Reader,
                    threadCount: _options.DecoderThreadCount,
                    bufferDepth: _options.AudioBufferDepth,
                    latestSeekEpochProvider: () => Volatile.Read(ref _seekEpoch)));

                _log.LogInformation("Stream {Index}: audio channel opened (SampleRate={SampleRate}, Channels={Channels}, Codec={Codec})",
                    i, codecPars->sample_rate, codecPars->ch_layout.nb_channels, codecPars->codec_id);
            }
            else if (codecPars->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                if (!_options.EnableVideo)
                {
                    _log.LogDebug("Stream {Index}: video stream skipped (EnableVideo=false)", i);
                    continue;
                }

                var q = Channel.CreateBounded<EncodedPacket>(
                    new BoundedChannelOptions(_options.PacketQueueDepth)
                    {
                        FullMode     = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false
                    });
                _queues[i] = q;

                video.Add(new FFmpegVideoChannel(i, stream, q.Reader,
                    hwDeviceCtx: _hwDeviceCtx,
                    targetPixelFormat: _options.VideoTargetPixelFormat,
                    threadCount: _options.DecoderThreadCount,
                    bufferDepth: _options.VideoBufferDepth,
                    latestSeekEpochProvider: () => Volatile.Read(ref _seekEpoch)));

                _log.LogInformation("Stream {Index}: video channel opened ({Width}x{Height}, Codec={Codec}, TargetPix={TargetPix})",
                    i, codecPars->width, codecPars->height, codecPars->codec_id, _options.VideoTargetPixelFormat);
            }
            else
            {
                _log.LogDebug("Stream {Index}: skipping unsupported type {Type}", i, codecPars->codec_type);
            }
        }

        AudioChannels = audio;
        VideoChannels = video;
        _log.LogInformation("Discovered {AudioCount} audio and {VideoCount} video channels", audio.Count, video.Count);
    }

    private void TryCreateHwDevice(string deviceType)
    {
        var type = ffmpeg.av_hwdevice_find_type_by_name(deviceType);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            _log.LogWarning("Unknown hw device type '{DeviceType}', falling back to software", deviceType);
            return;
        }

        AVBufferRef* ctx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&ctx, type, null, null, 0);
        if (ret < 0)
        {
            _log.LogWarning("av_hwdevice_ctx_create('{DeviceType}') failed ({ReturnCode}), falling back to software", deviceType, ret);
            return;
        }
        _hwDeviceCtx = ctx;
        _activeHwDeviceType = deviceType;
    }

    private void TryCreateDefaultHwDevice()
    {
        var available = GetAvailableHwDeviceTypes();
        _log.LogDebug("Available hw device types: [{Types}]", string.Join(", ", available));
        if (available.Count == 0)
            return;

        string[] preferredDevices;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            preferredDevices = ["vaapi", "vdpau", "cuda"];
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            preferredDevices = ["d3d11va", "dxva2", "cuda", "qsv"];
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            preferredDevices = ["videotoolbox"];
        else
            preferredDevices = ["vaapi", "d3d11va", "videotoolbox", "cuda", "qsv"];

        foreach (var device in preferredDevices)
        {
            if (_hwDeviceCtx != null)
                break;

            if (!available.Contains(device))
                continue;

            TryCreateHwDevice(device);
            if (_hwDeviceCtx != null)
                _log.LogInformation("Using hw device '{Device}'", device);
        }

        if (_hwDeviceCtx == null)
        {
            foreach (var device in available)
            {
                TryCreateHwDevice(device);
                if (_hwDeviceCtx != null)
                {
                    _log.LogInformation("Using hw device '{Device}' (fallback)", device);
                    break;
                }
            }
        }

        if (_hwDeviceCtx == null)
            _log.LogDebug("No hardware decode device available, using software decoding");
    }

    private static HashSet<string> GetAvailableHwDeviceTypes()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var type = ffmpeg.av_hwdevice_iterate_types(AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
        while (type != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            var name = ffmpeg.av_hwdevice_get_type_name(type);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);

            type = ffmpeg.av_hwdevice_iterate_types(type);
        }

        return names;
    }

    /// <summary>
    /// Starts the demux thread and all channel decode threads.
    /// Call after opening; add channels to mixers first.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            _log.LogDebug("Start() called again (idempotent, no-op)");
            return;
        }

        _log.LogInformation("Starting decoder: {AudioChannels} audio + {VideoChannels} video channels", AudioChannels.Count, VideoChannels.Count);

        foreach (var ch in AudioChannels) ch.StartDecoding(PacketPool);
        foreach (var ch in VideoChannels) ch.StartDecoding(PacketPool);

        _demuxTask = FFmpegDemuxWorker.RunAsync(this, _cts.Token);
        _log.LogDebug("Demux worker started");
    }

    /// <summary>Seeks all streams to <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        _log.LogInformation("Seeking to {Position}", position);
        long ts = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        int epoch;

        lock (_formatIoGate)
        {
            int ret = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
            {
                _log.LogWarning("av_seek_frame failed ({ReturnCode}) at {Position}", ret, position);
                return;
            }

            epoch = Interlocked.Increment(ref _seekEpoch);
            Volatile.Write(ref _seekPositionTicks, position.Ticks);
        }

        _log.LogDebug("Seek committed, epoch={Epoch}", epoch);

        var flush = EncodedPacket.Flush(epoch, position.Ticks);
        int droppedControlPackets = 0;
        foreach (var (_, q) in _queues)
            if (!WriteControlPacket(q.Writer, flush))
                droppedControlPackets++;

        if (droppedControlPackets > 0)
        {
            long warnCount = Interlocked.Increment(ref _seekControlDropLogCount);
            if (warnCount <= 3 || warnCount % 100 == 0)
                _log.LogWarning("Seek control packet dropped for {DroppedCount} stream(s) (total={TotalDrops})", droppedControlPackets, warnCount);
        }

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
        _log.LogError(ex, "Demux loop error");
    }

    // ── Demux helpers used by FFmpegDemuxWorker ───────────────────────────

    internal nint AllocateDemuxPacket() => (nint)ffmpeg.av_packet_alloc();

    internal void FreeDemuxPacket(nint pktHandle)
    {
        if (pktHandle == nint.Zero) return;
        var pkt = (AVPacket*)pktHandle;
        ffmpeg.av_packet_free(&pkt);
    }

    internal DemuxReadResult TryReadNextPacket(
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

        if (!PacketPool.TryDequeue(out var ep))
            ep = new EncodedPacket(data, actualLen, pts, dts, duration, flags,
                                   isPooled, packetEpoch, seekPositionTicks);
        else
            ep.Reset(data, actualLen, pts, dts, duration, flags,
                     isPooled, packetEpoch, seekPositionTicks);

        packet = ep;
        writer = q.Writer;
        return DemuxReadResult.Packet;
    }

    internal void CompletePacketQueues()
    {
        foreach (var (_, q) in _queues)
            q.Writer.TryComplete();
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var channels = new VideoChannelDiagnostics[VideoChannels.Count];
        int hwCount = 0;

        for (int i = 0; i < VideoChannels.Count; i++)
        {
            var ch = VideoChannels[i];
            bool isHw = ch.IsHardwareAccelerated;
            if (isHw) hwCount++;

            channels[i] = new VideoChannelDiagnostics(
                StreamIndex: ch.StreamIndex,
                DecoderName: ch.DecoderName,
                IsHardwareAccelerated: isHw,
                TargetPixelFormat: ch.TargetPixelFormat);
        }

        return new DiagnosticsSnapshot(
            PreferHardwareDecoding: _options.PreferHardwareDecoding,
            ActiveHardwareDeviceType: _activeHwDeviceType,
            VideoChannelCount: channels.Length,
            HardwareAcceleratedVideoChannelCount: hwCount,
            VideoChannels: channels);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.LogInformation("Disposing FFmpegDecoder");
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
        {
            if (_avioCtx != null)
                _fmt->pb = null;

            fixed (AVFormatContext** pp = &_fmt)
                ffmpeg.avformat_close_input(pp);
        }

        _avioCtx?.Dispose();
        _log.LogDebug("FFmpegDecoder disposed");
    }
}

