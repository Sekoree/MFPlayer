using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;

namespace S.Media.FFmpeg;

/// <summary>
/// Decodes a single audio stream into interleaved Float32 PCM via a background thread,
/// then exposes the data through the <see cref="IAudioChannel"/> pull/push interface.
/// </summary>
internal sealed unsafe class FFmpegAudioChannel : IAudioChannel, IDecodableChannel
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegAudioChannel));
    private readonly struct AudioChunk
    {
        public readonly float[] Buffer;
        public readonly int Samples;
        /// <summary>
        /// Stream-PTS ticks of the first frame in this chunk (not wall-clock elapsed).
        /// Used so <see cref="FFmpegAudioChannel.Position"/> reports container time and is
        /// directly comparable to <see cref="FFmpegVideoChannel.Position"/> for A/V drift.
        /// </summary>
        public readonly long StartPtsTicks;

        public AudioChunk(float[] buffer, int samples, long startPtsTicks)
        {
            Buffer = buffer;
            Samples = samples;
            StartPtsTicks = startPtsTicks;
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
    private long     _currentChunkStartPtsTicks;
    private long     _framesConsumed;
    private long     _positionTicks; // interpolated stream-PTS of the last sample consumed
    private long     _framesInRing;   // frame-accurate ring occupancy (not chunk count)

    // §3.49 / seek — bumped by FillBuffer (RT thread) after it has drained any
    // stale chunks left in the ring by a user-thread Seek. The drop-on-next-fill
    // mechanism avoids racing the RT callback against user-thread ring mutation:
    // whoever bumps _seekEpoch signals the RT thread, which owns the drain.
    private int      _rtObservedSeekEpoch;

    // ── IAudioChannel ─────────────────────────────────────────────────────
    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; private set; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => true;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }

    /// <summary>
    /// Current playback position in the container's stream-PTS domain.
    /// Starts at the first frame's PTS (may be non-zero for MP4 edit lists, HLS, etc.)
    /// and advances sample-accurately inside each decoded chunk.
    /// </summary>
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));

    public int BufferAvailable => (int)Math.Max(0, Interlocked.Read(ref _framesInRing));

    /// <summary>
    /// §2.8 — raised on the <see cref="ThreadPool"/> (detached from the RT pull
    /// callback so handler work cannot stall the audio hardware callback).
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
    /// <summary>
    /// §2.8 — raised on the demux worker thread after the last decoded sample
    /// has been enqueued. Handlers must not block; use <c>Task.Run</c> to
    /// chain further work.
    /// </summary>
    public event EventHandler? EndOfStream;

    private bool _disposed;

    // §3.48 / CH1 — single-reader reentrancy guard (Debug builds only).
    private int _fillBufferActive;

    // §4.7 — optional target format the decoder should produce directly,
    // eliminating the router's per-route resampler when source == endpoint.
    private readonly AudioFormat? _targetFormat;

    internal FFmpegAudioChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 int threadCount  = 0,
                                 int bufferDepth  = 16,
                                 Func<int>? latestSeekEpochProvider = null,
                                 AudioFormat? targetFormat = null)
    {
        _streamIndex  = streamIndex;
        _stream       = stream;
        _packetReader = packetReader;
        BufferDepth   = bufferDepth;
        _threadCount  = threadCount;
        _latestSeekEpochProvider = latestSeekEpochProvider ?? (() => 0);
        _targetFormat = targetFormat;

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

    public int StreamIndex => _streamIndex;

    public int LatestSeekEpoch => _latestSeekEpochProvider();

    // ── Codec init ────────────────────────────────────────────────────────

    private void OpenCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (codec == null) throw new MediaOpenException("Audio codec not found.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecCtx, _stream->codecpar);
        if (_threadCount >= 0)
            _codecCtx->thread_count = _threadCount; // 0 = FFmpeg auto
        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new MediaOpenException($"avcodec_open2 failed: {ret}");

        // Use negotiated decoder values (more reliable than container codecpar on some formats).
        int rate = _codecCtx->sample_rate > 0 ? _codecCtx->sample_rate : SourceFormat.SampleRate;
        int ch   = _codecCtx->ch_layout.nb_channels > 0 ? _codecCtx->ch_layout.nb_channels : SourceFormat.Channels;

        // §4.7 — when the caller supplied a target format, reshape SWR and
        // announce it as the SourceFormat so the router recognises source ==
        // endpoint and skips its resampler. Otherwise pass source through.
        SourceFormat = _targetFormat ?? new AudioFormat(rate, ch);

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
        AVChannelLayout inLayout  = _codecCtx->ch_layout;

        // §4.7 — output channel layout: default to the source layout, switch
        // to a synthesised default layout when a target channel count was
        // requested.
        AVChannelLayout outLayout = inLayout;
        int outRate = _codecCtx->sample_rate;
        if (_targetFormat is { } tf)
        {
            outRate = tf.SampleRate;
            ffmpeg.av_channel_layout_default(&outLayout, tf.Channels);
        }

        ffmpeg.av_opt_set_chlayout(_swr, "in_chlayout",  &inLayout,  0);
        ffmpeg.av_opt_set_chlayout(_swr, "out_chlayout", &outLayout, 0);
        ffmpeg.av_opt_set_int(_swr, "in_sample_rate",  _codecCtx->sample_rate, 0);
        ffmpeg.av_opt_set_int(_swr, "out_sample_rate", outRate, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt",
            _codecCtx->sample_fmt, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt",
            AVSampleFormat.AV_SAMPLE_FMT_FLT, 0);

        ffmpeg.swr_init(_swr);
    }

    // ── Decode thread ─────────────────────────────────────────────────────

    internal void StartDecoding(ConcurrentQueue<EncodedPacket>? packetPool = null)
    {
        _decodeTask = FFmpegDecodeWorkers.RunAsync(this, _packetReader, _cts.Token, packetPool);
    }

    public void ApplySeekEpoch(long seekPositionTicks)
    {
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        // _currentChunk / _currentOffset / _currentChunkSamples are owned by the
        // RT pull thread (FillBuffer). The RT thread observes the bumped
        // LatestSeekEpoch and discards its own mid-copy chunk on the next
        // callback — we do NOT touch those fields here, which would race.
        while (_ringReader.TryRead(out var chunk))
            ReturnChunkToPool(chunk.Buffer);
        Interlocked.Exchange(ref _framesInRing, 0);
        // Reset sample-counter AND PTS-domain position so Position reports the post-seek
        // value immediately instead of drifting until the first FillBuffer on the new epoch.
        Interlocked.Exchange(ref _framesConsumed, 0);
        Volatile.Write(ref _positionTicks, seekPositionTicks);
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
            Log.LogWarning("Audio stream={StreamIndex} avcodec_send_packet failed: {ErrorCode}", _streamIndex, sendRet);
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
                catch (ChannelClosedException)
                {
                    // §3.4 / B10 — the ring was completed concurrently
                    // (seek-flush race, Dispose on another thread). Before
                    // this guard the rented float[] leaked because
                    // WriteAsync threw before `_framesInRing` was bumped
                    // and the chunk was never returned to `_chunkPool`.
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
        int frameChannels = _frame->ch_layout.nb_channels;
        int sourceCodecChannels = _codecCtx->ch_layout.nb_channels;

        // Guard against transient channel-count mismatch: the codec context was opened with
        // a known layout; if a frame arrives with a different count (e.g. mono-island
        // packet inside a stereo stream), downstream mixers would see the wrong layout.
        // Compare against the codec's declared channel count, not the target format
        // (§4.7: `SourceFormat` now reports the target when a reshape is in effect).
        if (frameChannels != sourceCodecChannels)
        {
            Log.LogWarning("Audio stream={StreamIndex} frame channel count {FrameChannels} != codec declared {DeclaredChannels}; dropping frame.",
                _streamIndex, frameChannels, sourceCodecChannels);
            return null;
        }

        // Resolve stream-PTS of this frame's first sample. Falls back to the previous
        // chunk end when the frame has no PTS (AV_NOPTS_VALUE).
        double tbSeconds = _stream->time_base.num / (double)_stream->time_base.den;
        long framePtsTicks = FFmpegVideoChannel.SafePts(_frame->pts, tbSeconds).Ticks;

        // §4.7 — compute the target output-sample capacity accounting for any
        // rate change SWR performs. `av_rescale_rnd` with AV_ROUND_UP + a small
        // margin covers SWR's internal buffering delay on the first calls.
        int outRate = SourceFormat.SampleRate;
        int inRate  = _codecCtx->sample_rate;
        int outChannels = SourceFormat.Channels;
        int outMaxSamples = (outRate == inRate)
            ? samples
            : (int)ffmpeg.av_rescale_rnd(samples, outRate, inRate, AVRounding.AV_ROUND_UP) + 16;

        int outBufSize = outMaxSamples * outChannels;
        var outBuf = RentChunkBuffer(outBufSize);

        fixed (float* pOut = outBuf)
        {
            byte* outPtr = (byte*)pOut;
            // Build a stackalloc input pointer array from byte_ptrArray8
            byte** inData = stackalloc byte*[8];
            for (uint i = 0; i < 8; i++) inData[i] = _frame->data[i];

            int written = ffmpeg.swr_convert(_swr, &outPtr, outMaxSamples, inData, samples);
            if (written <= 0)
            {
                ReturnChunkToPool(outBuf);
                return null;
            }

            int writtenSamples = written * outChannels;
            return new AudioChunk(outBuf, writtenSamples, framePtsTicks);
        }
    }

    // ── IAudioChannel pull (RT thread) ────────────────────────────────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        // §3.48 / CH1 — assert single-reader invariant in debug builds.
        Debug.Assert(Interlocked.Exchange(ref _fillBufferActive, 1) == 0,
            "FFmpegAudioChannel.FillBuffer called concurrently — the contract requires single-threaded pull.");
        try
        {
            int channels     = SourceFormat.Channels;
            int sampleRate   = SourceFormat.SampleRate;
            int totalSamples = frameCount * channels;
            int filled       = 0;

            // Seek-epoch drain (RT thread, single-owner of chunk state).
            // When a user thread bumps the decoder's seek epoch, the RT
            // callback is the only thread that can safely discard the
            // pre-seek chunk it was mid-copying plus any pre-seek chunks
            // still queued in the ring. Output silence this callback and
            // let the decode worker refill with post-seek audio.
            int latestEpoch = LatestSeekEpoch;
            if (latestEpoch != _rtObservedSeekEpoch)
            {
                ReturnCurrentChunkToPool(); // nulls _currentChunk + offset + samples
                while (_ringReader.TryRead(out var stale))
                    ReturnChunkToPool(stale.Buffer);
                Interlocked.Exchange(ref _framesInRing, 0);
                _rtObservedSeekEpoch = latestEpoch;
                dest.Clear();
                return 0;
            }

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
                            UpdatePositionTicks(sampleRate, channels);
                        }
                        if (dropped > 0)
                        {
                            var state = (Self: this, Pos: Position, Dropped: dropped);
                            ThreadPool.QueueUserWorkItem(static s =>
                            {
                                var (self, pos, d) = ((FFmpegAudioChannel, TimeSpan, int))s!;
                                self.BufferUnderrun?.Invoke(self, new BufferUnderrunEventArgs(pos, d));
                            }, state);
                        }
                        return consumed;
                    }
                    _currentChunkSamples = chunk.Samples;
                    _currentChunk = chunk.Buffer;
                    _currentOffset = 0;
                    _currentChunkStartPtsTicks = chunk.StartPtsTicks;
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
            UpdatePositionTicks(sampleRate, channels);
            return frameCount;
        }
        finally
        {
            Interlocked.Exchange(ref _fillBufferActive, 0);
        }
    }

    /// <summary>
    /// Recomputes <see cref="_positionTicks"/> from the current chunk's start PTS plus the
    /// in-chunk sample offset.  Gives sample-accurate stream time even when the producer
    /// hasn't enqueued a new chunk yet.
    /// </summary>
    private void UpdatePositionTicks(int sampleRate, int channels)
    {
        if (sampleRate <= 0) return;
        long framesInChunk = _currentOffset / Math.Max(1, channels);
        long offsetTicks   = framesInChunk * TimeSpan.TicksPerSecond / sampleRate;
        Volatile.Write(ref _positionTicks, _currentChunkStartPtsTicks + offsetTicks);
    }

    // ── Push mode not supported — data comes from the internal decode thread ──
    // (FFmpegAudioChannel implements only IAudioChannel, not IWritableAudioChannel,
    // so there's nothing to stub: the API surface no longer advertises writes.)

    // ── Seek ──────────────────────────────────────────────────────────────

    public void Seek(TimeSpan position)
    {
        // Mirror of ApplySeekEpoch — called by FFmpegDecoder.Seek on the user
        // thread so Position updates before the decode worker picks up the
        // flush sentinel. _currentChunk is owned by the RT FillBuffer caller
        // and MUST NOT be touched here; the RT path drops it on the next
        // callback when it observes the bumped LatestSeekEpoch.
        while (_ringReader.TryRead(out var chunk))
            ReturnChunkToPool(chunk.Buffer);
        Interlocked.Exchange(ref _framesInRing, 0);
        Interlocked.Exchange(ref _framesConsumed, 0);
        Volatile.Write(ref _positionTicks, position.Ticks);
    }


    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing FFmpegAudioChannel stream={StreamIndex}", _streamIndex);
        _cts.Cancel();
        if (_decodeTask != null)
        {
            try { _decodeTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException ex)
            {
                Log.LogError(ex, "Audio stream={StreamIndex} decode task fault during dispose", _streamIndex);
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

    public void CompleteDecodeLoop() => _ringWriter.TryComplete();

    public void RaiseEndOfStream()
    {
        var handler = EndOfStream;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h) = ((FFmpegAudioChannel, EventHandler))s!;
            h(self, EventArgs.Empty);
        }, (this, handler));
    }

    private float[] RentChunkBuffer(int minSamples)
    {
        while (_chunkPool.TryDequeue(out var candidate))
        {
            if (candidate.Length >= minSamples)
                return candidate;
            // Drop undersized buffer — GC will collect it. Do NOT re-enqueue.
            break;
        }

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

