using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace Seko.OwnAudioNET.Video.Decoders;

public unsafe sealed class FFSharedDemuxSession : IDisposable
{
    private readonly record struct PacketEntry(nint PacketPtr, long Generation);

    private readonly object _sync = new();
    private readonly AutoResetEvent _event = new(false);
    private readonly Dictionary<int, Queue<PacketEntry>> _queues = [];
    private readonly HashSet<int> _streams = [];
    private readonly int _queueCapacity;
    private readonly Thread _thread;

    private nint _formatCtx;
    private volatile bool _running;
    private volatile bool _disposed;
    private bool _inputEof;
    private readonly bool _canSeek;
    private long _generation;
    private string? _fatalError;

    private FFSharedDemuxSession(nint formatCtx, bool canSeek, FFSharedDemuxSessionOptions options)
    {
        _formatCtx = formatCtx;
        _canSeek = canSeek;
        _queueCapacity = options.NormalizedPacketQueueCapacity;

        foreach (var streamIndex in options.InitialStreamIndices)
            RegisterStreamInternal(streamIndex);

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "FFSharedDemuxSession-Demux",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public static FFSharedDemuxSession OpenFile(string filePath, FFSharedDemuxSessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var opts = options ?? new FFSharedDemuxSessionOptions();

        AVFormatContext* ctx = ffmpeg.avformat_alloc_context();
        if (ctx == null)
            throw new InvalidOperationException("avformat_alloc_context failed.");

        try
        {
            var open = ffmpeg.avformat_open_input(&ctx, filePath, null, null);
            if (open < 0)
                throw new InvalidOperationException($"avformat_open_input failed: {GetErrorText(open)}");

            var info = ffmpeg.avformat_find_stream_info(ctx, null);
            if (info < 0)
                throw new InvalidOperationException($"avformat_find_stream_info failed: {GetErrorText(info)}");

            return new FFSharedDemuxSession((nint)ctx, canSeek: true, opts);
        }
        catch
        {
            ffmpeg.avformat_close_input(&ctx);
            throw;
        }
    }

    public bool CanSeek => _canSeek;

    /// <summary>Total stream count in the underlying container.</summary>
    public int StreamCount
    {
        get
        {
            lock (_sync)
            {
                if (_formatCtx == 0)
                    return 0;

                return (int)((AVFormatContext*)_formatCtx)->nb_streams;
            }
        }
    }

    /// <summary>Returns stream indices currently routed by this shared demux session.</summary>
    public int[] GetRegisteredStreams()
    {
        lock (_sync)
            return _streams.ToArray();
    }

    /// <summary>Registers a stream index for routing into per-stream packet queues.</summary>
    public bool RegisterStream(int streamIndex, out string error)
    {
        lock (_sync)
        {
            EnsureNotDisposed();
            if (!RegisterStreamInternal(streamIndex))
            {
                error = $"Invalid stream index {streamIndex}.";
                return false;
            }
        }

        _event.Set();
        error = string.Empty;
        return true;
    }

    /// <summary>Registers multiple stream indices for routing.</summary>
    public int RegisterStreams(IEnumerable<int> streamIndices)
    {
        ArgumentNullException.ThrowIfNull(streamIndices);
        var added = 0;
        lock (_sync)
        {
            EnsureNotDisposed();
            foreach (var streamIndex in streamIndices)
            {
                if (RegisterStreamInternal(streamIndex))
                    added++;
            }
        }

        if (added > 0)
            _event.Set();

        return added;
    }

    internal TimeSpan ContainerDuration
    {
        get
        {
            lock (_sync)
            {
                if (_formatCtx == 0)
                    return TimeSpan.Zero;

                var duration = ((AVFormatContext*)_formatCtx)->duration;
                return duration > 0
                    ? TimeSpan.FromSeconds(duration / (double)ffmpeg.AV_TIME_BASE)
                    : TimeSpan.Zero;
            }
        }
    }

    internal bool TryResolveStream(AVMediaType mediaType, int? preferredIndex, out int streamIndex, out AVCodec* codec, out AVRational timeBase, out AVStream* stream, out string error)
    {
        lock (_sync)
        {
            EnsureNotDisposed();
            var fmt = (AVFormatContext*)_formatCtx;
            streamIndex = -1;
            codec = null;
            timeBase = default;
            stream = null;

            if (preferredIndex.HasValue)
            {
                var candidate = preferredIndex.Value;
                if (candidate < 0 || candidate >= fmt->nb_streams)
                {
                    error = $"Stream index {candidate} is outside stream range.";
                    return false;
                }

                var preferredStream = fmt->streams[candidate];
                if (preferredStream->codecpar->codec_type != mediaType)
                {
                    error = $"Stream {candidate} is not a {mediaType} stream.";
                    return false;
                }

                var preferredCodec = ffmpeg.avcodec_find_decoder(preferredStream->codecpar->codec_id);
                if (preferredCodec == null || !RegisterStreamInternal(candidate))
                {
                    error = $"No decoder found for stream {candidate}.";
                    return false;
                }

                streamIndex = candidate;
                codec = preferredCodec;
                timeBase = preferredStream->time_base;
                stream = preferredStream;
                error = string.Empty;
                _event.Set();
                return true;
            }

            AVCodec* best = null;
            var bestIndex = ffmpeg.av_find_best_stream(fmt, mediaType, -1, -1, &best, 0);
            if (bestIndex >= 0 && RegisterStreamInternal(bestIndex))
            {
                var bestStream = fmt->streams[bestIndex];
                streamIndex = bestIndex;
                codec = best;
                timeBase = bestStream->time_base;
                stream = bestStream;
                error = string.Empty;
                _event.Set();
                return true;
            }

            error = $"No {mediaType} stream could be resolved.";
            return false;
        }
    }

    internal bool TryDequeuePacket(int streamIndex, AVPacket* targetPacket, out bool streamEof, out string? error, int waitTimeoutMs = 20)
    {
        error = null;
        streamEof = false;

        while (true)
        {
            lock (_sync)
            {
                EnsureNotDisposed();

                if (!_streams.Contains(streamIndex) && !RegisterStreamInternal(streamIndex))
                {
                    error = $"Stream {streamIndex} is not available.";
                    return false;
                }

                if (_queues.TryGetValue(streamIndex, out var queue))
                {
                    while (queue.Count > 0)
                    {
                        var entry = queue.Dequeue();
                        var packet = (AVPacket*)entry.PacketPtr;
                        if (entry.Generation != _generation)
                        {
                            ffmpeg.av_packet_free(&packet);
                            continue;
                        }

                        ffmpeg.av_packet_unref(targetPacket);
                        ffmpeg.av_packet_move_ref(targetPacket, packet);
                        ffmpeg.av_packet_free(&packet);
                        _event.Set();
                        return true;
                    }
                }

                if (_fatalError != null)
                {
                    error = _fatalError;
                    return false;
                }

                if (_inputEof)
                {
                    streamEof = true;
                    return false;
                }
            }

            _event.WaitOne(Math.Max(1, waitTimeoutMs));
        }
    }

    internal bool TrySeek(TimeSpan position, out string error)
    {
        lock (_sync)
        {
            EnsureNotDisposed();
            if (!_canSeek)
            {
                error = "Underlying input is not seekable.";
                return false;
            }

            var fmt = (AVFormatContext*)_formatCtx;
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;

            var targetUs = (long)Math.Round(position.TotalSeconds * ffmpeg.AV_TIME_BASE);

            var seek = ffmpeg.av_seek_frame(fmt, -1, targetUs, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seek < 0)
            {
                error = $"av_seek_frame failed: {GetErrorText(seek)}";
                return false;
            }

            ffmpeg.avformat_flush(fmt);
            _generation++;
            _inputEof = false;
            _fatalError = null;
            ClearQueuesLocked();
            _event.Set();
            error = string.Empty;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _running = false;
        _event.Set();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));

        lock (_sync)
        {
            if (_disposed)
                return;

            ClearQueuesLocked();
            if (_formatCtx != 0)
            {
                var fmt = (AVFormatContext*)_formatCtx;
                ffmpeg.avformat_close_input(&fmt);
                _formatCtx = 0;
            }

            _disposed = true;
        }

        _event.Dispose();
    }

    private void Loop()
    {
        while (_running)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                if (_inputEof || _streams.Count == 0 || AreAllQueuesFullLocked())
                    goto Wait;
            }

            var packet = ffmpeg.av_packet_alloc();
            if (packet == null)
            {
                lock (_sync)
                    _fatalError = "av_packet_alloc failed in shared demux loop.";
                _event.Set();
                goto Wait;
            }

            var gen = Volatile.Read(ref _generation);
            var read = ffmpeg.av_read_frame((AVFormatContext*)_formatCtx, packet);
            if (read < 0)
            {
                ffmpeg.av_packet_free(&packet);
                lock (_sync)
                {
                    if (read == ffmpeg.AVERROR_EOF)
                        _inputEof = true;
                    else
                        _fatalError = $"Shared demux read failed: {GetErrorText(read)}";
                }
                _event.Set();
                goto Wait;
            }

            var enqueued = false;
            while (_running && !enqueued)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        ffmpeg.av_packet_free(&packet);
                        return;
                    }

                    if (gen != _generation || !_streams.Contains(packet->stream_index))
                    {
                        ffmpeg.av_packet_free(&packet);
                        enqueued = true;
                        continue;
                    }

                    if (!_queues.TryGetValue(packet->stream_index, out var queue))
                    {
                        queue = [];
                        _queues[packet->stream_index] = queue;
                    }

                    if (queue.Count < _queueCapacity)
                    {
                        queue.Enqueue(new PacketEntry((nint)packet, gen));
                        enqueued = true;
                    }
                }

                if (!enqueued)
                    _event.WaitOne(2);
            }

            if (!enqueued)
                ffmpeg.av_packet_free(&packet);

            _event.Set();
            continue;

Wait:
            _event.WaitOne(4);
        }
    }

    private bool RegisterStreamInternal(int streamIndex)
    {
        if (_formatCtx == 0)
            return false;

        var fmt = (AVFormatContext*)_formatCtx;
        if (streamIndex < 0 || streamIndex >= fmt->nb_streams)
            return false;

        var stream = fmt->streams[streamIndex];
        if (stream->codecpar->codec_type is not AVMediaType.AVMEDIA_TYPE_AUDIO and not AVMediaType.AVMEDIA_TYPE_VIDEO)
            return false;

        _streams.Add(streamIndex);
        if (!_queues.ContainsKey(streamIndex))
            _queues[streamIndex] = [];
        return true;
    }

    private bool AreAllQueuesFullLocked()
    {
        foreach (var streamIndex in _streams)
        {
            if (!_queues.TryGetValue(streamIndex, out var queue) || queue.Count < _queueCapacity)
                return false;
        }

        return _streams.Count > 0;
    }

    private void ClearQueuesLocked()
    {
        foreach (var queue in _queues.Values)
        {
            while (queue.Count > 0)
            {
                var packet = (AVPacket*)queue.Dequeue().PacketPtr;
                ffmpeg.av_packet_free(&packet);
            }
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFSharedDemuxSession));
    }

    private static string GetErrorText(int code)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(code, buffer, 1024);
        return Marshal.PtrToStringAnsi((nint)buffer) ?? code.ToString();
    }
}

