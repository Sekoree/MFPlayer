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
    // ── Decode pipeline ───────────────────────────────────────────────────
    private readonly int                       _streamIndex;
    private readonly AVStream*                 _stream;
    private readonly ChannelReader<EncodedPacket> _packetReader;

    private AVCodecContext* _codecCtx;
    private SwrContext*     _swr;          // normalises codec output → AV_SAMPLE_FMT_FLT
    private AVFrame*        _frame;
    private AVPacket*       _pkt;

    private Thread?                  _decodeThread;
    private CancellationTokenSource  _cts = new();

    // ── Sample ring buffer ────────────────────────────────────────────────
    private readonly Channel<float[]>       _ring;
    private readonly ChannelReader<float[]> _ringReader;
    private readonly ChannelWriter<float[]> _ringWriter;

    private float[]? _currentChunk;
    private int      _currentOffset;
    private long     _framesConsumed;

    // ── IAudioChannel ─────────────────────────────────────────────────────
    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => true;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }

    public TimeSpan Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);

    public int BufferAvailable => _ringReader.Count;

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    private bool _disposed;

    internal FFmpegAudioChannel(int streamIndex, AVStream* stream,
                                 ChannelReader<EncodedPacket> packetReader,
                                 int bufferDepth = 16)
    {
        _streamIndex  = streamIndex;
        _stream       = stream;
        _packetReader = packetReader;
        BufferDepth   = bufferDepth;

        var cp = stream->codecpar;
        SourceFormat = new AudioFormat(cp->sample_rate, cp->ch_layout.nb_channels);

        _ring = Channel.CreateBounded<float[]>(
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

    // ── Codec init ────────────────────────────────────────────────────────

    private void OpenCodec()
    {
        var codec = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (codec == null) throw new InvalidOperationException("Audio codec not found.");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecCtx, _stream->codecpar);
        int ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

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
            (AVSampleFormat)_codecCtx->sample_fmt, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt",
            AVSampleFormat.AV_SAMPLE_FMT_FLT, 0);

        ffmpeg.swr_init(_swr);
    }

    // ── Decode thread ─────────────────────────────────────────────────────

    internal void StartDecoding()
    {
        _decodeThread = new Thread(DecodeLoop)
        {
            Name         = $"FFmpegAudio[{_streamIndex}].Decode",
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
                FlushCodec();
                continue;
            }

            var converted = DecodePacket(ep);
            if (converted != null)
                _ringWriter.WriteAsync(converted, token).AsTask().GetAwaiter().GetResult();
        }
        _ringWriter.TryComplete();
    }

    private unsafe void FlushCodec() => ffmpeg.avcodec_flush_buffers(_codecCtx);

    private unsafe float[]? DecodePacket(EncodedPacket ep)
    {
        fixed (byte* p = ep.Data)
        {
            _pkt->data     = ep.Data.Length > 0 ? p : null;
            _pkt->size     = ep.Data.Length;
            _pkt->pts      = ep.Pts;
            _pkt->dts      = ep.Dts;
            _pkt->duration = ep.Duration;
            _pkt->flags    = ep.Flags;
        }

        if (ffmpeg.avcodec_send_packet(_codecCtx, _pkt) < 0) return null;

        float[]? last = null;
        while (ffmpeg.avcodec_receive_frame(_codecCtx, _frame) >= 0)
        {
            last = ConvertFrame();
            ffmpeg.av_frame_unref(_frame);
        }
        return last;
    }

    private float[]? ConvertFrame()
    {
        int samples   = _frame->nb_samples;
        int channels  = _frame->ch_layout.nb_channels;
        var outBuf    = new float[samples * channels];

        fixed (float* pOut = outBuf)
        {
            byte* outPtr = (byte*)pOut;
            // Build a stackalloc input pointer array from byte_ptrArray8
            byte** inData = stackalloc byte*[8];
            for (uint i = 0; i < 8; i++) inData[i] = _frame->data[i];

            int written = ffmpeg.swr_convert(_swr, &outPtr, samples, inData, samples);
            if (written <= 0) return null;
            if (written < samples)
            {
                var trimmed = new float[written * channels];
                Array.Copy(outBuf, trimmed, trimmed.Length);
                return trimmed;
            }
        }
        return outBuf;
    }

    // ── IAudioChannel pull (RT thread) ────────────────────────────────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        int channels     = SourceFormat.Channels;
        int totalSamples = frameCount * channels;
        int filled       = 0;

        while (filled < totalSamples)
        {
            if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                if (!_ringReader.TryRead(out _currentChunk))
                {
                    dest[filled..].Clear();
                    int dropped = (totalSamples - filled) / channels;
                    if (dropped > 0)
                        ThreadPool.QueueUserWorkItem(_ =>
                            BufferUnderrun?.Invoke(this,
                                new BufferUnderrunEventArgs(Position, dropped)));
                    return filled / channels;
                }
                _currentOffset = 0;
            }

            int available = _currentChunk.Length - _currentOffset;
            int needed    = totalSamples - filled;
            int toCopy    = Math.Min(available, needed);

            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(dest[filled..]);
            filled         += toCopy;
            _currentOffset += toCopy;
        }

        Interlocked.Add(ref _framesConsumed, frameCount);
        return frameCount;
    }

    // ── Push mode ─────────────────────────────────────────────────────────

    public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
        => _ringWriter.WriteAsync(frames.ToArray(), ct);

    public bool TryWrite(ReadOnlySpan<float> frames)
        => _ringWriter.TryWrite(frames.ToArray());

    // ── Seek ──────────────────────────────────────────────────────────────

    public void Seek(TimeSpan position)
    {
        _currentChunk  = null;
        _currentOffset = 0;
        while (_ringReader.TryRead(out _)) { }
        Interlocked.Exchange(ref _framesConsumed,
            (long)(position.TotalSeconds * SourceFormat.SampleRate));
    }

    internal void FlushAfterSeek()
    {
        _ringWriter.TryWrite(EncodedPacket.Flush() is var _ ? Array.Empty<float>() : Array.Empty<float>());
        ffmpeg.avcodec_flush_buffers(_codecCtx);
        Seek(TimeSpan.Zero);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _decodeThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();

        if (_frame != null)    fixed (AVFrame**   pp = &_frame)    ffmpeg.av_frame_free(pp);
        if (_pkt != null)      fixed (AVPacket**  pp = &_pkt)      ffmpeg.av_packet_free(pp);
        if (_codecCtx != null) fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
        if (_swr != null)      fixed (SwrContext** pp = &_swr)     ffmpeg.swr_free(pp);
    }
}

