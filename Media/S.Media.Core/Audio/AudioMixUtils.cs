using System.Numerics;
using System.Runtime.CompilerServices;

namespace S.Media.Core.Audio;

/// <summary>
/// SIMD-accelerated helpers for the audio mix loop.
/// All methods are allocation-free hot-path utilities.
/// </summary>
internal static class AudioMixUtils
{
    /// <summary>
    /// Accumulates <paramref name="src"/> into <paramref name="dst"/> with an optional per-source gain.
    /// Uses <see cref="Vector{T}"/> SIMD when hardware-accelerated; falls back to scalar otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MixInto(float[] dst, float[] src, int count, float gain = 1.0f)
    {
        int i = 0;

        if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
        {
            int simd = Vector<float>.Count;
            int end  = count - (count % simd);

            if (Math.Abs(gain - 1.0f) < 0.001f)
            {
                // Unity gain — skip multiply
                for (; i < end; i += simd)
                    (new Vector<float>(dst, i) + new Vector<float>(src, i)).CopyTo(dst, i);
            }
            else
            {
                var gVec = new Vector<float>(gain);
                for (; i < end; i += simd)
                    (new Vector<float>(dst, i) + new Vector<float>(src, i) * gVec).CopyTo(dst, i);
            }
        }

        if (Math.Abs(gain - 1.0f) < 0.001f)
            for (; i < count; i++) dst[i] += src[i];
        else
            for (; i < count; i++) dst[i] += src[i] * gain;
    }

    /// <summary>
    /// Clamps all values in <paramref name="buf"/> to [-1.0, 1.0].
    /// Uses SIMD when hardware-accelerated.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clamp(float[] buf, int count)
    {
        int i = 0;

        if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
        {
            int simd = Vector<float>.Count;
            int end  = count - (count % simd);
            var mn   = new Vector<float>(-1f);
            var mx   = new Vector<float>( 1f);

            for (; i < end; i += simd)
                Vector.Clamp(new Vector<float>(buf, i), mn, mx).CopyTo(buf, i);
        }

        for (; i < count; i++) buf[i] = Math.Clamp(buf[i], -1f, 1f);
    }

    /// <summary>
    /// Mixes a single interleaved source channel into a single interleaved destination channel
    /// across <paramref name="frameCount"/> frames, with an optional per-route gain.
    /// Used by the audio routing path to implement per-channel <see cref="S.Media.Core.Mixing.AudioRoutingRule"/> routing.
    /// </summary>
    /// <param name="dst">Destination interleaved buffer.</param>
    /// <param name="dstChannel">Zero-based index of the destination channel within <paramref name="dst"/>.</param>
    /// <param name="dstChannels">Total channel count of <paramref name="dst"/>.</param>
    /// <param name="src">Source interleaved buffer.</param>
    /// <param name="srcChannel">Zero-based index of the source channel within <paramref name="src"/>.</param>
    /// <param name="srcChannels">Total channel count of <paramref name="src"/>.</param>
    /// <param name="frameCount">Number of frames to process.</param>
    /// <param name="gain">Per-route gain multiplier (default 1.0 = unity).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MixChannel(
        float[] dst, int dstChannel, int dstChannels,
        float[] src, int srcChannel, int srcChannels,
        int frameCount, float gain = 1.0f)
    {
        // Channels are interleaved with arbitrary stride — contiguous SIMD is not applicable here.
        // Scalar loop is correct and efficient for typical channel counts (1–8).
        if (Math.Abs(gain - 1.0f) < 0.001f)
        {
            for (int f = 0; f < frameCount; f++)
                dst[f * dstChannels + dstChannel] += src[f * srcChannels + srcChannel];
        }
        else
        {
            for (int f = 0; f < frameCount; f++)
                dst[f * dstChannels + dstChannel] += src[f * srcChannels + srcChannel] * gain;
        }
    }

    /// <summary>
    /// Applies a master-volume multiplier to <paramref name="buf"/>.
    /// Skips the multiply at unity (|vol - 1.0| &lt; 0.001).
    /// Uses SIMD when hardware-accelerated.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyVolume(float[] buf, int count, float volume)
    {
        if (Math.Abs(volume - 1.0f) < 0.001f) return;

        int i = 0;

        if (Vector.IsHardwareAccelerated && count >= Vector<float>.Count)
        {
            int simd = Vector<float>.Count;
            int end  = count - (count % simd);
            var vVec = new Vector<float>(volume);

            for (; i < end; i += simd)
                (new Vector<float>(buf, i) * vVec).CopyTo(buf, i);
        }

        for (; i < count; i++) buf[i] *= volume;
    }
}

