using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Seko.OwnAudioSharp.Video.Decoders;

public unsafe class FFVideoDecoder : IVideoDecoder
{
    private const int SwsBilinear = 2;

    private nint _formatCtx;
    private nint _codecCtx;
    private nint _packet;
    private nint _frame;
    private nint _rgbaFrame;
    private nint _swsCtx;
    private nint _hwDeviceCtx;
    private nint _swFrame;

    private int _videoStreamIndex;
    private AVRational _timeBase;
    private int _width;
    private int _height;
    private int _rgbaStride;

    private bool _inputEof;
    private bool _drainPacketSent;
    private bool _decoderEof;
    private bool _disposed;
    private bool _isHardwareDecoding;
    private double _lastPtsSeconds;
    private readonly double _fallbackFrameDuration;

    public VideoStreamInfo StreamInfo { get; }
    public bool IsEndOfStream => _decoderEof;
    public bool IsHardwareDecoding => _isHardwareDecoding;

    public FFVideoDecoder(string filePath)
        : this(filePath, new FFVideoDecoderOptions())
    {
    }

    public FFVideoDecoder(string filePath, FFVideoDecoderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        options ??= new FFVideoDecoderOptions();

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

            AVCodec* codec = null;
            _videoStreamIndex = ffmpeg.av_find_best_stream(fCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (_videoStreamIndex < 0)
                throw new Exception($"av_find_best_stream(video): {GetErrorText(_videoStreamIndex)}");

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
            _rgbaFrame = (nint)ffmpeg.av_frame_alloc();
            _swFrame = (nint)ffmpeg.av_frame_alloc();
            if (_packet == 0 || _frame == 0 || _rgbaFrame == 0 || _swFrame == 0)
                throw new Exception("av_packet_alloc/av_frame_alloc failed");

            var rgbaFrame = (AVFrame*)_rgbaFrame;
            rgbaFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            rgbaFrame->width = _width;
            rgbaFrame->height = _height;

            var frameBufferResult = ffmpeg.av_frame_get_buffer(rgbaFrame, 1);
            if (frameBufferResult < 0)
                throw new Exception($"av_frame_get_buffer(rgba): {GetErrorText(frameBufferResult)}");

            _rgbaStride = rgbaFrame->linesize[0];

            _swsCtx = (nint)ffmpeg.sws_getContext(
                _width,
                _height,
                cCtx->pix_fmt,
                _width,
                _height,
                AVPixelFormat.AV_PIX_FMT_RGBA,
                SwsBilinear,
                null,
                null,
                null);
            if (_swsCtx == 0)
                throw new Exception("sws_getContext failed");

            var frameRate = ResolveFrameRate(stream);
            _fallbackFrameDuration = frameRate > 0 ? 1.0 / frameRate : 1.0 / 30.0;

            StreamInfo = new VideoStreamInfo(
                width: _width,
                height: _height,
                frameRate: frameRate,
                duration: fCtx->duration > 0
                    ? TimeSpan.FromSeconds(fCtx->duration / (double)ffmpeg.AV_TIME_BASE)
                    : TimeSpan.Zero,
                pixelFormat: VideoPixelFormat.Rgba32);
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

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
        var rgbaFrame = (AVFrame*)_rgbaFrame;

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

                ffmpeg.sws_scale(
                    (SwsContext*)_swsCtx,
                    sourceFrame->data,
                    sourceFrame->linesize,
                    0,
                    codecCtx->height,
                    rgbaFrame->data,
                    rgbaFrame->linesize);

                var ptsSeconds = ResolvePtsSeconds(decodedFrame);
                var dataLength = _rgbaStride * _height;
                frame = VideoFrame.CreatePooled(dataLength, _width, _height, _rgbaStride, ptsSeconds);
                Marshal.Copy((nint)rgbaFrame->data[0], frame.RgbaData, 0, dataLength);

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
        var frameRate = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (frameRate > 0)
            return frameRate;

        frameRate = ffmpeg.av_q2d(stream->r_frame_rate);
        if (frameRate > 0)
            return frameRate;

        return 30.0;
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

        if (_rgbaFrame != 0)
        {
            var f = (AVFrame*)_rgbaFrame;
            ffmpeg.av_frame_free(&f);
            _rgbaFrame = 0;
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