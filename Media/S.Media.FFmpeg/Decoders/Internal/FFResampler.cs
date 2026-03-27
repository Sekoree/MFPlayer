using FFmpeg.AutoGen;
using S.Media.Core.Errors;

namespace S.Media.FFmpeg.Decoders.Internal;

internal sealed class FFResampler : IDisposable
{
    private bool _disposed;
    private bool _initialized;
    private bool _nativeResampleEnabled = true;
    private FFNativeResamplerBackend? _nativeBackend;

    internal bool IsNativeResampleEnabled => _nativeResampleEnabled;

    public int Initialize()
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegResamplerInitFailed;
        }

        _nativeResampleEnabled = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
        _initialized = true;
        return MediaResult.Success;
    }

    public int Resample() => _disposed || !_initialized ? (int)MediaErrorCode.FFmpegResampleFailed : MediaResult.Success;

    public int Resample(FFAudioDecodeResult decoded, out FFAudioResampleResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegResampleFailed;
        }

        if (_nativeResampleEnabled && TryNativeResample(decoded, out var nativeResult))
        {
            result = nativeResult;
            return MediaResult.Success;
        }

        return (int)MediaErrorCode.FFmpegResampleFailed;
    }

    public void Dispose()
    {
        _disposed = true;
        _nativeBackend?.Dispose();
        _nativeBackend = null;
    }

    private bool TryNativeResample(FFAudioDecodeResult decoded, out FFAudioResampleResult result)
    {
        result = default;

        if (decoded.NativeSampleFormat is null || decoded.NativeSampleRate is null || decoded.NativeChannelCount is null)
        {
            return false;
        }

        try
        {
            if (decoded.NativeSampleRate.Value <= 0 || decoded.NativeChannelCount.Value <= 0)
            {
                _nativeResampleEnabled = false;
                return false;
            }

            _nativeBackend ??= new FFNativeResamplerBackend();
            if (!_nativeBackend.TryEnsureInitialized(
                    decoded.NativeSampleFormat.Value,
                    decoded.NativeSampleRate.Value,
                    decoded.NativeChannelCount.Value))
            {
                _nativeResampleEnabled = false;
                return false;
            }

            var resolvedSamples = ResolveSamples(decoded);
            var resolvedChannelCount = Math.Max(1, decoded.NativeChannelCount.GetValueOrDefault(1));
            var payloadFrameCount = Math.Max(1, resolvedSamples.Length / resolvedChannelCount);
            var shapedFrameCount = _nativeBackend.TryGetOutSamples(decoded.FrameCount, out var outSamples)
                ? Math.Max(1, Math.Min(outSamples, payloadFrameCount))
                : payloadFrameCount;

            result = new FFAudioResampleResult(
                decoded.Generation,
                decoded.PresentationTime,
                shapedFrameCount,
                decoded.SampleValue,
                resolvedSamples,
                decoded.NativeTimeBaseNumerator,
                decoded.NativeTimeBaseDenominator,
                decoded.NativeSampleRate,
                decoded.NativeChannelCount,
                decoded.NativeSampleFormat);
            return true;
        }
        catch (DllNotFoundException)
        {
            _nativeResampleEnabled = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _nativeResampleEnabled = false;
            return false;
        }
        catch (TypeInitializationException)
        {
            _nativeResampleEnabled = false;
            return false;
        }
        catch (NotSupportedException)
        {
            _nativeResampleEnabled = false;
            return false;
        }
    }

    private static ReadOnlyMemory<float> ResolveSamples(FFAudioDecodeResult decoded)
    {
        if (!decoded.Samples.IsEmpty)
        {
            return decoded.Samples;
        }

        var channelCount = Math.Max(1, decoded.NativeChannelCount.GetValueOrDefault(1));
        var sampleCount = Math.Max(1, decoded.FrameCount) * channelCount;
        var generated = new float[sampleCount];
        generated.AsSpan().Fill(decoded.SampleValue);
        return generated;
    }
}

internal unsafe sealed class FFNativeResamplerBackend : IDisposable
{
    private SwrContext* _context;
    private int _inputSampleFormat;
    private int _inputSampleRate;
    private int _inputChannelCount;
    private bool _disposed;

    public bool TryEnsureInitialized(int nativeSampleFormat, int nativeSampleRate, int nativeChannelCount)
    {
        if (_disposed)
        {
            return false;
        }

        if (_context is not null &&
            _inputSampleFormat == nativeSampleFormat &&
            _inputSampleRate == nativeSampleRate &&
            _inputChannelCount == nativeChannelCount)
        {
            return true;
        }

        DisposeContext();

        var inFormat = (AVSampleFormat)nativeSampleFormat;
        var outFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;

        AVChannelLayout inLayout = default;
        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&inLayout, nativeChannelCount);
        ffmpeg.av_channel_layout_default(&outLayout, nativeChannelCount);

        SwrContext* context = null;
        var allocCode = ffmpeg.swr_alloc_set_opts2(
            &context,
            &outLayout,
            outFormat,
            nativeSampleRate,
            &inLayout,
            inFormat,
            nativeSampleRate,
            0,
            null);

        ffmpeg.av_channel_layout_uninit(&inLayout);
        ffmpeg.av_channel_layout_uninit(&outLayout);

        if (allocCode < 0 || context is null)
        {
            return false;
        }

        _context = context;

        if (ffmpeg.swr_init(_context) < 0)
        {
            DisposeContext();
            return false;
        }

        _inputSampleFormat = nativeSampleFormat;
        _inputSampleRate = nativeSampleRate;
        _inputChannelCount = nativeChannelCount;
        return true;
    }

    public bool TryGetOutSamples(int inSamples, out int outSamples)
    {
        outSamples = 0;

        if (_disposed || _context is null || inSamples <= 0)
        {
            return false;
        }

        var value = ffmpeg.swr_get_out_samples(_context, inSamples);
        if (value <= 0)
        {
            return false;
        }

        outSamples = value;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeContext();
    }

    private void DisposeContext()
    {
        if (_context is null)
        {
            return;
        }

        var context = _context;
        ffmpeg.swr_free(&context);
        _context = null;
    }
}

internal readonly struct FFAudioResampleResult
{
    public FFAudioResampleResult(
        long generation,
        TimeSpan presentationTime,
        int frameCount,
        float sampleValue,
        ReadOnlyMemory<float> samples = default,
        int? nativeTimeBaseNumerator = null,
        int? nativeTimeBaseDenominator = null,
        int? nativeSampleRate = null,
        int? nativeChannelCount = null,
        int? nativeSampleFormat = null)
    {
        Generation = generation;
        PresentationTime = presentationTime;
        FrameCount = frameCount;
        SampleValue = sampleValue;
        Samples = samples;
        NativeTimeBaseNumerator = nativeTimeBaseNumerator;
        NativeTimeBaseDenominator = nativeTimeBaseDenominator;
        NativeSampleRate = nativeSampleRate;
        NativeChannelCount = nativeChannelCount;
        NativeSampleFormat = nativeSampleFormat;
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
