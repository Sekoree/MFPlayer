using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFVideoDecoder : IDisposable
{

    private bool _disposed;
    private bool _initialized;
    private bool _nativeDecodeEnabled = true;
    private FFNativeVideoDecoderBackend? _nativeBackend;

    internal bool IsNativeDecodeEnabled => _nativeDecodeEnabled;

    public int Initialize()
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegDecoderInitFailed;
        }

        _nativeBackend?.Dispose();
        _nativeBackend = null;
        _nativeDecodeEnabled = true;
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
            if (!_nativeBackend.TryEnsureInitialized(packet.NativeCodecId.Value, packet.NativeCodecParametersPtr))
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

                _nativeDecodeEnabled = false;
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

    public bool TryEnsureInitialized(int codecId, nint? codecParametersPtr)
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

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext is null)
        {
            return false;
        }

        if (codecParametersPtr is not null && codecParametersPtr.Value != 0)
        {
            var applyCode = ffmpeg.avcodec_parameters_to_context(_codecContext, (AVCodecParameters*)codecParametersPtr.Value);
            if (applyCode < 0)
            {
                DisposeCodecContext();
                return false;
            }
        }

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

        // N5: allocate the reusable packet once after the codec context is fully initialised.
        _packet = ffmpeg.av_packet_alloc();
        if (_packet is null)
        {
            DisposeCodecContext();
            return false;
        }

        _codecId = requestedCodecId;
        return true;
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
            width = _frame->width;
            height = _frame->height;
            isKeyFrame = (_frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;
            pixelFormat = _frame->format;
            plane0Stride = Math.Max(1, Math.Abs(_frame->linesize[0]));
            var copySize = Math.Max(1, plane0Stride * Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 0)));
            var copied = new byte[copySize];
            if (_frame->data[0] is not null)
            {
                Marshal.Copy((IntPtr)_frame->data[0], copied, 0, copySize);
            }

            plane0 = copied;

            if (_frame->data[1] is not null)
            {
                plane1Stride = Math.Max(1, Math.Abs(_frame->linesize[1]));
                var plane1Height = Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 1));
                var plane1Bytes = new byte[Math.Max(1, plane1Stride * plane1Height)];
                Marshal.Copy((IntPtr)_frame->data[1], plane1Bytes, 0, plane1Bytes.Length);
                plane1 = plane1Bytes;
            }

            if (_frame->data[2] is not null)
            {
                plane2Stride = Math.Max(1, Math.Abs(_frame->linesize[2]));
                var plane2Height = Math.Max(1, GetPlaneHeight((AVPixelFormat)pixelFormat, height, 2));
                var plane2Bytes = new byte[Math.Max(1, plane2Stride * plane2Height)];
                Marshal.Copy((IntPtr)_frame->data[2], plane2Bytes, 0, plane2Bytes.Length);
                plane2 = plane2Bytes;
            }

            ffmpeg.av_frame_unref(_frame);
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
        DisposeCodecContext();
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
