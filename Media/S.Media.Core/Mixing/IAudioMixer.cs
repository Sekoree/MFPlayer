namespace S.Media.Core.Mixing;

/// <summary>
/// Pure-function audio mixing primitives used by <c>AVRouter</c> to combine per-route
/// scratch buffers into a per-endpoint destination. Extracting these from the router
/// (review item §4.12 / M1) enables:
/// <list type="bullet">
///   <item>Unit-testing the maths without spinning up a router + endpoints.</item>
///   <item>Swapping in backend-specific implementations (SIMD variants, soft-clip,
///         denormal-flushed) without touching routing logic.</item>
///   <item>Benchmarking under <c>BenchmarkDotNet</c>.</item>
/// </list>
/// All methods operate on interleaved Float32 PCM.
/// </summary>
public interface IAudioMixer
{
    /// <summary>
    /// Accumulates <paramref name="src"/> into <paramref name="dest"/> sample-by-sample
    /// (<c>dest[i] += src[i]</c>). When the spans differ in length, only the common
    /// prefix is processed.
    /// </summary>
    void MixInto(Span<float> dest, ReadOnlySpan<float> src);

    /// <summary>Scales every sample in <paramref name="buffer"/> by <paramref name="gain"/> in place.</summary>
    void ApplyGain(Span<float> buffer, float gain);

    /// <summary>
    /// Scatters interleaved source samples into interleaved destination samples using
    /// a pre-baked channel route table.
    /// </summary>
    /// <param name="src">Interleaved source samples (<paramref name="srcChannels"/> × <paramref name="frameCount"/>).</param>
    /// <param name="dest">Interleaved destination samples (<paramref name="dstChannels"/> × <paramref name="frameCount"/>).</param>
    /// <param name="bakedRoutes">
    /// Jagged array indexed by source channel; each inner array contains
    /// <c>(destination channel, gain)</c> pairs. Produced by <c>ChannelRouteMap.Bake</c>.
    /// </param>
    /// <param name="srcChannels">Channel count of the source interleaving.</param>
    /// <param name="dstChannels">Channel count of the destination interleaving.</param>
    /// <param name="frameCount">Number of sample frames to process.</param>
    void ApplyChannelMap(
        ReadOnlySpan<float> src, Span<float> dest,
        (int dstCh, float gain)[][] bakedRoutes,
        int srcChannels, int dstChannels, int frameCount);

    /// <summary>
    /// Returns the maximum absolute sample value in <paramref name="buffer"/>.
    /// Returns <c>0</c> for an empty buffer.
    /// </summary>
    float MeasurePeak(ReadOnlySpan<float> buffer);

    /// <summary>
    /// Enables flush-denormals-to-zero on the current thread on platforms that
    /// support it (currently SSE). Call once from the audio-producing thread to
    /// avoid denormal-number slowdowns in tight SIMD loops. No-op on platforms
    /// without hardware support. Review item M2 / §4.13.
    /// </summary>
    void FlushDenormalsToZero();
}

