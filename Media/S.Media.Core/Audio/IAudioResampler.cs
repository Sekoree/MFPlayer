namespace S.Media.Core.Audio;

/// <summary>
/// Abstraction over sample-rate and channel-count conversion.
/// <para>
/// Two implementations exist:
/// <list type="bullet">
///   <item><see cref="AudioResampler"/> — pure-C# windowed-sinc fallback (no external dependencies).</item>
///   <item><c>FFAudioResampler</c> in <c>S.Media.FFmpeg</c> — backed by libswresample for higher quality.</item>
/// </list>
/// </para>
/// </summary>
public interface IAudioResampler : IDisposable
{
    /// <summary>Source sample rate this instance was created for.</summary>
    int SourceSampleRate { get; }

    /// <summary>Target sample rate this instance was created for.</summary>
    int TargetSampleRate { get; }

    /// <summary>Source channel count this instance was created for.</summary>
    int SourceChannelCount { get; }

    /// <summary>Target channel count this instance was created for.</summary>
    int TargetChannelCount { get; }

    /// <summary>
    /// Upper-bound estimate of output frames for a given input frame count.
    /// Callers should size the destination buffer to at least this many frames * TargetChannelCount.
    /// </summary>
    int EstimateOutputFrameCount(int inputFrameCount);

    /// <summary>
    /// Resamples interleaved float32 samples from source rate/channels to target rate/channels.
    /// Maintains fractional-position state across calls for gapless streaming.
    /// </summary>
    /// <param name="source">Interleaved float32 source samples.</param>
    /// <param name="inputFrameCount">Number of frames in <paramref name="source"/>.</param>
    /// <param name="destination">Output buffer (must be at least <c>EstimateOutputFrameCount * TargetChannelCount</c> in length).</param>
    /// <returns>Number of output frames written, or -1 on a hard error (e.g. channel mismatch with <c>Fail</c> policy).</returns>
    int Resample(ReadOnlySpan<float> source, int inputFrameCount, Span<float> destination);

    /// <summary>
    /// Resets fractional-position and ring-buffer state. Call after a seek.
    /// </summary>
    void Reset();
}

