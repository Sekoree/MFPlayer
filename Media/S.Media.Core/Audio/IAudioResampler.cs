using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Performs sample-rate conversion on interleaved <see cref="float"/> PCM data.
/// Channel count is preserved; only the sample rate changes.
/// Implementations are stateful (carry fractional phase across calls for seamless output).
/// </summary>
public interface IAudioResampler : IDisposable
{
    /// <summary>
    /// Converts <paramref name="input"/> from <c>inputFormat.SampleRate</c> to
    /// <paramref name="outputSampleRate"/>, writing results to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">
    /// Interleaved source samples. Length must be
    /// <c>inputFrames × inputFormat.Channels</c>.
    /// </param>
    /// <param name="output">
    /// Buffer for the resampled result. Length must be
    /// <c>outputFrames × inputFormat.Channels</c>.
    /// The caller decides <c>outputFrames</c>; see remarks.
    /// </param>
    /// <param name="inputFormat">Format of the source data (rate + channels used; SampleType must be Float32).</param>
    /// <param name="outputSampleRate">Target sample rate.</param>
    /// <returns>Number of output frames written.</returns>
    /// <remarks>
    /// To avoid artefacts at buffer boundaries the caller should supply
    /// <c>ceil(outputFrames × inputRate / outputRate) + 1</c> input frames.
    /// The resampler carries the fractional phase internally.
    /// </remarks>
    int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate);

    /// <summary>Resets the internal phase accumulator (call on seek).</summary>
    void Reset();
}

