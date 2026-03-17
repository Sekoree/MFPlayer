using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Seko.OwnAudioNET.Video.Decoders;

/// <summary>
/// FFmpeg-based video decoder that produces format-negotiated <see cref="VideoFrame"/> objects.
/// Supports both software and hardware-accelerated (VAAPI/VDPAU/Vulkan) decoding.
/// </summary>
public unsafe partial class FFVideoDecoder : IVideoDecoder
{
    private const int SwsBilinear = 2;

    private nint _formatCtx;
    private nint _codecCtx;
    private nint _packet;
    private nint _frame;
    private nint _outputFrame;
    private nint _swsCtx;
    private nint _hwDeviceCtx;
    private nint _swFrame;

    private int _videoStreamIndex;
    private AVRational _timeBase;
    private int _width;
    private int _height;
    private int _outputPlane0Stride;
    private int _outputPlane1Stride;
    private int _outputPlane2Stride;
    private int _swsSrcWidth = -1;
    private int _swsSrcHeight = -1;
    private AVPixelFormat _swsSrcPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVPixelFormat _swsDstPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    private readonly VideoPixelFormat[] _preferredOutputFormats;
    private readonly bool _preferSourcePixelFormatWhenSupported;
    private readonly bool _preferLowestConversionCost;
    private VideoPixelFormat _activeOutputFormat;
    private AVPixelFormat _activeOutputAvPixelFormat;
    private AVPixelFormat _lastSourceAvPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    private bool _inputEof;
    private bool _drainPacketSent;
    private bool _decoderEof;
    private bool _disposed;
    private bool _isHardwareDecoding;
    private double _lastPtsSeconds;
    private readonly double _fallbackFrameDuration;
    private readonly double _streamFrameRate;
    private readonly TimeSpan _streamDuration;
    private readonly long? _streamFrameCount;

    /// <summary>Metadata describing the decoded video stream.</summary>
    public VideoStreamInfo StreamInfo { get; private set; }

    /// <summary><see langword="true"/> once the decoder has consumed and flushed all frames.</summary>
    public bool IsEndOfStream => _decoderEof;

    /// <summary><see langword="true"/> when a hardware-accelerated decode context is active.</summary>
    public bool IsHardwareDecoding => _isHardwareDecoding;

    /// <summary>Last decoded source pixel format reported by FFmpeg (for diagnostics).</summary>
    public string LastSourcePixelFormatName => _lastSourceAvPixelFormat.ToString();

    /// <summary>Current active decoder output pixel format (for diagnostics).</summary>
    public string LastOutputPixelFormatName => _activeOutputFormat.ToString();

    /// <inheritdoc/>
    public event Action<VideoStreamInfo>? StreamInfoChanged;

    /// <summary>Initializes a new <see cref="FFVideoDecoder"/> with default options.</summary>
    /// <param name="filePath">Path to the media file to open.</param>
    public FFVideoDecoder(string filePath)
        : this(filePath, new FFVideoDecoderOptions())
    {
    }

    /// <summary>Initializes a new <see cref="FFVideoDecoder"/>.</summary>
    /// <param name="filePath">Path to the media file to open.</param>
    /// <param name="options">Decoder configuration.</param>
    public FFVideoDecoder(string filePath, FFVideoDecoderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _preferredOutputFormats = ResolvePreferredOutputFormats(options.PreferredOutputPixelFormats);
        _preferSourcePixelFormatWhenSupported = options.PreferSourcePixelFormatWhenSupported;
        _preferLowestConversionCost = options.PreferLowestConversionCost;
        _activeOutputFormat = _preferredOutputFormats[0];
        _activeOutputAvPixelFormat = ToAvPixelFormat(_activeOutputFormat);

        try
        {
            var fCtx = ffmpeg.avformat_alloc_context();
            if (fCtx == null)
                throw new Exception("avformat_alloc_context failed");

            var openInputResult = ffmpeg.avformat_open_input(&fCtx, filePath, null, null);
            if (openInputResult < 0)
                throw new Exception($"avformat_open_input: {GetErrorText(openInputResult)}");

            _formatCtx = (nint)fCtx;

            var findStreamInfoResult = ffmpeg.avformat_find_stream_info(fCtx, null);
            if (findStreamInfoResult < 0)
                throw new Exception($"avformat_find_stream_info: {GetErrorText(findStreamInfoResult)}");

            AVCodec* codec;
            _videoStreamIndex = ResolveVideoStreamIndex(fCtx, options.PreferredStreamIndex, out codec);

            var stream = fCtx->streams[_videoStreamIndex];
            _timeBase = stream->time_base;

            var cCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (cCtx == null)
                throw new Exception("avcodec_alloc_context3 failed");

            var parametersResult = ffmpeg.avcodec_parameters_to_context(cCtx, stream->codecpar);
            if (parametersResult < 0)
                throw new Exception($"avcodec_parameters_to_context: {GetErrorText(parametersResult)}");

            if (options.ThreadCount > 0)
                cCtx->thread_count = options.ThreadCount;

            TryEnableHardwareDecoding(cCtx, options);

            var codecOpenResult = ffmpeg.avcodec_open2(cCtx, codec, null);
            if (codecOpenResult < 0)
                throw new Exception($"avcodec_open2: {GetErrorText(codecOpenResult)}");

            _codecCtx = (nint)cCtx;
            _width = cCtx->width;
            _height = cCtx->height;

            _packet = (nint)ffmpeg.av_packet_alloc();
            _frame = (nint)ffmpeg.av_frame_alloc();
            _outputFrame = (nint)ffmpeg.av_frame_alloc();
            _swFrame = (nint)ffmpeg.av_frame_alloc();
            if (_packet == 0 || _frame == 0 || _outputFrame == 0 || _swFrame == 0)
                throw new Exception("av_packet_alloc/av_frame_alloc failed");

            var outputFrame = (AVFrame*)_outputFrame;
            outputFrame->format = (int)_activeOutputAvPixelFormat;
            outputFrame->width = _width;
            outputFrame->height = _height;

            var frameBufferResult = ffmpeg.av_frame_get_buffer(outputFrame, 1);
            if (frameBufferResult < 0)
                throw new Exception($"av_frame_get_buffer(output): {GetErrorText(frameBufferResult)}");

            _outputPlane0Stride = outputFrame->linesize[0];
            _outputPlane1Stride = outputFrame->linesize[1];
            _outputPlane2Stride = outputFrame->linesize[2];

            _swsCtx = 0;

            _streamFrameRate = ResolveFrameRate(stream);
            _streamDuration = fCtx->duration > 0
                ? TimeSpan.FromSeconds(fCtx->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;
            _streamFrameCount = ResolveFrameCount(stream, _streamFrameRate, _streamDuration);
            _fallbackFrameDuration = _streamFrameRate > 0 ? 1.0 / _streamFrameRate : 1.0 / 30.0;

            UpdateStreamInfo(raiseEvent: false);
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool TryDecodeNextFrame(out VideoFrame frame, out string? error)
    {
        EnsureNotDisposed();

        frame = null!;
        error = null;

        var fmtCtx = (AVFormatContext*)_formatCtx;
        var codecCtx = (AVCodecContext*)_codecCtx;
        var packet = (AVPacket*)_packet;
        var decodedFrame = (AVFrame*)_frame;
        var swFrame = (AVFrame*)_swFrame;
        var outputFrame = (AVFrame*)_outputFrame;

        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(codecCtx, decodedFrame);
            if (receiveResult == 0)
            {
                AVFrame* sourceFrame = decodedFrame;
                if (decodedFrame->hw_frames_ctx != null)
                {
                    ffmpeg.av_frame_unref(swFrame);
                    var transferResult = ffmpeg.av_hwframe_transfer_data(swFrame, decodedFrame, 0);
                    if (transferResult < 0)
                    {
                        ffmpeg.av_frame_unref(decodedFrame);
                        error = $"av_hwframe_transfer_data failed: {GetErrorText(transferResult)}";
                        return false;
                    }

                    sourceFrame = swFrame;
                }

                var selectedOutputFormat = SelectOutputPixelFormat(sourceFrame);
                _lastSourceAvPixelFormat = (AVPixelFormat)sourceFrame->format;
                if (!EnsureOutputFrameForSource(sourceFrame, selectedOutputFormat, out error))
                {
                    ffmpeg.av_frame_unref(decodedFrame);
                    ffmpeg.av_frame_unref(swFrame);
                    return false;
                }

                var sourceForCopy = sourceFrame;
                var needsConversion = !CanCopyFrameDirectly(sourceFrame, selectedOutputFormat);
                if (needsConversion)
                {
                    if (!EnsureScaleContext(sourceFrame, selectedOutputFormat, out error))
                    {
                        ffmpeg.av_frame_unref(decodedFrame);
                        ffmpeg.av_frame_unref(swFrame);
                        return false;
                    }

                    var makeWritableResult = ffmpeg.av_frame_make_writable(outputFrame);
                    if (makeWritableResult < 0)
                    {
                        ffmpeg.av_frame_unref(decodedFrame);
                        ffmpeg.av_frame_unref(swFrame);
                        error = $"av_frame_make_writable(output) failed: {GetErrorText(makeWritableResult)}";
                        return false;
                    }

                    var scaledHeight = ffmpeg.sws_scale(
                        (SwsContext*)_swsCtx,
                        sourceFrame->data,
                        sourceFrame->linesize,
                        0,
                        sourceFrame->height,
                        outputFrame->data,
                        outputFrame->linesize);

                    if (scaledHeight <= 0)
                    {
                        ffmpeg.av_frame_unref(decodedFrame);
                        ffmpeg.av_frame_unref(swFrame);
                        error = "sws_scale failed.";
                        return false;
                    }

                    sourceForCopy = outputFrame;
                }

                var ptsSeconds = ResolvePtsSeconds(decodedFrame);
                if (!TryCopyFrameToVideoFrame(sourceForCopy, selectedOutputFormat, ptsSeconds, out frame, out error))
                {
                    ffmpeg.av_frame_unref(decodedFrame);
                    ffmpeg.av_frame_unref(swFrame);
                    return false;
                }

                ffmpeg.av_frame_unref(decodedFrame);
                ffmpeg.av_frame_unref(swFrame);
                return true;
            }

            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                _decoderEof = true;
                error = "End of video stream.";
                return false;
            }

            if (receiveResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                error = $"avcodec_receive_frame failed: {GetErrorText(receiveResult)}";
                return false;
            }

            if (_inputEof)
            {
                if (_drainPacketSent)
                {
                    _decoderEof = true;
                    error = "End of video stream.";
                    return false;
                }

                var drainResult = ffmpeg.avcodec_send_packet(codecCtx, null);
                if (drainResult < 0 && drainResult != ffmpeg.AVERROR_EOF)
                {
                    error = $"avcodec_send_packet(null) failed: {GetErrorText(drainResult)}";
                    return false;
                }

                _drainPacketSent = true;
                continue;
            }

            var readResult = ffmpeg.av_read_frame(fmtCtx, packet);
            if (readResult < 0)
            {
                if (readResult == ffmpeg.AVERROR_EOF)
                {
                    _inputEof = true;
                    continue;
                }

                error = $"av_read_frame failed: {GetErrorText(readResult)}";
                return false;
            }

            if (packet->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(packet);
                continue;
            }

            var sendResult = ffmpeg.avcodec_send_packet(codecCtx, packet);
            ffmpeg.av_packet_unref(packet);

            if (sendResult == 0 || sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                continue;

            if (sendResult == ffmpeg.AVERROR_EOF)
            {
                _decoderEof = true;
                error = "End of video stream.";
                return false;
            }

            error = $"avcodec_send_packet failed: {GetErrorText(sendResult)}";
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TrySeek(TimeSpan position, out string error)
    {
        EnsureNotDisposed();

        error = string.Empty;

        var fCtx = (AVFormatContext*)_formatCtx;
        var cCtx = (AVCodecContext*)_codecCtx;
        var pkt = (AVPacket*)_packet;
        var frm = (AVFrame*)_frame;
        var sw = (AVFrame*)_swFrame;
        var stream = fCtx->streams[_videoStreamIndex];

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        var avTimeBaseQ = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        var targetUs = (long)Math.Round(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
        var targetTs = ffmpeg.av_rescale_q(targetUs, avTimeBaseQ, stream->time_base);

        var seekResult = ffmpeg.av_seek_frame(fCtx, _videoStreamIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (seekResult < 0)
        {
            error = $"av_seek_frame failed: {GetErrorText(seekResult)}";
            return false;
        }

        ffmpeg.avcodec_flush_buffers(cCtx);
        ffmpeg.avformat_flush(fCtx);
        ffmpeg.av_packet_unref(pkt);
        ffmpeg.av_frame_unref(frm);
        ffmpeg.av_frame_unref(sw);

        _inputEof = false;
        _drainPacketSent = false;
        _decoderEof = false;
        _lastPtsSeconds = position.TotalSeconds;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    private double ResolvePtsSeconds(AVFrame* decodedFrame)
    {
        var pts = decodedFrame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
            pts = decodedFrame->pts;

        if (pts == ffmpeg.AV_NOPTS_VALUE)
        {
            _lastPtsSeconds += _fallbackFrameDuration;
            return _lastPtsSeconds;
        }

        var seconds = pts * ffmpeg.av_q2d(_timeBase);
        _lastPtsSeconds = seconds;
        return seconds;
    }

    private static double ResolveFrameRate(AVStream* stream)
    {
        if (IsSingleFrameStream(stream))
            return 1.0;

        var frameRate = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (IsReasonableFrameRate(frameRate))
            return frameRate;

        frameRate = ffmpeg.av_q2d(stream->r_frame_rate);
        if (IsReasonableFrameRate(frameRate))
            return frameRate;

        if (stream->nb_frames > 1 && stream->duration > 0)
        {
            var seconds = stream->duration * ffmpeg.av_q2d(stream->time_base);
            if (seconds > 0)
            {
                var estimated = stream->nb_frames / seconds;
                if (IsReasonableFrameRate(estimated))
                    return estimated;
            }
        }

        return 30.0;
    }

    private static bool IsSingleFrameStream(AVStream* stream)
    {
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            return true;

        return stream->nb_frames == 1;
    }

    private static bool IsReasonableFrameRate(double frameRate)
    {
        return frameRate is >= 1.0 and <= 240.0;
    }

    private static long? ResolveFrameCount(AVStream* stream, double frameRate, TimeSpan streamDuration)
    {
        if (stream->nb_frames > 0)
            return stream->nb_frames;

        if (IsSingleFrameStream(stream))
            return 1;

        if (!IsReasonableFrameRate(frameRate) || streamDuration <= TimeSpan.Zero)
            return null;

        var estimated = (long)Math.Round(streamDuration.TotalSeconds * frameRate, MidpointRounding.AwayFromZero);
        return estimated > 0 ? estimated : null;
    }

    private static int ResolveVideoStreamIndex(AVFormatContext* formatContext, int? preferredStreamIndex, out AVCodec* codec)
    {
        codec = null;

        if (preferredStreamIndex.HasValue)
        {
            var index = preferredStreamIndex.Value;
            if (index < 0 || index >= formatContext->nb_streams)
                throw new ArgumentOutOfRangeException(nameof(preferredStreamIndex), $"Video stream index {index} is outside stream range.");

            var stream = formatContext->streams[index];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                throw new ArgumentException($"Stream {index} is not a video stream.", nameof(preferredStreamIndex));

            codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException($"No decoder found for stream {index} codec id {stream->codecpar->codec_id}.");

            return index;
        }

        AVCodec* bestCodec = null;
        var selected = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &bestCodec, 0);
        if (selected < 0)
            throw new Exception($"av_find_best_stream(video): {GetErrorText(selected)}");

        codec = bestCodec;

        return selected;
    }

    private bool EnsureScaleContext(AVFrame* sourceFrame, VideoPixelFormat outputFormat, out string? error)
    {
        error = null;

        if (sourceFrame->width <= 0 || sourceFrame->height <= 0)
        {
            error = "Decoded frame has invalid dimensions.";
            return false;
        }

        if (sourceFrame->data[0] == null)
        {
            error = "Decoded frame has invalid source pointers.";
            return false;
        }

        var sourcePixelFormat = (AVPixelFormat)sourceFrame->format;
        if (sourcePixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
        {
            error = "Decoded frame has unknown pixel format.";
            return false;
        }

        var destinationPixelFormat = ToAvPixelFormat(outputFormat);

        if (_swsCtx != 0 &&
            _swsSrcWidth == sourceFrame->width &&
            _swsSrcHeight == sourceFrame->height &&
            _swsSrcPixelFormat == sourcePixelFormat &&
            _swsDstPixelFormat == destinationPixelFormat)
        {
            return true;
        }

        var swsContext = ffmpeg.sws_getCachedContext(
            (SwsContext*)_swsCtx,
            sourceFrame->width,
            sourceFrame->height,
            sourcePixelFormat,
            _width,
            _height,
            destinationPixelFormat,
            SwsBilinear,
            null,
            null,
            null);

        if (swsContext == null)
        {
            error = $"sws_getCachedContext failed for source format {sourcePixelFormat}.";
            return false;
        }

        _swsCtx = (nint)swsContext;
        _swsSrcWidth = sourceFrame->width;
        _swsSrcHeight = sourceFrame->height;
        _swsSrcPixelFormat = sourcePixelFormat;
        _swsDstPixelFormat = destinationPixelFormat;
        return true;
    }

    private bool EnsureOutputFrameForSource(AVFrame* sourceFrame, VideoPixelFormat outputFormat, out string? error)
    {
        error = null;

        var outputAvPixelFormat = ToAvPixelFormat(outputFormat);
        if (_width == sourceFrame->width &&
            _height == sourceFrame->height &&
            _activeOutputFormat == outputFormat)
            return true;

        var outputFrame = (AVFrame*)_outputFrame;
        ffmpeg.av_frame_unref(outputFrame);

        _width = sourceFrame->width;
        _height = sourceFrame->height;
        _activeOutputFormat = outputFormat;
        _activeOutputAvPixelFormat = outputAvPixelFormat;

        outputFrame->format = (int)_activeOutputAvPixelFormat;
        outputFrame->width = _width;
        outputFrame->height = _height;

        var frameBufferResult = ffmpeg.av_frame_get_buffer(outputFrame, 1);
        if (frameBufferResult < 0)
        {
            error = $"av_frame_get_buffer(output) resize failed: {GetErrorText(frameBufferResult)}";
            return false;
        }

        _outputPlane0Stride = outputFrame->linesize[0];
        _outputPlane1Stride = outputFrame->linesize[1];
        _outputPlane2Stride = outputFrame->linesize[2];
        UpdateStreamInfo(raiseEvent: true);

        // Force scaler reconfiguration after output geometry changes.
        _swsSrcWidth = -1;
        _swsSrcHeight = -1;
        _swsSrcPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _swsDstPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        return true;
    }

    private void UpdateStreamInfo(bool raiseEvent)
    {
        var updated = new VideoStreamInfo(
            width: _width,
            height: _height,
            frameRate: _streamFrameRate,
            duration: _streamDuration,
            pixelFormat: _activeOutputFormat,
            frameCount: _streamFrameCount);

        var previous = StreamInfo;
        StreamInfo = updated;

        if (!raiseEvent)
            return;

        if (previous.Width == updated.Width &&
            previous.Height == updated.Height &&
            previous.FrameRate == updated.FrameRate &&
            previous.Duration == updated.Duration &&
            previous.PixelFormat == updated.PixelFormat &&
            previous.FrameCount == updated.FrameCount)
        {
            return;
        }

        StreamInfoChanged?.Invoke(updated);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FFVideoDecoder));
    }

    private void ReleaseResources()
    {
        if (_swFrame != 0)
        {
            var f = (AVFrame*)_swFrame;
            ffmpeg.av_frame_free(&f);
            _swFrame = 0;
        }

        if (_frame != 0)
        {
            var f = (AVFrame*)_frame;
            ffmpeg.av_frame_free(&f);
            _frame = 0;
        }

        if (_outputFrame != 0)
        {
            var f = (AVFrame*)_outputFrame;
            ffmpeg.av_frame_free(&f);
            _outputFrame = 0;
        }

        if (_packet != 0)
        {
            var p = (AVPacket*)_packet;
            ffmpeg.av_packet_free(&p);
            _packet = 0;
        }

        if (_swsCtx != 0)
        {
            ffmpeg.sws_freeContext((SwsContext*)_swsCtx);
            _swsCtx = 0;
        }

        if (_hwDeviceCtx != 0)
        {
            var hw = (AVBufferRef*)_hwDeviceCtx;
            ffmpeg.av_buffer_unref(&hw);
            _hwDeviceCtx = 0;
        }


        if (_codecCtx != 0)
        {
            var c = (AVCodecContext*)_codecCtx;
            ffmpeg.avcodec_free_context(&c);
            _codecCtx = 0;
        }

        if (_formatCtx != 0)
        {
            var f = (AVFormatContext*)_formatCtx;
            ffmpeg.avformat_close_input(&f);
            _formatCtx = 0;
        }
    }

    private static string GetErrorText(int code)
    {
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(code, buffer, 1024);
        return Marshal.PtrToStringAnsi((nint)buffer) ?? code.ToString();
    }

    private void TryEnableHardwareDecoding(AVCodecContext* codecContext, FFVideoDecoderOptions options)
    {
        if (!options.EnableHardwareDecoding)
            return;

        var device = ResolvePreferredHardwareDevice(options.PreferredHardwareDevice);
        if (device == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            return;

        AVBufferRef* hwDevice = null;
        var createResult = ffmpeg.av_hwdevice_ctx_create(&hwDevice, device, null, null, 0);
        if (createResult < 0 || hwDevice == null)
            return;

        codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
        _hwDeviceCtx = (nint)hwDevice;
        _isHardwareDecoding = codecContext->hw_device_ctx != null;
    }

    private static AVHWDeviceType ResolvePreferredHardwareDevice(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var explicitDevice = ffmpeg.av_hwdevice_find_type_by_name(preferred);
            if (explicitDevice != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                return explicitDevice;
        }

        var linuxPreferred = new[]
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN
        };

        foreach (var candidate in linuxPreferred)
        {
            var name = ffmpeg.av_hwdevice_get_type_name(candidate);
            if (name != null)
                return candidate;
        }

        return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    }
}