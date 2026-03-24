using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFAudioDecoder : IDisposable
{
    private const int PlaceholderAudioFramesPerChunk = 256;

    private bool _disposed;
    private bool _initialized;
    private bool _nativeDecodeEnabled = true;
    private FFNativeAudioDecoderBackend? _nativeBackend;

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

    public int Decode() => _disposed || !_initialized ? (int)MediaErrorCode.FFmpegAudioDecodeFailed : MediaResult.Success;

    public int Decode(FFPacket packet, out FFAudioDecodeResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegAudioDecodeFailed;
        }

        if (_nativeDecodeEnabled && TryDecodeNative(packet, out var nativeResult))
        {
            result = nativeResult;
            return MediaResult.Success;
        }

        result = new FFAudioDecodeResult(packet.Generation, packet.PresentationTime, PlaceholderAudioFramesPerChunk, packet.SampleValue);
        return MediaResult.Success;
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
    }

    private bool TryDecodeNative(FFPacket packet, out FFAudioDecodeResult result)
    {
        result = default;

        if (packet.NativeCodecId is null || packet.NativePacketData is null || packet.NativePacketData.Length == 0)
        {
            return false;
        }

        try
        {
            _nativeBackend ??= new FFNativeAudioDecoderBackend();
            if (!_nativeBackend.TryEnsureInitialized(packet.NativeCodecId.Value, packet.NativeCodecParametersPtr))
            {
                _nativeDecodeEnabled = false;
                return false;
            }

            if (!_nativeBackend.TryDecode(
                    packet.NativePacketData,
                    packet.NativePacketFlags,
                    out var nativeFrameCount,
                    out var nativeSampleRate,
                    out var nativeChannelCount,
                    out var nativeSampleFormat))
            {
                _nativeDecodeEnabled = false;
                return false;
            }

            result = new FFAudioDecodeResult(
                packet.Generation,
                packet.PresentationTime,
                Math.Max(1, nativeFrameCount),
                packet.SampleValue,
                NativeTimeBaseNumerator: packet.NativeTimeBaseNumerator,
                NativeTimeBaseDenominator: packet.NativeTimeBaseDenominator,
                NativeSampleRate: nativeSampleRate,
                NativeChannelCount: nativeChannelCount,
                NativeSampleFormat: nativeSampleFormat);
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

internal unsafe sealed class FFNativeAudioDecoderBackend : IDisposable
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
        out int frameCount,
        out int sampleRate,
        out int channelCount,
        out int sampleFormat)
    {
        frameCount = 0;
        sampleRate = 0;
        channelCount = 0;
        sampleFormat = 0;

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
                frameCount = _frame->nb_samples;
                sampleRate = _frame->sample_rate;
                channelCount = _frame->ch_layout.nb_channels;
                sampleFormat = _frame->format;
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

internal readonly record struct FFAudioDecodeResult(
    long Generation,
    TimeSpan PresentationTime,
    int FrameCount,
    float SampleValue,
    int? NativeTimeBaseNumerator = null,
    int? NativeTimeBaseDenominator = null,
    int? NativeSampleRate = null,
    int? NativeChannelCount = null,
    int? NativeSampleFormat = null);

