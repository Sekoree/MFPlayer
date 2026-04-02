using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFVideoDecoder : IDisposable
{

    private bool _disposed;
    private bool _initialized;
    private bool _nativeDecodeEnabled = true;
    private FFNativeVideoDecoderBackend? _nativeBackend;
    private int _threadCount;
    private bool _lowLatency;
    private bool _enableHardwareDecode;

    internal bool IsNativeDecodeEnabled => _nativeDecodeEnabled;

    public int Initialize(FFmpegDecodeOptions? decodeOptions = null)
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegDecoderInitFailed;
        }

        _nativeBackend?.Dispose();
        _nativeBackend = null;
        _nativeDecodeEnabled = true;
        _threadCount = decodeOptions?.DecodeThreadCount ?? 0;
        _lowLatency = decodeOptions?.LowLatencyMode ?? false;
        _enableHardwareDecode = decodeOptions?.EnableHardwareDecode ?? false;
        _initialized = true;
        return MediaResult.Success;
    }

    /// <summary>
    /// Decodes a video packet. Returns <see cref="MediaResult.Success"/> when a frame is produced,
    /// <c>EAGAIN</c> (via <see cref="MediaErrorCode.FFmpegVideoDecodeNeedMoreData"/>) when the
    /// decoder needs more packets before it can output a frame, or an error code on failure.
    /// </summary>
    public int Decode(FFPacket packet, out FFVideoDecodeResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegVideoDecodeFailed;
        }

        if (!_nativeDecodeEnabled)
        {
            return (int)MediaErrorCode.FFmpegVideoDecodeFailed;
        }

        var status = TryDecodeNative(packet, out var nativeResult);
        switch (status)
        {
            case DecodeStatus.Success:
                result = nativeResult;
                return MediaResult.Success;
            case DecodeStatus.NeedMoreData:
                return (int)MediaErrorCode.FFmpegVideoDecodeNeedMoreData;
            default:
                return (int)MediaErrorCode.FFmpegVideoDecodeFailed;
        }
    }

    /// <summary>Flushes the native codec context after a seek to discard stale B-frame / reference state.</summary>
    public void FlushCodecBuffers()
    {
        _nativeBackend?.FlushCodecBuffers();
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
    }

    private enum DecodeStatus { Success, NeedMoreData, Failed }

    private DecodeStatus TryDecodeNative(FFPacket packet, out FFVideoDecodeResult result)
    {
        result = default;

        if (packet.NativeCodecId is null || packet.NativePacketData is null || packet.NativePacketData.Length == 0)
        {
            return DecodeStatus.Failed;
        }

        try
        {
            _nativeBackend ??= new FFNativeVideoDecoderBackend();
            if (!_nativeBackend.TryEnsureInitialized(packet.NativeCodecId.Value, packet.NativeCodecParametersPtr, _threadCount, _lowLatency, _enableHardwareDecode))
            {
                _nativeDecodeEnabled = false;
                return DecodeStatus.Failed;
            }

            if (!_nativeBackend.TryDecode(
                    packet.NativePacketData,
                    packet.NativePacketFlags,
                    out var width,
                    out var height,
                    out var keyFrameFromDecoder,
                    out var nativePixelFormat,
                    out var plane0,
                    out var plane0Stride,
                    out var plane1,
                    out var plane1Stride,
                    out var plane2,
                    out var plane2Stride,
                    out var needMoreData))
            {
                if (needMoreData)
                {
                    return DecodeStatus.NeedMoreData;
                }

                // Per-frame decode failure (e.g. corrupt H.264 reference frames).
                // Do NOT set _nativeDecodeEnabled = false here — the decoder can
                // recover on the next keyframe. Only initialization / library-level
                // failures should permanently disable native decode.
                return DecodeStatus.Failed;
            }

            result = new FFVideoDecodeResult(
                packet.Generation,
                packet.Sequence,
                packet.PresentationTime,
                keyFrameFromDecoder || packet.IsKeyFrame,
                Math.Max(1, width),
                Math.Max(1, height),
                plane0,
                plane0Stride,
                plane1,
                plane1Stride,
                plane2,
                plane2Stride,
                NativeTimeBaseNumerator: packet.NativeTimeBaseNumerator,
                NativeTimeBaseDenominator: packet.NativeTimeBaseDenominator,
                NativeFrameRateNumerator: packet.NativeFrameRateNumerator,
                NativeFrameRateDenominator: packet.NativeFrameRateDenominator,
                NativePixelFormat: nativePixelFormat);
            return DecodeStatus.Success;
        }
        catch (DllNotFoundException)
        {
            _nativeDecodeEnabled = false;
            return DecodeStatus.Failed;
        }
        catch (EntryPointNotFoundException)
        {
            _nativeDecodeEnabled = false;
            return DecodeStatus.Failed;
        }
        catch (TypeInitializationException)
        {
            _nativeDecodeEnabled = false;
            return DecodeStatus.Failed;
        }
        catch (NotSupportedException)
        {
            _nativeDecodeEnabled = false;
            return DecodeStatus.Failed;
        }
        catch (ArgumentNullException)
        {
            // Marshal.Copy can throw when FFmpeg produces a frame with corrupt/null data
            // pointers (e.g. from severely damaged H.264 reference frames).
            return DecodeStatus.Failed;
        }
        catch (AccessViolationException)
        {
            // Native memory access failure from corrupt frame data.
            _nativeDecodeEnabled = false;
            return DecodeStatus.Failed;
        }
    }
}

internal unsafe sealed class FFNativeVideoDecoderBackend : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    // N5: pre-allocated packet reused across TryDecode calls to avoid per-call native heap allocation.
    private AVPacket* _packet;
    private AVCodecID? _codecId;
    private bool _disposed;

    // ── I.1: Hardware decode state ──────────────────────────────────────────────
    private AVBufferRef* _hwDeviceCtx;
    private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVFrame* _swFrame;
    private bool _usingHardwareDecode;

    /// <summary>
    /// Maps codec context pointers to their target hardware pixel formats.
    /// Used by the static <see cref="GetHwFormatCallback"/> to select the correct
    /// hardware format during codec negotiation.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, AVPixelFormat> HwFormatMap = new();

    /// <summary>
    /// Pinned delegate instance for the <c>get_format</c> callback.
    /// Stored in a static field to prevent garbage collection while native code holds a pointer to it.
    /// </summary>
    private static readonly AVCodecContext_get_format GetHwFormatDelegate = GetHwFormatCallback;

    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 1 (not exposed as a constant by FFmpeg.AutoGen 8.0.0)
    private const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 1;

    public bool TryEnsureInitialized(int codecId, nint? codecParametersPtr, int threadCount = 0, bool lowLatency = false, bool enableHardwareDecode = false)
    {
        if (_disposed)
        {
            return false;
        }

        var requestedCodecId = (AVCodecID)codecId;
        if (_codecContext is not null && _codecId == requestedCodecId)
        {
            return true;
        }

        DisposeCodecContext();

        var codec = ffmpeg.avcodec_find_decoder(requestedCodecId);
        if (codec is null)
        {
            return false;
        }

        // I.1: Try hardware decode first, fall back to software on any failure.
        if (enableHardwareDecode && TryInitializeWithHardware(codec, requestedCodecId, codecParametersPtr, threadCount, lowLatency))
        {
            return true;
        }

        return TryInitializeSoftware(codec, requestedCodecId, codecParametersPtr, threadCount, lowLatency);
    }

    /// <summary>
    /// Enumerates hardware decode configurations for <paramref name="codec"/> and attempts
    /// to open the first one that succeeds. Returns <see langword="true"/> if hardware
    /// decode was successfully negotiated; the caller should fall back to software otherwise.
    /// </summary>
    private bool TryInitializeWithHardware(AVCodec* codec, AVCodecID codecId, nint? codecParametersPtr, int threadCount, bool lowLatency)
    {
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                break;

            if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0)
                continue;

            AVBufferRef* deviceCtx = null;
            if (ffmpeg.av_hwdevice_ctx_create(&deviceCtx, config->device_type, null, null, 0) < 0)
                continue;

            // Allocate codec context for this HW attempt.
            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext is null)
            {
                ffmpeg.av_buffer_unref(&deviceCtx);
                continue;
            }

            if (!TryApplyCodecParameters(codecParametersPtr))
            {
                ffmpeg.av_buffer_unref(&deviceCtx);
                DisposeCodecContext();
                continue;
            }

            ApplyCodecOptions(threadCount, lowLatency);

            // Wire the hardware device context and get_format callback.
            _hwDeviceCtx = deviceCtx;
            _hwPixelFormat = config->pix_fmt;
            _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            _usingHardwareDecode = true;

            HwFormatMap[(nint)_codecContext] = _hwPixelFormat;
            _codecContext->get_format = GetHwFormatDelegate;

            if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
            {
                CleanupHardwareDecode();
                DisposeCodecContext();
                continue; // try next hw config
            }

            _frame = ffmpeg.av_frame_alloc();
            _swFrame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();

            if (_frame is null || _swFrame is null || _packet is null)
            {
                CleanupHardwareDecode();
                DisposeCodecContext();
                continue;
            }

            _codecId = codecId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Standard software-only initialisation path — identical to the original
    /// <c>TryEnsureInitialized</c> logic before I.1.
    /// </summary>
    private bool TryInitializeSoftware(AVCodec* codec, AVCodecID codecId, nint? codecParametersPtr, int threadCount, bool lowLatency)
    {
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext is null)
        {
            return false;
        }

        if (!TryApplyCodecParameters(codecParametersPtr))
        {
            DisposeCodecContext();
            return false;
        }

        ApplyCodecOptions(threadCount, lowLatency);

        if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
        {
            DisposeCodecContext();
            return false;
        }

        _frame = ffmpeg.av_frame_alloc();
        if (_frame is null)
        {
            DisposeCodecContext();
            return false;
        }

        _packet = ffmpeg.av_packet_alloc();
        if (_packet is null)
        {
            DisposeCodecContext();
            return false;
        }

        _codecId = codecId;
        return true;
    }

    private bool TryApplyCodecParameters(nint? codecParametersPtr)
    {
        if (codecParametersPtr is not null && codecParametersPtr.Value != 0)
        {
            return ffmpeg.avcodec_parameters_to_context(_codecContext, (AVCodecParameters*)codecParametersPtr.Value) >= 0;
        }

        return true;
    }

    private void ApplyCodecOptions(int threadCount, bool lowLatency)
    {
        // Wire DecodeThreadCount: 0 = let FFmpeg auto-detect.
        if (threadCount > 0)
        {
            _codecContext->thread_count = threadCount;
            _codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        }

        // Wire LowLatencyMode: disable B-frame reordering for lower latency.
        if (lowLatency)
        {
            _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        }

        // Enable error concealment so the decoder attempts to produce usable frames
        // from corrupt input (e.g. missing reference frames in H.264) instead of failing.
        // FF_EC_GUESS_MVS (1) = guess motion vectors, FF_EC_DEBLOCK (2) = deblock damaged areas.
        _codecContext->error_concealment = ffmpeg.FF_EC_GUESS_MVS | ffmpeg.FF_EC_DEBLOCK;

        // Be lenient with errors — don't refuse to decode when bitstream errors are detected.
        _codecContext->err_recognition = 0;
    }

    /// <summary>
    /// Static callback used by FFmpeg during codec negotiation to select the
    /// hardware pixel format. Registered via <see cref="AVCodecContext.get_format"/>.
    /// </summary>
    private static AVPixelFormat GetHwFormatCallback(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        if (HwFormatMap.TryGetValue((nint)ctx, out var targetFormat))
        {
            for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == targetFormat)
                    return targetFormat;
            }
        }

        // Fallback: return the first offered format (software).
        return *formats;
    }

    public bool TryDecode(
        byte[] packetData,
        int packetFlags,
        out int width,
        out int height,
        out bool isKeyFrame,
        out int pixelFormat,
        out ReadOnlyMemory<byte> plane0,
        out int plane0Stride,
        out ReadOnlyMemory<byte> plane1,
        out int plane1Stride,
        out ReadOnlyMemory<byte> plane2,
        out int plane2Stride,
        out bool needMoreData)
    {
        width = 0;
        height = 0;
        isKeyFrame = false;
        pixelFormat = 0;
        plane0 = default;
        plane0Stride = 0;
        plane1 = default;
        plane1Stride = 0;
        plane2 = default;
        plane2Stride = 0;
        needMoreData = false;

        if (_disposed || _codecContext is null || _frame is null || _packet is null)
        {
            return false;
        }

        // N5: reuse the pre-allocated packet — unref first so av_grow_packet starts from a clean state.
        ffmpeg.av_packet_unref(_packet);
        if (ffmpeg.av_grow_packet(_packet, packetData.Length) < 0)
        {
            return false;
        }

        Marshal.Copy(packetData, 0, (IntPtr)_packet->data, packetData.Length);
        _packet->flags = packetFlags;

        if (ffmpeg.avcodec_send_packet(_codecContext, _packet) < 0)
        {
            return false;
        }

        var receiveCode = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (receiveCode == 0)
        {
            // I.1: If the frame is in a hardware pixel format, transfer to CPU memory.
            AVFrame* sourceFrame = _frame;
            if (_usingHardwareDecode && _frame->format == (int)_hwPixelFormat && _swFrame is not null)
            {
                ffmpeg.av_frame_unref(_swFrame);
                if (ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0) < 0)
                {
                    // GPU→CPU transfer failed — disable HW decode for subsequent frames.
                    _usingHardwareDecode = false;
                    ffmpeg.av_frame_unref(_frame);
                    return false;
                }

                // Copy metadata (pts, flags, etc.) that transfer_data does not propagate.
                _swFrame->pts = _frame->pts;
                _swFrame->pkt_dts = _frame->pkt_dts;
                _swFrame->flags = _frame->flags;
                sourceFrame = _swFrame;
            }

            width = sourceFrame->width;
            height = sourceFrame->height;
            isKeyFrame = (sourceFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;
            pixelFormat = sourceFrame->format;
            plane0Stride = Math.Max(1, Math.Abs(sourceFrame->linesize[0]));
            var copySize = Math.Max(1, plane0Stride * Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 0)));
            var copied = new byte[copySize];
            if (sourceFrame->data[0] is not null)
            {
                Marshal.Copy((IntPtr)sourceFrame->data[0], copied, 0, copySize);
            }

            plane0 = copied;

            if (sourceFrame->data[1] is not null)
            {
                plane1Stride = Math.Max(1, Math.Abs(sourceFrame->linesize[1]));
                var plane1Height = Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 1));
                var plane1Bytes = new byte[Math.Max(1, plane1Stride * plane1Height)];
                Marshal.Copy((IntPtr)sourceFrame->data[1], plane1Bytes, 0, plane1Bytes.Length);
                plane1 = plane1Bytes;
            }

            if (sourceFrame->data[2] is not null)
            {
                plane2Stride = Math.Max(1, Math.Abs(sourceFrame->linesize[2]));
                var plane2Height = Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 2));
                var plane2Bytes = new byte[Math.Max(1, plane2Stride * plane2Height)];
                Marshal.Copy((IntPtr)sourceFrame->data[2], plane2Bytes, 0, plane2Bytes.Length);
                plane2 = plane2Bytes;
            }

            ffmpeg.av_frame_unref(_frame);
            if (sourceFrame == _swFrame)
                ffmpeg.av_frame_unref(_swFrame);

            return true;
        }

        if (receiveCode == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            needMoreData = true;
            return false;
        }

        return false;
    }

    /// <summary>Flushes stale codec state after a seek. Must be called while the pipeline gate is held.</summary>
    public void FlushCodecBuffers()
    {
        if (_codecContext is not null)
        {
            ffmpeg.avcodec_flush_buffers(_codecContext);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CleanupHardwareDecode();
        DisposeCodecContext();
    }

    private void CleanupHardwareDecode()
    {
        if (_codecContext is not null)
        {
            HwFormatMap.TryRemove((nint)_codecContext, out _);
        }

        if (_swFrame is not null)
        {
            var swFrame = _swFrame;
            ffmpeg.av_frame_free(&swFrame);
            _swFrame = null;
        }

        if (_hwDeviceCtx is not null)
        {
            var hwCtx = _hwDeviceCtx;
            ffmpeg.av_buffer_unref(&hwCtx);
            _hwDeviceCtx = null;
        }

        _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _usingHardwareDecode = false;
    }

    private void DisposeCodecContext()
    {
        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        // N5: free the pre-allocated packet alongside the codec context.
        if (_packet is not null)
        {
            var pkt = _packet;
            ffmpeg.av_packet_free(&pkt);
            _packet = null;
        }

        if (_codecContext is not null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = null;
        }

        _codecId = null;
    }

    private static int GetPlaneHeight(AVPixelFormat format, int height, int planeIndex)
    {
        var safeHeight = Math.Max(1, height);
        if (planeIndex == 0)
        {
            return safeHeight;
        }

        return format switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P => safeHeight / 2,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => safeHeight / 2,
            AVPixelFormat.AV_PIX_FMT_NV12 => safeHeight / 2,
            AVPixelFormat.AV_PIX_FMT_P010LE => safeHeight / 2,
            _ => safeHeight,
        };
    }
}

internal readonly record struct FFVideoDecodeResult(
    long Generation,
    long FrameIndex,
    TimeSpan PresentationTime,
    bool IsKeyFrame,
    int Width,
    int Height,
    ReadOnlyMemory<byte> Plane0 = default,
    int Plane0Stride = 0,
    ReadOnlyMemory<byte> Plane1 = default,
    int Plane1Stride = 0,
    ReadOnlyMemory<byte> Plane2 = default,
    int Plane2Stride = 0,
    int? NativeTimeBaseNumerator = null,
    int? NativeTimeBaseDenominator = null,
    int? NativeFrameRateNumerator = null,
    int? NativeFrameRateDenominator = null,
    int? NativePixelFormat = null);
