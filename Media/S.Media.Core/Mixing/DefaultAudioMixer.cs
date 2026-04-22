using System.Numerics;

namespace S.Media.Core.Mixing;

/// <summary>
/// Default <see cref="IAudioMixer"/> implementation. Uses <see cref="Vector{T}"/>
/// (portable SIMD) for the inner loops and falls back to scalar tails. Bit-exact
/// behavioural copy of the private static helpers that used to live in
/// <c>AVRouter</c> prior to review item §4.12 / M1.
/// </summary>
public sealed class DefaultAudioMixer : IAudioMixer
{
    /// <summary>Shared stateless instance — safe to reuse across threads.</summary>
    public static readonly DefaultAudioMixer Instance = new();

    /// <inheritdoc />
    public void MixInto(Span<float> dest, ReadOnlySpan<float> src)
    {
        int len = Math.Min(dest.Length, src.Length);
        int i = 0;
        if (Vector.IsHardwareAccelerated && len >= Vector<float>.Count)
        {
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= len; i += simdLen)
            {
                var d = new Vector<float>(dest[i..]);
                var s = new Vector<float>(src[i..]);
                (d + s).CopyTo(dest[i..]);
            }
        }
        for (; i < len; i++)
            dest[i] += src[i];
    }

    /// <inheritdoc />
    public void ApplyGain(Span<float> buffer, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= buffer.Length; i += simdLen)
            {
                var v = new Vector<float>(buffer[i..]);
                (v * vGain).CopyTo(buffer[i..]);
            }
        }
        for (; i < buffer.Length; i++)
            buffer[i] *= gain;
    }

    /// <inheritdoc />
    public void ApplyChannelMap(
        ReadOnlySpan<float> src, Span<float> dest,
        (int dstCh, float gain)[][] bakedRoutes,
        int srcChannels, int dstChannels, int frameCount)
    {
        for (int f = 0; f < frameCount; f++)
        {
            int srcBase = f * srcChannels;
            int dstBase = f * dstChannels;

            for (int srcCh = 0; srcCh < bakedRoutes.Length; srcCh++)
            {
                float sample = src[srcBase + srcCh];
                var targets = bakedRoutes[srcCh];
                for (int t = 0; t < targets.Length; t++)
                {
                    var (dstCh, gain) = targets[t];
                    if (dstCh < dstChannels)
                        dest[dstBase + dstCh] += sample * gain;
                }
            }
        }
    }

    /// <inheritdoc />
    public float MeasurePeak(ReadOnlySpan<float> buffer)
    {
        float peak = 0f;
        int i = 0;
        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vMax = Vector<float>.Zero;
            int simdLen = Vector<float>.Count;
            for (; i + simdLen <= buffer.Length; i += simdLen)
            {
                var v = Vector.Abs(new Vector<float>(buffer[i..]));
                vMax = Vector.Max(vMax, v);
            }
            for (int j = 0; j < Vector<float>.Count; j++)
                peak = Math.Max(peak, vMax[j]);
        }
        for (; i < buffer.Length; i++)
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        return peak;
    }

    /// <inheritdoc />
    public void FlushDenormalsToZero()
    {
        // .NET's public surface no longer exposes the MXCSR FTZ/DAZ bits directly
        // (the legacy `Sse.SetCsr`/`GetCsr` helpers were removed). Leaving this as
        // a documented no-op until we wire a P/Invoke-based alternative under
        // review item §4.13 / M2. The JIT already uses SSE scalars that honour
        // FTZ when the runtime sets it globally, so this method's absence is not
        // a correctness issue — only a missed perf opportunity on denormal-heavy
        // workloads.
    }
}

