using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;

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

    /// <summary>
    /// §4.7 — when set, <see cref="FFmpegAudioChannel"/> configures its
    /// internal SWR to produce Float32 samples at this rate and channel
    /// count. The channel's reported <see cref="IAudioChannel.SourceFormat"/>
    /// matches, so the router recognises source == endpoint and skips its
    /// per-route resampler — eliminating the redundant second conversion
    /// when the sole audio endpoint has a well-known target rate (e.g.
    /// 48 kHz stereo for NDI or a PortAudio hardware stream).
    ///
    /// <para>
    /// <see langword="null"/> (default) keeps the historical behaviour:
    /// SWR passes rate/channels through from the source and only converts
    /// sample format (planar → interleaved). The builder sets this
    /// automatically when exactly one audio endpoint with a known format
    /// is wired up; manual callers should only set it when they know
    /// there will be a single audio endpoint consuming the channel.
    /// </para>
    /// </summary>
    public AudioFormat? AudioTargetFormat { get; init; }
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
/// <see cref="IAudioChannel"/> / <see cref="IVideoChannel"/> collections.
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
        Cancelled,
        /// <summary>
        /// Terminal, non-recoverable IO failure raised from the custom AVIO
        /// callback (e.g. broken underlying Stream). OnError has already been
        /// fired; the demux worker must stop without raising EndOfMedia.
        /// </summary>
        Fatal
    }

    private AVFormatContext*         _fmt;
    private AVBufferRef*             _hwDeviceCtx;  // null when sw-only
    private StreamAvioContext?       _avioCtx;      // non-null when opened from a Stream
    private Task?                    _demuxTask;
    private CancellationTokenSource  _cts = new();
    private bool                     _disposed;
    private string?                  _resourcePath;   // Path if opened via Open(string), null for Stream.
    private FFmpegDecoderOptions     _options = new();

    // ── Seek epoch protocol ──────────────────────────────────────────────
    // Each Seek() call increments _seekEpoch and sends a Flush sentinel to every
    // packet queue.  Decode workers compare the packet's epoch against the channel's
    // LatestSeekEpoch to discard stale packets from the pre-seek position.
    private int                      _seekEpoch;
    private long                     _seekPositionTicks;
    private long                     _seekControlDropLogCount;

    private int                      _started;
    // One-shot guard: ensures EndOfMedia fires at most once per session even when
    // multiple termination paths (clean EOF + demux finally fallback for Retry-
    // exhaustion / Fatal exits) try to publish it. Reset by Reopen/Seek-restart
    // is unnecessary because each FFmpegDecoder instance covers a single media
    // session (a new MediaPlayer.OpenAsync constructs a new decoder).
    private int                      _endOfMediaRaised;

    // Guards concurrent av_read_frame (demux thread) vs. av_seek_frame (user thread).
    // Read lock = demux reading packets; Write lock = seek repositioning the format context.
    // This is the ONLY lock on the demux hot path and is held only for the duration of
    // a single FFmpeg call — not across async operations.
    private readonly ReaderWriterLockSlim  _formatIoGate = new(LockRecursionPolicy.NoRecursion);

    private string?                  _activeHwDeviceType;
    // The AVHWDeviceType associated with the active HW device, needed by FFmpegVideoChannel
    // to look up the correct HW pixel format via avcodec_get_hw_config and wire up the
    // get_format callback. Without this the codec context falls back to SW silently.
    private AVHWDeviceType           _activeHwDeviceTypeEnum = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

    // Review §3.7 / §Consistency: shared static logger instead of per-instance
    // (decoder instances are short-lived; the per-instance field was gratuitous).
    private static readonly ILogger  _log = FFmpegLogging.GetLogger(nameof(FFmpegDecoder));

    // Per stream-index → bounded packet channel
    private readonly Dictionary<int, Channel<EncodedPacket>> _queues = new();

    // Pool of EncodedPacket objects to avoid per-packet heap allocations during demuxing.
    // Demux thread borrows; decode threads return via ReturnPacketToPool().
    internal readonly ConcurrentQueue<EncodedPacket> PacketPool = new();

    private IReadOnlyList<FFmpegAudioChannel> _audioChannelsImpl = Array.Empty<FFmpegAudioChannel>();
    private IReadOnlyList<FFmpegVideoChannel> _videoChannelsImpl = Array.Empty<FFmpegVideoChannel>();

    public IReadOnlyList<IAudioChannel> AudioChannels { get; private set; }
        = Array.Empty<IAudioChannel>();
    public IReadOnlyList<IVideoChannel> VideoChannels { get; private set; }
        = Array.Empty<IVideoChannel>();

    /// <summary>
    /// Returns the first audio channel, or <see langword="null"/> if no audio streams were found.
    /// </summary>
    public IAudioChannel? FirstAudioChannel => AudioChannels.Count > 0 ? AudioChannels[0] : null;

    /// <summary>
    /// Returns the first video channel, or <see langword="null"/> if no video streams were found.
    /// </summary>
    public IVideoChannel? FirstVideoChannel => VideoChannels.Count > 0 ? VideoChannels[0] : null;

    /// <summary>
    /// Total duration of the media, or <see langword="null"/> when the duration is unknown
    /// (e.g. live streams or formats without a duration header).
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            if (_fmt == null) return null;
            long d = _fmt->duration;
            // AV_NOPTS_VALUE means unknown duration.
            if (d <= 0 || d == long.MinValue) return null;
            // FFmpeg stores duration in AV_TIME_BASE units (microseconds).
            return TimeSpan.FromTicks(d * (TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE));
        }
    }

    /// <summary>
    /// §2.8 — raised once on a <see cref="ThreadPool"/> thread when the demux loop
    /// reaches the end of the media file/stream. Audio/video channels may still have
    /// buffered frames to drain; wait for their <c>EndOfStream</c> events before
    /// closing the pipeline.
    /// </summary>
    public event EventHandler? EndOfMedia;

    /// <summary>
    /// §2.8 — raised on a <see cref="ThreadPool"/> thread when the demux loop
    /// encounters a non-EOF read failure (typically a broken stream surfaced from
    /// <see cref="StreamAvioContext"/>). Review item §3.3 / B9. Subscribers get a
    /// <see cref="MediaDecodeException"/> whose <see cref="Exception.InnerException"/>
    /// is the underlying IO exception (if any). Handlers must not block.
    /// </summary>
    public event EventHandler<MediaDecodeException>? OnError;

    private FFmpegDecoder()
    {
    }

    /// <summary>Opens a local file or URL and creates channel objects for each stream.</summary>
    /// <param name="path">File path or URL (anything avformat_open_input accepts).</param>
    /// <param name="options">Decoder options; <see langword="null"/> uses defaults.</param>
    public static FFmpegDecoder Open(string path, FFmpegDecoderOptions? options = null)
    {
        FFmpegLoader.EnsureLoaded();
        var dec = new FFmpegDecoder();
        dec._options = NormalizeOptions(options ?? new FFmpegDecoderOptions());
        _log.LogInformation("Opening media from path: {Path}", path);
        _log.LogDebug("Options: QueueDepth={QueueDepth} AudioBuf={AudioBuf} VideoBuf={VideoBuf} Threads={Threads} HW={HW} Audio={Audio} Video={Video} PixFmt={PixFmt}",
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
        _log.LogInformation("Opening media from Stream (CanSeek={CanSeek}, LeaveOpen={LeaveOpen})", stream.CanSeek, leaveOpen);
        _log.LogDebug("Options: QueueDepth={QueueDepth} AudioBuf={AudioBuf} VideoBuf={VideoBuf} Threads={Threads} HW={HW} Audio={Audio} Video={Video} PixFmt={PixFmt}",
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
            VideoTargetPixelFormat = options.VideoTargetPixelFormat,
            AudioTargetFormat = options.AudioTargetFormat
        };
    }

    private void InitialiseFromPath(string path)
    {
        _resourcePath = path;
        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (ret < 0) throw new MediaOpenException($"avformat_open_input failed: {ret}", path);
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
            throw new MediaOpenException("avformat_alloc_context returned null.", _resourcePath);
        }

        fmt->pb = _avioCtx.Context;

        int ret = ffmpeg.avformat_open_input(&fmt, string.Empty, null, null);
        if (ret < 0)
        {
            _avioCtx.Dispose();
            _avioCtx = null;
            throw new MediaOpenException($"avformat_open_input (stream) failed: {ret}", _resourcePath);
        }

        _fmt = fmt;
        _log.LogDebug("avformat_open_input succeeded for Stream source");
        DiscoverStreams();
    }

    private void DiscoverStreams()
    {
        int ret = ffmpeg.avformat_find_stream_info(_fmt, null);
        if (ret < 0) throw new MediaOpenException($"avformat_find_stream_info failed: {ret}", _resourcePath);

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
                    latestSeekEpochProvider: () => Volatile.Read(ref _seekEpoch),
                    targetFormat: _options.AudioTargetFormat));

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
                    hwDeviceType: _activeHwDeviceTypeEnum,
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

        _audioChannelsImpl = audio;
        _videoChannelsImpl = video;
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
        _activeHwDeviceTypeEnum = type;
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

        foreach (var ch in _audioChannelsImpl) ch.StartDecoding(PacketPool);
        foreach (var ch in _videoChannelsImpl) ch.StartDecoding(PacketPool);

        _demuxTask = FFmpegDemuxWorker.RunAsync(this, _cts.Token);
        _log.LogDebug("Demux worker started");
    }

    /// <summary>Seeks all streams to <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        _log.LogInformation("Seeking to {Position}", position);
        // §3.5 / B21: integer division from Ticks → AV_TIME_BASE units avoids the
        // ±1 µs rounding error of (long)(TotalSeconds * AV_TIME_BASE) for positions
        // that are exact multiples of 100 ns but not exactly representable as a double.
        long ts = position.Ticks / (TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE);
        int epoch;

        _formatIoGate.EnterWriteLock();
        try
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
        finally
        {
            _formatIoGate.ExitWriteLock();
        }

        _log.LogDebug("Seek committed, epoch={Epoch}", epoch);

        int droppedControlPackets = 0;
        foreach (var (_, q) in _queues)
            if (!WriteControlPacket(q.Writer, EncodedPacket.Flush(epoch, position.Ticks)))
                droppedControlPackets++;

        if (droppedControlPackets > 0)
        {
            long warnCount = Interlocked.Increment(ref _seekControlDropLogCount);
            if (warnCount <= 3 || warnCount % 100 == 0)
                _log.LogWarning("Seek control packet dropped for {DroppedCount} stream(s) (total={TotalDrops})", droppedControlPackets, warnCount);
        }

        foreach (var ch in _audioChannelsImpl) ch.Seek(position);
        foreach (var ch in _videoChannelsImpl) ch.Seek(position);
    }

    private bool WriteControlPacket(ChannelWriter<EncodedPacket> writer, EncodedPacket packet)
    {
        try
        {
            // Fast path: ring has room — publish immediately.
            if (writer.TryWrite(packet)) return true;

            // §3.1 / B1 — flush-reliability fallback. The ring is full. If
            // we silently dropped the flush sentinel here, stale pre-seek
            // packets would remain in the queue and frames emitted from
            // them would carry the new post-seek epoch but the wrong PTS
            // → audible/visible rewind-then-black after the seek. Fall
            // back to a bounded async write so the decode worker has a
            // brief window to drain one slot. 50 ms is well below any
            // perceptible Seek latency but long enough to ride out the
            // typical decode-loop scheduling jitter.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var w = writer.WriteAsync(packet, cts.Token);
            if (w.IsCompletedSuccessfully) return true;
            try
            {
                w.AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException) { return false; }
        }
        catch (ChannelClosedException) { return false; }
    }

    internal void ReportDemuxLoopError(Exception ex)
    {
        _log.LogError(ex, "Demux loop error");
    }

    /// <summary>
    /// Fires <see cref="OnError"/> on a ThreadPool thread so the demux loop is not
    /// delayed by subscriber callbacks. Review §3.3 / B9.
    /// </summary>
    private void RaiseDemuxError(MediaDecodeException ex)
    {
        _log.LogError(ex, "Demux IO error surfaced to subscribers");
        var handler = OnError;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h, e) = ((FFmpegDecoder, EventHandler<MediaDecodeException>, MediaDecodeException))s!;
            try { h(self, e); } catch { /* subscriber errors must not crash the decoder */ }
        }, (this, handler, ex));
    }

    // ── Demux helpers used by FFmpegDemuxWorker ───────────────────────────

    internal nint AllocateDemuxPacket() => (nint)ffmpeg.av_packet_alloc();

    internal void FreeDemuxPacket(nint pktHandle)
    {
        if (pktHandle == nint.Zero) return;
        var pkt = (AVPacket*)pktHandle;
        ffmpeg.av_packet_free(&pkt);
    }

    /// <summary>
    /// Reads one packet from the format context under the read-lock, copies its payload
    /// into an <see cref="ArrayPool{T}"/>-rented buffer, and wraps it in a pooled
    /// <see cref="EncodedPacket"/>.  The native <c>AVPacket</c> is unreffed immediately
    /// so the caller owns only managed memory.
    /// <para>
    /// The caller (<see cref="FFmpegDemuxWorker"/>) is responsible for writing the returned
    /// packet into the appropriate per-stream channel; the decode worker on the other end
    /// returns the <c>ArrayPool</c> buffer and the <c>EncodedPacket</c> shell to their
    /// respective pools after decoding.
    /// </para>
    /// </summary>
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
        _formatIoGate.EnterReadLock();
        try
        {
            int ret = ffmpeg.av_read_frame(_fmt, pkt);
            if (ret == ffmpeg.AVERROR_EOF)
                return DemuxReadResult.Eof;
            if (ret < 0)
            {
                // Distinguish broken-stream (AVIO callback threw) from generic
                // retry-able errors. Review §3.3 / B9. Broken streams are
                // *terminal* — we raise OnError and return Eof so the demux
                // worker stops cleanly instead of tight-looping on Retry.
                var ioErr = _avioCtx?.ConsumeLastIoError();
                if (ioErr != null)
                {
                    RaiseDemuxError(new MediaDecodeException(
                        $"Demux read failed ({ret}): {ioErr.Message}", position: null, inner: ioErr));
                    return DemuxReadResult.Fatal;
                }
                return DemuxReadResult.Retry;
            }

            packetEpoch = Volatile.Read(ref _seekEpoch);
            seekPositionTicks = Volatile.Read(ref _seekPositionTicks);
        }
        finally
        {
            _formatIoGate.ExitReadLock();
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

    internal void RaiseEndOfMedia()
    {
        // Idempotent: clean-EOF demux exit and the demux finally-block fallback
        // (Retry-exhaustion / Fatal) can both reach this. We must not publish
        // EndOfMedia twice, otherwise MediaPlayer's drain task would race itself
        // and the playback-completion observer would fire a phantom completion.
        if (Interlocked.Exchange(ref _endOfMediaRaised, 1) != 0)
            return;

        var handler = EndOfMedia;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h) = ((FFmpegDecoder, EventHandler))s!;
            h(self, EventArgs.Empty);
        }, (this, handler));
    }

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var channels = new VideoChannelDiagnostics[_videoChannelsImpl.Count];
        int hwCount = 0;

        for (int i = 0; i < _videoChannelsImpl.Count; i++)
        {
            var ch = _videoChannelsImpl[i];
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

    /// <summary>
    /// Disposes the decoder, stopping all threads and releasing unmanaged FFmpeg resources.
    /// <para><b>Teardown order</b> (§3.2 / B2+B3):</para>
    /// <list type="number">
    ///   <item>Cancel the CTS → signals the demux worker to exit.</item>
    ///   <item>Wait on the demux task (3 s timeout) → the demux worker stops calling
    ///         <c>av_read_frame</c> and completes every packet-queue writer, so no
    ///         in-flight <c>q.Writer.WriteAsync</c> can race the channel dispose below.</item>
    ///   <item>Dispose channels → each cancels its own decode task, drains its ring and
    ///         releases per-channel FFmpeg state (codec ctx, swr, sws, frames).</item>
    ///   <item>Release HW device context (if any).</item>
    ///   <item>Take the format write-lock and close the AVFormatContext — belt-and-braces
    ///         guard against an any demux-read that slipped through the cancel (shouldn't
    ///         happen after the join, but correctness is cheap here).</item>
    ///   <item>Dispose the custom AVIO context (if opened from a Stream) and release
    ///         <see cref="_formatIoGate"/>.</item>
    /// </list>
    /// <para>
    /// <b>Note:</b> The 3-second blocking wait on <c>_demuxTask</c> can deadlock if Dispose
    /// is called from a sync context that the demux task's <c>ChannelWriter.WriteAsync</c>
    /// continuation needs. Callers in async code should <c>await StopAsync()</c> first or
    /// call Dispose from a plain thread/ThreadPool context.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.LogInformation("Disposing FFmpegDecoder");
        _cts.Cancel();

        // §3.2 / B2 — join the demux task BEFORE disposing channels so the
        // demux worker's in-flight WriteAsync calls cannot race an
        // already-completed ring. If the cooperative cancel times out
        // (3 s), fall through to channel disposal anyway — it is always
        // safe for the demux side.
        if (_demuxTask != null)
        {
            try { _demuxTask.Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException ex)
            {
                ReportDemuxLoopError(ex.Flatten());
            }
        }

        foreach (var ch in _audioChannelsImpl) ch.Dispose();
        foreach (var ch in _videoChannelsImpl) ch.Dispose();

        if (_hwDeviceCtx != null)
            fixed (AVBufferRef** pp = &_hwDeviceCtx)
                ffmpeg.av_buffer_unref(pp);

        // §3.2 / B3 — close the format context under the write lock so a
        // demux read that somehow slipped past the cancel (or a future
        // caller that bypasses the documented teardown order) cannot
        // execute av_read_frame concurrently with avformat_close_input
        // and touch freed state.
        if (_fmt != null)
        {
            bool lockTaken = false;
            try
            {
                _formatIoGate.EnterWriteLock();
                lockTaken = true;

                if (_avioCtx != null)
                    _fmt->pb = null;

                fixed (AVFormatContext** pp = &_fmt)
                    ffmpeg.avformat_close_input(pp);
            }
            finally
            {
                if (lockTaken) _formatIoGate.ExitWriteLock();
            }
        }

        _avioCtx?.Dispose();
        _formatIoGate.Dispose();
        _log.LogDebug("FFmpegDecoder disposed");
    }

    /// <summary>
    /// Cooperatively stops the demux task without tearing down FFmpeg resources.
    /// Safe to call multiple times. Implements review item §4.5 (Concurrency #1):
    /// async callers can <c>await StopAsync()</c> before <see cref="Dispose"/> to
    /// avoid the 3-second <c>Wait</c> inside sync Dispose under load.
    /// </summary>
    /// <param name="ct">Cancellation token aborts the wait (not the decoder itself).</param>
    public Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* raced Dispose */ }

        var t = _demuxTask;
        return t == null ? Task.CompletedTask : FFmpegDecoderAsyncHelpers.AwaitDemuxStopAsync(this, t, ct);
    }
}

/// <summary>
/// Async helpers that live outside the <c>unsafe</c> <see cref="FFmpegDecoder"/>
/// class so they can legally use <c>await</c> (C# forbids <c>await</c> in an
/// unsafe context).
/// </summary>
internal static class FFmpegDecoderAsyncHelpers
{
    internal static async Task AwaitDemuxStopAsync(FFmpegDecoder self, Task demux, CancellationToken ct)
    {
        try
        {
            await demux.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* ct cancelled — leave demux to finish on its own */ }
        catch (Exception ex)
        {
            // Mirror Dispose's error-reporting behaviour so callers don't see
            // spurious background exceptions escape.
            self.ReportDemuxLoopError(ex);
        }
    }
}

