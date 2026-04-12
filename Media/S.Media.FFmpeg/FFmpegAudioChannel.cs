using System.Collections.Concurrent;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.FFmpeg;

/// <summary>
/// Decodes a single audio stream into interleaved Float32 PCM via a background thread,
/// then exposes the data through the <see cref="IAudioChannel"/> pull/push interface.
/// </summary>
public sealed unsafe class FFmpegAudioChannel : IAudioChannel
{
    private readonly struct AudioChunk
    {
        public readonly float[] Buffer;
        public readonly int Samples;

        public AudioChunk(float[] buffer, int samples)
        {
            Buffer = buffer;
            Samples = samples;
        }
    }

    // ── Decode pipeline ───────────────────────────────────────────────────
    private readonly AVStream*                 _stream;
    private readonly int                       _streamIndex;
    private readonly ChannelReader<EncodedPacket> _packetReader;
    private readonly int                       _threadCount;
    private readonly Func<int>                 _latestSeekEpochProvider;

    private AVCodecContext* _codecCtx;
    private SwrContext*     _swr;          // normalises codec output → AV_SAMPLE_FMT_FLT
    private AVFrame*        _frame;
    private AVPacket*       _pkt;

    private Task?                    _decodeTask;
    private CancellationTokenSource  _cts = new();

    // ── Sample ring buffer ────────────────────────────────────────────────
    private readonly ChannelReader<AudioChunk> _ringReader;
    private readonly ChannelWriter<AudioChunk> _ringWriter;
    private readonly ConcurrentQueue<float[]> _chunkPool = new();

    private float[]? _currentChunk;
    private int      _currentOffset;
    private int      _currentChunkSamples;
    private long     _framesConsumed;
    private long     _framesInRing;   // frame-accurate ring occupancy (not chunk count)

    // ── IAudioChannel ─────────────────────────────────────────────────────
    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; private set; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => true;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }

    public TimeSpan Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);

    public int BufferAvailable => (int)Math.Max(0, Interlocked.Read(ref _framesInRing));

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    private bool _disposed;

    internal FFmpegAudioChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 int threadCount  = 0,
                                 int bufferDepth  = 16,
                                 Func<int>? latestSeekEpochProvider = null)
    {
        _streamIndex  = streamIndex;
        _stream       = stream;
        _packetReader = packetReader;
        BufferDepth   = bufferDepth;
        _threadCount  = threadCount;
        _latestSeekEpochProvider = latestSeekEpochProvider ?? (() => 0);

        var cp = stream->codecpar;
        // Seed from codecpar; OpenCodec() will refine from the opened codec context.
        SourceFormat = new AudioFormat(cp->sample_rate, cp->ch_layout.nb_channels);

        var ring = Channel.CreateBounded<AudioChunk>(
            new BoundedChannelOptions(bufferDepth)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
        _ringReader = ring.Reader;
        _ringWriter = ring.Writer;

        OpenCodec();
    }

    internal int StreamIndex => _streamIndex;

    internal int LatestSeekEpoch => _latestSeekEpochProvider();

    internal void ReportDecodeLoopError(Exception ex, int currentEpoch, EncodedPacket ep)
    {
        Console.Error.WriteLine(
            $"[FFmpegAudioChannel] stream={_streamIndex} epoch={currentEpoch} packetEpoch={ep.SeekEpoch} packetBytes={ep.ActualLength} decode-loop error: {ex}");
    }

    // ── Codec init ────────────────────────────────────────────────────────

    private void OpenCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Audio codec not found.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecCtx, _stream->codecpar);
        if (_threadCount >= 0)
            _codecCtx->thread_count = _threadCount; // 0 = FFmpeg auto
        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

        // Use negotiated decoder values (more reliable than container codecpar on some formats).
        int rate = _codecCtx->sample_rate > 0 ? _codecCtx->sample_rate : SourceFormat.SampleRate;
        int ch   = _codecCtx->ch_layout.nb_channels > 0 ? _codecCtx->ch_layout.nb_channels : SourceFormat.Channels;
        SourceFormat = new AudioFormat(rate, ch);

        _frame = ffmpeg.av_frame_alloc();
        _pkt   = ffmpeg.av_packet_alloc();

        InitSwr();
    }

    private void InitSwr()
    {
        if (_swr != null)
            fixed (SwrContext** pp = &_swr)
                ffmpeg.swr_free(pp);

        _swr = ffmpeg.swr_alloc();
        AVChannelLayout layout = _codecCtx->ch_layout;

        ffmpeg.av_opt_set_chlayout(_swr, "in_chlayout",  &layout, 0);
        ffmpeg.av_opt_set_chlayout(_swr, "out_chlayout", &layout, 0);
        ffmpeg.av_opt_set_int(_swr, "in_sample_rate",  _codecCtx->sample_rate, 0);
        ffmpeg.av_opt_set_int(_swr, "out_sample_rate", _codecCtx->sample_rate, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt",
            _codecCtx->sample_fmt, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt",
            AVSampleFormat.AV_SAMPLE_FMT_FLT, 0);

        ffmpeg.swr_init(_swr);
    }

    // ── Decode thread ─────────────────────────────────────────────────────

    internal void StartDecoding()
    {
        _decodeTask = FFmpegDecodeWorkers.RunAudioAsync(this, _packetReader, _cts.Token);
    }

    internal void ApplySeekEpoch(long seekPositionTicks)
    {
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        ReturnCurrentChunkToPool();
        _currentChunk  = null;
        _currentOffset = 0;
        _currentChunkSamples = 0;
        while (_ringReader.TryRead(out var chunk))
            ReturnChunkToPool(chunk.Buffer);
        Interlocked.Exchange(ref _framesInRing, 0);
    }

    internal bool DecodePacketAndEnqueue(EncodedPacket ep, CancellationToken token)
    {
        int sendRet;
        fixed (byte* p = ep.Data)
        {
            _pkt->data     = ep.ActualLength > 0 ? p : null;
            _pkt->size     = ep.ActualLength;
            _pkt->pts      = ep.Pts;
            _pkt->dts      = ep.Dts;
            _pkt->duration = ep.Duration;
            _pkt->flags    = ep.Flags;

            // Keep ep.Data pinned while libavcodec reads the packet.
            sendRet = ffmpeg.avcodec_send_packet(_codecCtx, _pkt);
        }

        if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            return true;
        if (sendRet == ffmpeg.AVERROR_EOF)
            return false;
        if (sendRet < 0)
        {
            Console.Error.WriteLine($"[FFmpegAudioChannel] stream={_streamIndex} avcodec_send_packet failed: {sendRet}");
            return true;
        }

        while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
        {
            var converted = ConvertFrame();
            ffmpeg.av_frame_unref(_frame);

            if (converted == null)
                continue;

            var w = _ringWriter.WriteAsync(converted.Value, token);
            if (!w.IsCompletedSuccessfully)
            {
                try { w.AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    ReturnChunkToPool(converted.Value.Buffer);
                    return false;
                }
            }

            Interlocked.Add(ref _framesInRing, converted.Value.Samples / SourceFormat.Channels);
        }

        return true;
    }

    private AudioChunk? ConvertFrame()
    {
        int samples   = _frame->nb_samples;
        int channels  = _frame->ch_layout.nb_channels;
        int maxSamples = samples * channels;
        var outBuf = RentChunkBuffer(maxSamples);

        fixed (float* pOut = outBuf)
        {
            byte* outPtr = (byte*)pOut;
            // Build a stackalloc input pointer array from byte_ptrArray8
            byte** inData = stackalloc byte*[8];
            for (uint i = 0; i < 8; i++) inData[i] = _frame->data[i];

            int written = ffmpeg.swr_convert(_swr, &outPtr, samples, inData, samples);
            if (written <= 0)
            {
                ReturnChunkToPool(outBuf);
                return null;
            }

            int writtenSamples = written * channels;
            return new AudioChunk(outBuf, writtenSamples);
        }
    }

    // ── IAudioChannel pull (RT thread) ────────────────────────────────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        int channels     = SourceFormat.Channels;
        int totalSamples = frameCount * channels;
        int filled       = 0;

        while (filled < totalSamples)
        {
            if (_currentChunk == null || _currentOffset >= _currentChunkSamples)
            {
                ReturnCurrentChunkToPool();
                if (!_ringReader.TryRead(out var chunk))
                {
                    dest[filled..].Clear();
                    int consumed = filled / channels;
                    int dropped  = (totalSamples - filled) / channels;
                    if (consumed > 0)
                    {
                        Interlocked.Add(ref _framesConsumed, consumed);
                        Interlocked.Add(ref _framesInRing, -consumed);
                    }
                    if (dropped > 0)
                        ThreadPool.QueueUserWorkItem(_ =>
                            BufferUnderrun?.Invoke(this,
                                new BufferUnderrunEventArgs(Position, dropped)));
                    return consumed;
                }
                _currentChunkSamples = chunk.Samples;
                _currentChunk = chunk.Buffer;
                _currentOffset = 0;
            }

            int available = _currentChunkSamples - _currentOffset;
            int needed    = totalSamples - filled;
            int toCopy    = Math.Min(available, needed);

            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(dest[filled..]);
            filled         += toCopy;
            _currentOffset += toCopy;
        }

        Interlocked.Add(ref _framesConsumed, frameCount);
        Interlocked.Add(ref _framesInRing, -frameCount);
        return frameCount;
    }

    // ── Push mode (not supported — data comes from the internal decode thread) ──

    /// <summary>Not supported. <see cref="FFmpegAudioChannel"/> is fed by its internal decode
    /// thread; external writes would race with it and violate the <c>SingleWriter = true</c>
    /// contract on the ring.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
        => throw new NotSupportedException(
            "FFmpegAudioChannel is decode-driven and does not accept external writes.");

    /// <inheritdoc cref="WriteAsync"/>
    public bool TryWrite(ReadOnlySpan<float> frames)
        => throw new NotSupportedException(
            "FFmpegAudioChannel is decode-driven and does not accept external writes.");

    // ── Seek ──────────────────────────────────────────────────────────────

    public void Seek(TimeSpan position)
    {
        ReturnCurrentChunkToPool();
        _currentChunk  = null;
        _currentOffset = 0;
        _currentChunkSamples = 0;
        while (_ringReader.TryRead(out var chunk))
            ReturnChunkToPool(chunk.Buffer);
        Interlocked.Exchange(ref _framesInRing, 0);
        Interlocked.Exchange(ref _framesConsumed,
            (long)(position.TotalSeconds * SourceFormat.SampleRate));
    }


    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_decodeTask != null)
        {
            try { _decodeTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException ex)
            {
                Console.Error.WriteLine($"[FFmpegAudioChannel] stream={_streamIndex} decode task fault during dispose: {ex.Flatten()}");
            }
        }
        CompleteDecodeLoop();
        ReturnCurrentChunkToPool();
        while (_ringReader.TryRead(out var chunk))
            ReturnChunkToPool(chunk.Buffer);

        if (_frame != null)    fixed (AVFrame**   pp = &_frame)    ffmpeg.av_frame_free(pp);
        if (_pkt != null)      fixed (AVPacket**  pp = &_pkt)      ffmpeg.av_packet_free(pp);
        if (_codecCtx != null) fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
        if (_swr != null)      fixed (SwrContext** pp = &_swr)     ffmpeg.swr_free(pp);
    }

    internal void CompleteDecodeLoop() => _ringWriter.TryComplete();

    private float[] RentChunkBuffer(int minSamples)
    {
        while (_chunkPool.TryDequeue(out var candidate))
            if (candidate.Length >= minSamples)
                return candidate;

        return new float[minSamples];
    }

    private void ReturnCurrentChunkToPool()
    {
        if (_currentChunk != null)
            ReturnChunkToPool(_currentChunk);
    }

    private void ReturnChunkToPool(float[] buffer)
    {
        _chunkPool.Enqueue(buffer);
        if (ReferenceEquals(_currentChunk, buffer))
        {
            _currentChunk = null;
            _currentOffset = 0;
            _currentChunkSamples = 0;
        }
    }
}

