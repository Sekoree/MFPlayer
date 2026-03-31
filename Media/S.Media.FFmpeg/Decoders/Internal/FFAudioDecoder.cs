using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFAudioDecoder : IDisposable
{

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

    public int Decode(FFPacket packet, out FFAudioDecodeResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegAudioDecodeFailed;
        }

        var hasNativePacket = packet.NativeCodecId is not null && packet.NativePacketData is { Length: > 0 };
        if (_nativeDecodeEnabled && hasNativePacket)
        {
            if (TryDecodeNative(packet, out var nativeResult, out var needMoreInput))
            {
                result = nativeResult;
                return MediaResult.Success;
            }

            if (needMoreInput)
            {
                // Native decoder consumed packet but has no frame yet; caller should feed more packets.
                result = new FFAudioDecodeResult(
                    packet.Generation,
                    packet.PresentationTime,
                    FrameCount: 0,
                    packet.SampleValue,
                    Samples: ReadOnlyMemory<float>.Empty,
                    NativeTimeBaseNumerator: packet.NativeTimeBaseNumerator,
                    NativeTimeBaseDenominator: packet.NativeTimeBaseDenominator);
                return MediaResult.Success;
            }
        }

        return (int)MediaErrorCode.FFmpegAudioDecodeFailed;
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

    private bool TryDecodeNative(FFPacket packet, out FFAudioDecodeResult result, out bool needMoreInput)
    {
        result = default;
        needMoreInput = false;

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
                    out var nativeSampleFormat,
                    out var nativeSamples,
                    out needMoreInput))
            {
                if (!needMoreInput)
                {
                    _nativeDecodeEnabled = false;
                }

                return false;
            }

            result = new FFAudioDecodeResult(
                packet.Generation,
                packet.PresentationTime,
                Math.Max(1, nativeFrameCount),
                packet.SampleValue,
                nativeSamples,
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
        out int frameCount,
        out int sampleRate,
        out int channelCount,
        out int sampleFormat,
        out ReadOnlyMemory<float> samples,
        out bool needMoreInput)
    {
        frameCount = 0;
        sampleRate = 0;
        channelCount = 0;
        sampleFormat = 0;
        samples = default;
        needMoreInput = false;

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
            frameCount = _frame->nb_samples;
            sampleRate = _frame->sample_rate > 0
                ? _frame->sample_rate
                : _codecContext->sample_rate;
            channelCount = _frame->ch_layout.nb_channels > 0
                ? _frame->ch_layout.nb_channels
                : _codecContext->ch_layout.nb_channels;
            if (channelCount <= 0)
            {
                channelCount = 2;
            }

            sampleFormat = _frame->format;
            samples = ExtractSamples(_frame, frameCount, channelCount, sampleFormat);
            ffmpeg.av_frame_unref(_frame);
            return true;
        }

        if (receiveCode == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            needMoreInput = true;
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

    private static ReadOnlyMemory<float> ExtractSamples(AVFrame* frame, int frameCount, int channelCount, int sampleFormat)
    {
        var sampleCount = Math.Max(1, frameCount) * Math.Max(1, channelCount);
        var output = new float[sampleCount];
        var format = (AVSampleFormat)sampleFormat;

        if (frame->extended_data is null)
        {
            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_FLT)
        {
            var ptr = (IntPtr)frame->extended_data[0];
            Marshal.Copy(ptr, output, 0, Math.Min(sampleCount, output.Length));
            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var plane = new float[Math.Max(1, frameCount)];
                Marshal.Copy((IntPtr)frame->extended_data[ch], plane, 0, Math.Max(1, frameCount));
                for (var i = 0; i < frameCount; i++)
                {
                    output[(i * channelCount) + ch] = plane[i];
                }
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_S16)
        {
            var tmp = new short[sampleCount];
            Marshal.Copy((IntPtr)frame->extended_data[0], tmp, 0, tmp.Length);
            for (var i = 0; i < tmp.Length; i++)
            {
                output[i] = tmp[i] / 32768f;
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_S16P)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var plane = new short[Math.Max(1, frameCount)];
                Marshal.Copy((IntPtr)frame->extended_data[ch], plane, 0, Math.Max(1, frameCount));
                for (var i = 0; i < frameCount; i++)
                {
                    output[(i * channelCount) + ch] = plane[i] / 32768f;
                }
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_S32)
        {
            var tmp = new int[sampleCount];
            Marshal.Copy((IntPtr)frame->extended_data[0], tmp, 0, tmp.Length);
            for (var i = 0; i < tmp.Length; i++)
            {
                output[i] = tmp[i] / 2147483648f;
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_S32P)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var plane = new int[Math.Max(1, frameCount)];
                Marshal.Copy((IntPtr)frame->extended_data[ch], plane, 0, Math.Max(1, frameCount));
                for (var i = 0; i < frameCount; i++)
                {
                    output[(i * channelCount) + ch] = plane[i] / 2147483648f;
                }
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_DBL)
        {
            var tmp = new double[sampleCount];
            Marshal.Copy((IntPtr)frame->extended_data[0], tmp, 0, tmp.Length);
            for (var i = 0; i < tmp.Length; i++)
            {
                output[i] = (float)tmp[i];
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_DBLP)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var plane = new double[Math.Max(1, frameCount)];
                Marshal.Copy((IntPtr)frame->extended_data[ch], plane, 0, Math.Max(1, frameCount));
                for (var i = 0; i < frameCount; i++)
                {
                    output[(i * channelCount) + ch] = (float)plane[i];
                }
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_U8)
        {
            var tmp = new byte[sampleCount];
            Marshal.Copy((IntPtr)frame->extended_data[0], tmp, 0, tmp.Length);
            for (var i = 0; i < tmp.Length; i++)
            {
                output[i] = (tmp[i] - 128) / 128f;
            }

            return output;
        }

        if (format == AVSampleFormat.AV_SAMPLE_FMT_U8P)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                var plane = new byte[Math.Max(1, frameCount)];
                Marshal.Copy((IntPtr)frame->extended_data[ch], plane, 0, Math.Max(1, frameCount));
                for (var i = 0; i < frameCount; i++)
                {
                    output[(i * channelCount) + ch] = (plane[i] - 128) / 128f;
                }
            }

            return output;
        }

        return output;
    }
}

internal readonly struct FFAudioDecodeResult
{
    public FFAudioDecodeResult(
        long Generation,
        TimeSpan PresentationTime,
        int FrameCount,
        float SampleValue,
        ReadOnlyMemory<float> Samples = default,
        int? NativeTimeBaseNumerator = null,
        int? NativeTimeBaseDenominator = null,
        int? NativeSampleRate = null,
        int? NativeChannelCount = null,
        int? NativeSampleFormat = null)
    {
        this.Generation = Generation;
        this.PresentationTime = PresentationTime;
        this.FrameCount = FrameCount;
        this.SampleValue = SampleValue;
        this.Samples = Samples;
        this.NativeTimeBaseNumerator = NativeTimeBaseNumerator;
        this.NativeTimeBaseDenominator = NativeTimeBaseDenominator;
        this.NativeSampleRate = NativeSampleRate;
        this.NativeChannelCount = NativeChannelCount;
        this.NativeSampleFormat = NativeSampleFormat;
    }

    public long Generation { get; }

    public TimeSpan PresentationTime { get; }

    public int FrameCount { get; }

    public float SampleValue { get; }

    public ReadOnlyMemory<float> Samples { get; }

    public int? NativeTimeBaseNumerator { get; }

    public int? NativeTimeBaseDenominator { get; }

    public int? NativeSampleRate { get; }

    public int? NativeChannelCount { get; }

    public int? NativeSampleFormat { get; }
}
