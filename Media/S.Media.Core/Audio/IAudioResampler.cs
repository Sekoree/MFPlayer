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
    /// The resampler carries the fractional phase internally; the caller should use
    /// <see cref="GetRequiredInputFrames"/> to determine the correct input size rather
    /// than a fixed formula, so that internally buffered pending frames are accounted for.
    /// </remarks>
    int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate);

    /// <summary>
    /// Returns the number of <b>new</b> input frames the next <see cref="Resample"/> call
    /// needs to produce <paramref name="outputFrames"/> output frames, accounting for any
    /// internally buffered pending frames from the previous call.
    /// <para>
    /// Using this instead of a fixed <c>ceil(…) + 1</c> formula prevents the internal
    /// pending-frame buffer from growing unboundedly (which would cause per-callback
    /// heap allocations on the RT thread and trigger GC pauses → audio crackling).
    /// </para>
    /// </summary>
    int GetRequiredInputFrames(int outputFrames, AudioFormat inputFormat, int outputSampleRate);

    /// <summary>Resets the internal phase accumulator (call on seek).</summary>
    void Reset();
}

