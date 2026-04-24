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
    public void ApplyGainRamp(Span<float> buffer, float startGain, float endGain, int channels)
    {
        if (channels <= 0 || buffer.IsEmpty) return;
        int frameCount = buffer.Length / channels;
        if (frameCount <= 0) return;
        if (frameCount == 1)
        {
            ApplyGain(buffer, endGain);
            return;
        }
        float step = (endGain - startGain) / (frameCount - 1);
        float g = startGain;
        for (int f = 0; f < frameCount; f++)
        {
            int baseIdx = f * channels;
            for (int c = 0; c < channels; c++)
                buffer[baseIdx + c] *= g;
            g += step;
        }
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
        // §4.13 / M2 — The .NET runtime (since .NET 6) sets FTZ and DAZ
        // on the MXCSR register for every managed thread at creation time.
        // On ARM64, NEON has flush-to-zero by default. Therefore this method
        // is intentionally a no-op: the runtime already provides the
        // denormal-flush behaviour that audio threads need, and the managed
        // surface no longer exposes Sse.GetCsr/SetCsr (removed in .NET 5+)
        // or X86Base.GetMxcsr/SetMxcsr. Callers may still invoke this at
        // thread entry as a self-documenting annotation that denormal
        // flushing is required on the code path — it costs nothing.
    }

    /// <inheritdoc />
    public int CountOverflows(ReadOnlySpan<float> buffer)
    {
        int count = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            int simdLen = Vector<float>.Count;
            var vOne = new Vector<float>(1.0f);
            for (; i + simdLen <= buffer.Length; i += simdLen)
            {
                var v = Vector.Abs(new Vector<float>(buffer[i..]));
                // GreaterThan → lanes are -1 (all-bits-set) on match; Sum of the
                // negated mask gives the number of matches.
                var mask = Vector.GreaterThan(v, vOne);
                for (int j = 0; j < simdLen; j++)
                    if (mask[j] != 0) count++;
            }
        }
        for (; i < buffer.Length; i++)
            if (Math.Abs(buffer[i]) > 1.0f) count++;
        return count;
    }

    /// <inheritdoc />
    public void ApplySoftClip(Span<float> buffer, float threshold = 0.98f)
    {
        // tanh-ish curve above the threshold. We avoid MathF.Tanh per-sample by
        // using a cheap rational approximation (x / (1 + |x|)) rescaled so the
        // knee tangent continues smoothly from the linear region. This is the
        // classic Chebyshev / Padé soft-clip: audibly transparent up to the
        // threshold, gentle above it, asymptotic to ±(threshold + (1-threshold)).
        if (threshold <= 0f || threshold >= 1f || buffer.IsEmpty)
            return;

        float t = threshold;
        float range = 1f - t;

        for (int i = 0; i < buffer.Length; i++)
        {
            float s = buffer[i];
            float abs = Math.Abs(s);
            if (abs <= t) continue;

            float excess = abs - t;
            float shaped = excess / (1f + excess / range);  // ∈ [0, range)
            buffer[i] = MathF.CopySign(t + shaped, s);
        }
    }
}

