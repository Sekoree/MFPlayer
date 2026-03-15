using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Ownaudio;
using Ownaudio.Decoders;

namespace Seko.OwnAudioNET.Video.Decoders;

/// <summary>
/// FFmpeg-based audio decoder that resamples decoded PCM to interleaved 32-bit float at the
/// requested sample-rate and channel count.
/// </summary>
public unsafe class FFAudioDecoder : IAudioDecoder
{
    private const int AvioBufferSize = 32 * 1024;
    private const int SeekSet = 0;
    private const int SeekCur = 1;
    private const int SeekEnd = 2;

    // Keep AVIO callbacks alive for the whole process lifetime.
    private static readonly avio_alloc_context_read_packet SReadPacketDelegate = ReadPacket;
    private static readonly avio_alloc_context_seek SSeekPacketDelegate = SeekPacket;

    private readonly int _outSampleRate;
    private readonly int _outChannels;

    private nint _formatCtx;
    private nint _swrCtx;
    private nint _codecCtx;
    private int _streamIndex;

    private nint _packet;
    private nint _frame;

    private nint _avioCtx;
    private nint _avioBuffer;
    private GCHandle _streamStateHandle;
    private bool _hasStreamState;
    private bool _canSeek;

    private byte[] _pendingBuffer = Array.Empty<byte>();
    private byte[] _convertBuffer = Array.Empty<byte>();
    private byte[] _legacyDecodeBuffer = Array.Empty<byte>();
    private int _pendingStart;
    private int _pendingLength;
    private bool _inputEof;
    private bool _drainPacketSent;
    private bool _decoderEof;
    private bool _disposed;

    /// <summary>Metadata describing the output audio stream (resampled parameters).</summary>
    public AudioStreamInfo StreamInfo { get; private set; }

    /// <summary>
    /// Initializes a new <see cref="FFAudioDecoder"/> that reads from a file.
    /// </summary>
    /// <param name="file">Path to the media file.</param>
    /// <param name="outSampleRate">Output sample rate in Hz. Default: <c>44100</c>.</param>
    /// <param name="outChannels">Number of output channels (1 = mono, 2 = stereo). Default: <c>2</c>.</param>
    public FFAudioDecoder(string file, int outSampleRate = 44100, int outChannels = 2, int? preferredStreamIndex = null)
        : this(outSampleRate, outChannels)
    {
        try
        {
            var fCtx = ffmpeg.avformat_alloc_context();
            if (fCtx == null)
                throw new Exception("avformat_alloc_context failed");

            var openInputResult = ffmpeg.avformat_open_input(&fCtx, file, null, null);
            if (openInputResult < 0)
                throw new Exception($"avformat_open_input: {GetErrorText(openInputResult)}");

            _formatCtx = (nint)fCtx;
            _canSeek = true;

            InitializeCodecAndResampler(fCtx, preferredStreamIndex);
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    /// <summary>
    /// Initializes a new <see cref="FFAudioDecoder"/> that reads from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="stream">Readable input stream.</param>
    /// <param name="outSampleRate">Output sample rate in Hz. Default: <c>44100</c>.</param>
    /// <param name="outChannels">Number of output channels. Default: <c>2</c>.</param>
    /// <param name="leaveOpen">When <see langword="true"/> the stream is not disposed with this instance.</param>
    public FFAudioDecoder(Stream stream, int outSampleRate = 44100, int outChannels = 2, bool leaveOpen = false, int? preferredStreamIndex = null)
        : this(outSampleRate, outChannels)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Input stream must be readable.", nameof(stream));

        var state = new StreamIoState(stream, leaveOpen);
        _streamStateHandle = GCHandle.Alloc(state, GCHandleType.Normal);
        _hasStreamState = true;
        _canSeek = stream.CanSeek;

        try
        {
            var fCtx = ffmpeg.avformat_alloc_context();
            if (fCtx == null)
                throw new Exception("avformat_alloc_context failed");

            _formatCtx = (nint)fCtx;

            _avioBuffer = (nint)ffmpeg.av_malloc(AvioBufferSize);
            if (_avioBuffer == 0)
                throw new Exception("av_malloc failed for AVIO buffer");

            var opaque = GCHandle.ToIntPtr(_streamStateHandle).ToPointer();
            var readCallback = (avio_alloc_context_read_packet_func)SReadPacketDelegate;
            var seekCallback = _canSeek
                ? (avio_alloc_context_seek_func)SSeekPacketDelegate
                : default;

            var avio = ffmpeg.avio_alloc_context(
                (byte*)_avioBuffer,
                AvioBufferSize,
                0,
                opaque,
                readCallback,
                null,
                seekCallback);

            if (avio == null)
                throw new Exception("avio_alloc_context failed");

            _avioCtx = (nint)avio;
            fCtx->pb = avio;
            fCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            var openInputResult = ffmpeg.avformat_open_input(&fCtx, null, null, null);
            if (openInputResult < 0)
                throw new Exception($"avformat_open_input(stream): {GetErrorText(openInputResult)}");

            _formatCtx = (nint)fCtx;

            InitializeCodecAndResampler(fCtx, preferredStreamIndex);
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    private FFAudioDecoder(int outSampleRate, int outChannels)
    {
        _outSampleRate = outSampleRate;
        _outChannels = outChannels;
    }

    private void InitializeCodecAndResampler(AVFormatContext* fCtx, int? preferredStreamIndex)
    {
        var findStreamInfoResult = ffmpeg.avformat_find_stream_info(fCtx, null);
        if (findStreamInfoResult < 0)
            throw new Exception($"avformat_find_stream_info: {GetErrorText(findStreamInfoResult)}");

        var inCodec = ResolveAudioStreamAndCodec(fCtx, preferredStreamIndex, out _streamIndex);

        var stream = fCtx->streams[_streamIndex];
        var codecContext = ffmpeg.avcodec_alloc_context3(inCodec);
        if (codecContext == null)
            throw new Exception("avcodec_alloc_context3 failed");

        var codecParamToContext = ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar);
        if (codecParamToContext < 0)
            throw new Exception($"avcodec_parameters_to_context: {GetErrorText(codecParamToContext)}");

        var codecOpenResult = ffmpeg.avcodec_open2(codecContext, inCodec, null);
        if (codecOpenResult < 0)
            throw new Exception($"avcodec_open2: {GetErrorText(codecOpenResult)}");

        _codecCtx = (nint)codecContext;

        var outChannelLayout = new AVChannelLayout();
        var channelLayoutInitResult = ffmpeg.av_channel_layout_from_string(&outChannelLayout, _outChannels == 1 ? "mono" : "stereo");
        if (channelLayoutInitResult < 0)
            throw new Exception($"av_channel_layout_from_string: {GetErrorText(channelLayoutInitResult)}");

        try
        {
            SwrContext* localSwrCtx = null;
            var swrAllocResult = ffmpeg.swr_alloc_set_opts2(
                &localSwrCtx,
                &outChannelLayout,
                AVSampleFormat.AV_SAMPLE_FMT_FLT,
                _outSampleRate,
                &codecContext->ch_layout,
                codecContext->sample_fmt,
                codecContext->sample_rate,
                0,
                null);

            if (swrAllocResult < 0 || localSwrCtx == null)
                throw new Exception($"swr_alloc_set_opts2 failed: {GetErrorText(swrAllocResult)}");

            var swrInitResult = ffmpeg.swr_init(localSwrCtx);
            if (swrInitResult < 0)
                throw new Exception($"swr_init failed: {GetErrorText(swrInitResult)}");

            _swrCtx = (nint)localSwrCtx;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outChannelLayout);
        }

        _packet = (nint)ffmpeg.av_packet_alloc();
        _frame = (nint)ffmpeg.av_frame_alloc();
        if (_packet == 0 || _frame == 0)
            throw new Exception("av_packet_alloc/av_frame_alloc failed");

        StreamInfo = new AudioStreamInfo(
            channels: _outChannels,
            sampleRate: _outSampleRate,
            duration: fCtx->duration > 0
                ? TimeSpan.FromSeconds(fCtx->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero,
            bitDepth: sizeof(float) * 8
        );
    }

    /// <summary>
    /// Legacy single-frame decode path. Returns <see cref="AudioDecoderResult"/> with
    /// <c>Frame = null</c>; prefer <see cref="ReadFrames(Span{byte})"/> for new code.
    /// </summary>
    public AudioDecoderResult DecodeNextFrame()
    {
        EnsureNotDisposed();

        var frameBytes = _outChannels * sizeof(float) * 1024;
        EnsureLegacyDecodeBufferCapacity(frameBytes);
        var result = ReadFrames(_legacyDecodeBuffer.AsSpan(0, frameBytes));

        if (!result.IsSucceeded)
            return new AudioDecoderResult(frame: null, succeeded: false, eof: result.IsEOF, errorMessage: result.ErrorMessage);

        if (result.FramesRead <= 0)
            return new AudioDecoderResult(frame: null, succeeded: false, eof: false, errorMessage: "No frames decoded.");

        // Legacy API path: callers should migrate to ReadFrames(buffer).
        return new AudioDecoderResult(frame: null, succeeded: true, eof: false);
    }

    private void EnsureLegacyDecodeBufferCapacity(int requiredBytes)
    {
        if (_legacyDecodeBuffer.Length >= requiredBytes)
            return;

        var newSize = Math.Max(requiredBytes, Math.Max(_legacyDecodeBuffer.Length * 2, 8192));
        Array.Resize(ref _legacyDecodeBuffer, newSize);
    }

    /// <summary>
    /// Reads decoded PCM frames into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destination byte buffer sized as a multiple of <c>channels × sizeof(float)</c>.</param>
    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        return ReadFrames(buffer.AsSpan());
    }

    /// <summary>
    /// Reads decoded PCM frames into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destination span sized as a multiple of <c>channels × sizeof(float)</c>.</param>
    public AudioDecoderResult ReadFrames(Span<byte> buffer)
    {
        EnsureNotDisposed();

        var bytesPerFrame = _outChannels * sizeof(float);
        if (bytesPerFrame <= 0)
            return AudioDecoderResult.CreateError("Invalid output format.");

        var targetFrames = buffer.Length / bytesPerFrame;
        if (targetFrames <= 0)
            return AudioDecoderResult.CreateError("Buffer size is smaller than one frame.");

        var framesWritten = 0;

        while (framesWritten < targetFrames)
        {
            var copiedFrames = CopyPendingTo(buffer, framesWritten, targetFrames - framesWritten, bytesPerFrame);
            framesWritten += copiedFrames;
            if (framesWritten >= targetFrames)
                break;

            if (_decoderEof)
                break;

            var decodeStatus = DecodeNextChunkToPending(bytesPerFrame);
            if (!decodeStatus.ok)
            {
                if (framesWritten > 0)
                    return AudioDecoderResult.CreateSuccess(framesWritten);

                return decodeStatus.eof
                    ? AudioDecoderResult.CreateEOF()
                    : AudioDecoderResult.CreateError(decodeStatus.error ?? "Failed to decode audio frame.");
            }
        }

        if (framesWritten > 0)
            return AudioDecoderResult.CreateSuccess(framesWritten);

        return _decoderEof ? AudioDecoderResult.CreateEOF() : AudioDecoderResult.CreateSuccess(0);
    }

    /// <summary>
    /// Seeks the stream to <paramref name="position"/>, flushes codec and resampler state.
    /// </summary>
    /// <param name="position">Target position. Clamped to zero if negative.</param>
    /// <param name="error">Human-readable error on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        EnsureNotDisposed();

        error = string.Empty;

        if (!_canSeek)
        {
            error = "Underlying stream is not seekable.";
            return false;
        }

        var fCtx = (AVFormatContext*)_formatCtx;
        var cCtx = (AVCodecContext*)_codecCtx;
        var sCtx = (SwrContext*)_swrCtx;
        var pkt = (AVPacket*)_packet;
        var frm = (AVFrame*)_frame;
        var stream = fCtx->streams[_streamIndex];

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var avTimeBaseQ = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        var targetUs = (long)Math.Round(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        var targetTs = ffmpeg.av_rescale_q(targetUs, avTimeBaseQ, stream->time_base);

        var seekResult = ffmpeg.av_seek_frame(fCtx, _streamIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (seekResult < 0)
        {
            error = $"av_seek_frame failed: {GetErrorText(seekResult)}";
            return false;
        }

        ffmpeg.avcodec_flush_buffers(cCtx);
        ffmpeg.avformat_flush(fCtx);
        ffmpeg.av_packet_unref(pkt);
        ffmpeg.av_frame_unref(frm);

        ffmpeg.swr_close(sCtx);
        var reinitResult = ffmpeg.swr_init(sCtx);
        if (reinitResult < 0)
        {
            error = $"swr_init after seek failed: {GetErrorText(reinitResult)}";
            return false;
        }

        ResetDecodeState();
        return true;
    }

    /// <summary>Releases all native FFmpeg resources and optionally disposes the input stream.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseNativeResources();

        GC.SuppressFinalize(this);
    }

    private (bool ok, bool eof, string? error) DecodeNextChunkToPending(int bytesPerFrame)
    {
        var fCtx = (AVFormatContext*)_formatCtx;
        var cCtx = (AVCodecContext*)_codecCtx;
        var sCtx = (SwrContext*)_swrCtx;
        var pkt = (AVPacket*)_packet;
        var frm = (AVFrame*)_frame;

        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(cCtx, frm);
            if (receiveResult == 0)
            {
                var convertStatus = ConvertFrameToPending(sCtx, cCtx, frm, bytesPerFrame);
                ffmpeg.av_frame_unref(frm);
                if (!convertStatus.ok)
                    return convertStatus;
                return (true, false, null);
            }

            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                _decoderEof = true;
                return (false, true, null);
            }

            if (receiveResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                return (false, false, $"avcodec_receive_frame failed: {GetErrorText(receiveResult)}");

            if (_inputEof)
            {
                if (_drainPacketSent)
                {
                    _decoderEof = true;
                    return (false, true, null);
                }

                var sendDrainResult = ffmpeg.avcodec_send_packet(cCtx, null);
                if (sendDrainResult < 0 && sendDrainResult != ffmpeg.AVERROR_EOF)
                    return (false, false, $"avcodec_send_packet(null) failed: {GetErrorText(sendDrainResult)}");

                _drainPacketSent = true;
                continue;
            }

            var readResult = ffmpeg.av_read_frame(fCtx, pkt);
            if (readResult < 0)
            {
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    _inputEof = true;
                    continue;
                }

                return (false, false, $"av_read_frame failed: {GetErrorText(readResult)}");
            }

            if (pkt->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(pkt);
                continue;
            }

            var sendResult = ffmpeg.avcodec_send_packet(cCtx, pkt);
            ffmpeg.av_packet_unref(pkt);

            if (sendResult == 0 || sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;

            if (sendResult == ffmpeg.AVERROR_EOF)
            {
                _decoderEof = true;
                return (false, true, null);
            }

            return (false, false, $"avcodec_send_packet failed: {GetErrorText(sendResult)}");
        }
    }

    private (bool ok, bool eof, string? error) ConvertFrameToPending(SwrContext* sCtx, AVCodecContext* cCtx, AVFrame* frm, int bytesPerFrame)
    {
        var delay = ffmpeg.swr_get_delay(sCtx, cCtx->sample_rate);
        var outSamplesCapacity = (int)ffmpeg.av_rescale_rnd(
            delay + frm->nb_samples,
            _outSampleRate,
            cCtx->sample_rate,
            AVRounding.AV_ROUND_UP);

        if (outSamplesCapacity <= 0)
            return (true, false, null);

        var outBytesCapacity = outSamplesCapacity * bytesPerFrame;
        EnsureConvertCapacity(outBytesCapacity);

        fixed (byte* outRaw = _convertBuffer)
        {
            byte** outPlanes = stackalloc byte*[1];
            outPlanes[0] = outRaw;

            var convertedSamples = ffmpeg.swr_convert(
                sCtx,
                outPlanes,
                outSamplesCapacity,
                frm->extended_data,
                frm->nb_samples);

            if (convertedSamples < 0)
                return (false, false, $"swr_convert failed: {GetErrorText(convertedSamples)}");

            if (convertedSamples == 0)
                return (true, false, null);

            var convertedBytes = convertedSamples * bytesPerFrame;
            EnsurePendingCapacity(convertedBytes);
            _convertBuffer.AsSpan(0, convertedBytes).CopyTo(_pendingBuffer.AsSpan(_pendingStart + _pendingLength));
            _pendingLength += convertedBytes;

            return (true, false, null);
        }
    }

    private int CopyPendingTo(Span<byte> destination, int destinationFramesOffset, int requestedFrames, int bytesPerFrame)
    {
        if (_pendingLength <= 0 || requestedFrames <= 0)
            return 0;

        var availableFrames = _pendingLength / bytesPerFrame;
        var framesToCopy = Math.Min(requestedFrames, availableFrames);
        if (framesToCopy <= 0)
            return 0;

        var bytesToCopy = framesToCopy * bytesPerFrame;
        _pendingBuffer.AsSpan(_pendingStart, bytesToCopy)
            .CopyTo(destination.Slice(destinationFramesOffset * bytesPerFrame, bytesToCopy));

        _pendingStart += bytesToCopy;
        _pendingLength -= bytesToCopy;

        if (_pendingLength == 0)
            _pendingStart = 0;

        return framesToCopy;
    }

    private void EnsurePendingCapacity(int bytesToAppend)
    {
        if (_pendingLength == 0)
            _pendingStart = 0;

        if (_pendingBuffer.Length - (_pendingStart + _pendingLength) >= bytesToAppend)
            return;

        if (_pendingStart > 0)
        {
            Buffer.BlockCopy(_pendingBuffer, _pendingStart, _pendingBuffer, 0, _pendingLength);
            _pendingStart = 0;
            if (_pendingBuffer.Length - _pendingLength >= bytesToAppend)
                return;
        }

        var required = _pendingLength + bytesToAppend;
        var newSize = Math.Max(required, Math.Max(_pendingBuffer.Length * 2, 8192));
        Array.Resize(ref _pendingBuffer, newSize);
    }

    private void EnsureConvertCapacity(int requiredBytes)
    {
        if (_convertBuffer.Length >= requiredBytes)
            return;

        var newSize = Math.Max(requiredBytes, Math.Max(_convertBuffer.Length * 2, 8192));
        Array.Resize(ref _convertBuffer, newSize);
    }

    private void ClearPending()
    {
        _pendingStart = 0;
        _pendingLength = 0;
    }

    private void ResetDecodeState()
    {
        ClearPending();
        _inputEof = false;
        _drainPacketSent = false;
        _decoderEof = false;
    }

    private void ReleaseNativeResources()
    {
        if (_formatCtx != 0)
        {
            var f = (AVFormatContext*)_formatCtx;
            ffmpeg.avformat_close_input(&f);
            _formatCtx = 0;
        }

        if (_avioCtx != 0)
        {
            var avio = (AVIOContext*)_avioCtx;
            if (avio->buffer != null)
            {
                ffmpeg.av_free(avio->buffer);
                avio->buffer = null;
            }

            ffmpeg.avio_context_free(&avio);
            _avioCtx = 0;
            _avioBuffer = 0;
        }
        else if (_avioBuffer != 0)
        {
            ffmpeg.av_free((void*)_avioBuffer);
            _avioBuffer = 0;
        }

        if (_frame != 0)
        {
            var f = (AVFrame*)_frame;
            ffmpeg.av_frame_free(&f);
            _frame = 0;
        }

        if (_packet != 0)
        {
            var p = (AVPacket*)_packet;
            ffmpeg.av_packet_free(&p);
            _packet = 0;
        }

        if (_swrCtx != 0)
        {
            var s = (SwrContext*)_swrCtx;
            ffmpeg.swr_free(&s);
            _swrCtx = 0;
        }

        if (_codecCtx != 0)
        {
            var c = (AVCodecContext*)_codecCtx;
            ffmpeg.avcodec_free_context(&c);
            _codecCtx = 0;
        }

        if (_hasStreamState)
        {
            var state = _streamStateHandle.Target as StreamIoState;
            _streamStateHandle.Free();
            _hasStreamState = false;

            if (state is { LeaveOpen: false })
                state.Stream.Dispose();
        }
    }

    private sealed class StreamIoState
    {
        public StreamIoState(Stream stream, bool leaveOpen)
        {
            Stream = stream;
            LeaveOpen = leaveOpen;
        }

        public Stream Stream { get; }
        public bool LeaveOpen { get; }
        public Lock SyncRoot { get; } = new();
    }

    private static int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        try
        {
            var state = GetStreamState(opaque);
            if (state == null || bufferSize <= 0)
                return ffmpeg.AVERROR(ffmpeg.EINVAL);

            lock (state.SyncRoot)
            {
                var destination = new Span<byte>(buffer, bufferSize);
                var bytesRead = state.Stream.Read(destination);
                if (bytesRead <= 0)
                    return ffmpeg.AVERROR_EOF;

                return bytesRead;
            }
        }
        catch
        {
            return ffmpeg.AVERROR(ffmpeg.EINVAL);
        }
    }

    private static long SeekPacket(void* opaque, long offset, int whence)
    {
        try
        {
            var state = GetStreamState(opaque);
            if (state == null || !state.Stream.CanSeek)
                return ffmpeg.AVERROR(ffmpeg.EINVAL);

            lock (state.SyncRoot)
            {
                if ((whence & ffmpeg.AVSEEK_SIZE) != 0)
                    return state.Stream.Length;

                long origin;
                var baseWhence = whence & ~ffmpeg.AVSEEK_FORCE;
                if (baseWhence == SeekSet)
                    origin = (long)SeekOrigin.Begin;
                else if (baseWhence == SeekCur)
                    origin = (long)SeekOrigin.Current;
                else if (baseWhence == SeekEnd)
                    origin = (long)SeekOrigin.End;
                else
                    return ffmpeg.AVERROR(ffmpeg.EINVAL);

                var position = state.Stream.Seek(offset, (SeekOrigin)origin);
                return position;
            }
        }
        catch
        {
            return ffmpeg.AVERROR(ffmpeg.EINVAL);
        }
    }

    private static StreamIoState? GetStreamState(void* opaque)
    {
        if (opaque == null)
            return null;

        var handle = GCHandle.FromIntPtr((nint)opaque);
        return handle.Target as StreamIoState;
    }

    private static AVCodec* ResolveAudioStreamAndCodec(AVFormatContext* formatContext, int? preferredStreamIndex, out int streamIndex)
    {
        if (preferredStreamIndex.HasValue)
        {
            var index = preferredStreamIndex.Value;
            if (index < 0 || index >= formatContext->nb_streams)
                throw new ArgumentOutOfRangeException(nameof(preferredStreamIndex), $"Audio stream index {index} is outside stream range.");

            var stream = formatContext->streams[index];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                throw new ArgumentException($"Stream {index} is not an audio stream.", nameof(preferredStreamIndex));

            var explicitCodec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (explicitCodec == null)
                throw new InvalidOperationException($"No decoder found for stream {index} codec id {stream->codecpar->codec_id}.");

            streamIndex = index;
            return explicitCodec;
        }

        AVCodec* bestCodec = null;
        streamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &bestCodec, 0);
        if (streamIndex < 0)
            throw new Exception($"av_find_best_stream(audio): {GetErrorText(streamIndex)}");

        return bestCodec;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFAudioDecoder));
    }

    private static string GetErrorText(int code)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(code, buffer, 1024);
        return Marshal.PtrToStringAnsi((nint)buffer) ?? code.ToString();
    }
}