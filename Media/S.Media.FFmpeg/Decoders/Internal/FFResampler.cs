using S.Media.Core.Errors;

namespace S.Media.FFmpeg.Decoders.Internal;

/// <summary>
/// Pipeline stage that wraps decoded audio samples for hand-off to the audio source.
/// <para>
/// <b>Current limitation (N4):</b> Sample-rate conversion is not implemented. Samples are
/// passed through unchanged at their native rate. Format conversion (S16/S32/DBL → FLT)
/// is already handled upstream in <c>FFNativeAudioDecoderBackend</c>; no further conversion
/// is needed here. If you need to resample to a different output rate, a <c>SwrContext</c>-based
/// path should be implemented in this class.
/// </para>
/// </summary>
internal sealed class FFResampler : IDisposable
{
    private bool _disposed;
    private bool _initialized;

    public int Initialize()
    {
        if (_disposed)
        {
            return (int)MediaErrorCode.FFmpegResamplerInitFailed;
        }

        _initialized = true;
        return MediaResult.Success;
    }

    // N3: removed no-arg Resample() overload — it was a no-op.

    public int Resample(FFAudioDecodeResult decoded, out FFAudioResampleResult result)
    {
        result = default;

        if (_disposed || !_initialized)
        {
            return (int)MediaErrorCode.FFmpegResampleFailed;
        }

        if (decoded.NativeSampleFormat is null ||
            decoded.NativeSampleRate is null ||
            decoded.NativeChannelCount is null ||
            decoded.NativeSampleRate.Value <= 0 ||
            decoded.NativeChannelCount.Value <= 0)
        {
            return (int)MediaErrorCode.FFmpegResampleFailed;
        }

        // N4: no swr_convert — samples are already in FLT format from ExtractSamples upstream.
        // Pass through unchanged; frame count is derived from the actual sample buffer length.
        var resolvedSamples = ResolveSamples(decoded);
        var resolvedChannelCount = Math.Max(1, decoded.NativeChannelCount.GetValueOrDefault(1));
        var frameCount = Math.Max(1, resolvedSamples.Length / resolvedChannelCount);

        result = new FFAudioResampleResult(
            decoded.Generation,
            decoded.PresentationTime,
            frameCount,
            decoded.SampleValue,
            resolvedSamples,
            decoded.NativeTimeBaseNumerator,
            decoded.NativeTimeBaseDenominator,
            decoded.NativeSampleRate,
            decoded.NativeChannelCount,
            decoded.NativeSampleFormat);
        return MediaResult.Success;
    }

    public void Dispose()
    {
        _disposed = true;
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

// N4: FFNativeResamplerBackend removed — it initialised a SwrContext but never called swr_convert,
// so the context consumed resources while providing no actual resampling. The backend is replaced
// with the direct pass-through above. Re-add if/when sample-rate conversion is implemented.

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
