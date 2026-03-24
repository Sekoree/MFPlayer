using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFVideoDecoder : IDisposable
{
    private const int PlaceholderVideoWidth = 2;
    private const int PlaceholderVideoHeight = 2;

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

    public int Decode() => _disposed || !_initialized ? (int)MediaErrorCode.FFmpegVideoDecodeFailed : MediaResult.Success;

    public int Decode(FFPacket packet, out FFVideoDecodeResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegVideoDecodeFailed;
        }

        if (_nativeDecodeEnabled && TryDecodeNative(packet, out var nativeResult))
        {
            result = nativeResult;
            return MediaResult.Success;
        }

        result = new FFVideoDecodeResult(
            packet.Generation,
            packet.Sequence,
            packet.PresentationTime,
            packet.IsKeyFrame,
            PlaceholderVideoWidth,
            PlaceholderVideoHeight);
        return MediaResult.Success;
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
    }

    private bool TryDecodeNative(FFPacket packet, out FFVideoDecodeResult result)
    {
        result = default;

        if (packet.NativeCodecId is null || packet.NativePacketData is null || packet.NativePacketData.Length == 0)
        {
            return false;
        }

        try
        {
            _nativeBackend ??= new FFNativeVideoDecoderBackend();
            if (!_nativeBackend.TryEnsureInitialized(packet.NativeCodecId.Value, packet.NativeCodecParametersPtr))
            {
                _nativeDecodeEnabled = false;
                return false;
            }

            if (!_nativeBackend.TryDecode(
                    packet.NativePacketData,
                    packet.NativePacketFlags,
                    out var width,
                    out var height,
                    out var keyFrameFromDecoder,
                    out var nativePixelFormat))
            {
                _nativeDecodeEnabled = false;
                return false;
            }

            result = new FFVideoDecodeResult(
                packet.Generation,
                packet.Sequence,
                packet.PresentationTime,
                keyFrameFromDecoder || packet.IsKeyFrame,
                Math.Max(1, width),
                Math.Max(1, height),
                NativeTimeBaseNumerator: packet.NativeTimeBaseNumerator,
                NativeTimeBaseDenominator: packet.NativeTimeBaseDenominator,
                NativeFrameRateNumerator: packet.NativeFrameRateNumerator,
                NativeFrameRateDenominator: packet.NativeFrameRateDenominator,
                NativePixelFormat: nativePixelFormat);
            return true;
        }
        catch (DllNotFoundException)
        {
            _nativeDecodeEnabled = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _nativeDecodeEnabled = false;
            return false;
        }
        catch (TypeInitializationException)
        {
            _nativeDecodeEnabled = false;
            return false;
        }
        catch (NotSupportedException)
        {
            _nativeDecodeEnabled = false;
            return false;
        }
    }
}

internal unsafe sealed class FFNativeVideoDecoderBackend : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
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

        _codecId = requestedCodecId;
        return true;
    }

    public bool TryDecode(
        byte[] packetData,
        int packetFlags,
        out int width,
        out int height,
        out bool isKeyFrame,
        out int pixelFormat)
    {
        width = 0;
        height = 0;
        isKeyFrame = false;
        pixelFormat = 0;

        if (_disposed || _codecContext is null || _frame is null)
        {
            return false;
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet is null)
        {
            return false;
        }

        try
        {
            if (ffmpeg.av_new_packet(packet, packetData.Length) < 0)
            {
                return false;
            }

            Marshal.Copy(packetData, 0, (IntPtr)packet->data, packetData.Length);
            packet->flags = packetFlags;

            if (ffmpeg.avcodec_send_packet(_codecContext, packet) < 0)
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
                ffmpeg.av_frame_unref(_frame);
                return true;
            }

            return receiveCode == ffmpeg.AVERROR(ffmpeg.EAGAIN);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
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

        if (_codecContext is not null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = null;
        }

        _codecId = null;
    }
}

internal readonly record struct FFVideoDecodeResult(
    long Generation,
    long FrameIndex,
    TimeSpan PresentationTime,
    bool IsKeyFrame,
    int Width,
    int Height,
    int? NativeTimeBaseNumerator = null,
    int? NativeTimeBaseDenominator = null,
    int? NativeFrameRateNumerator = null,
    int? NativeFrameRateDenominator = null,
    int? NativePixelFormat = null);

