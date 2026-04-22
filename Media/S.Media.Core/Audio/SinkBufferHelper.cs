using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Shared helpers for <see cref="IAudioSink"/> implementations that perform
/// drift-corrected, rate-aware buffer writes.
/// All methods are allocation-free and safe for the RT thread.
/// </summary>
public static class SinkBufferHelper
{
    /// <summary>
    /// Computes the drift-corrected output frame count for a sink buffer.
    /// Handles the common pattern: nominal rate-ratio adjustment → optional
    /// <see cref="DriftCorrector"/> correction.
    /// </summary>
    /// <param name="sourceFrameCount">Number of source audio frames.</param>
    /// <param name="sourceSampleRate">Sample rate of the incoming buffer.</param>
    /// <param name="targetSampleRate">Sample rate of the sink's output.</param>
    /// <param name="driftCorrector">Optional drift corrector (may be <see langword="null"/>).</param>
    /// <param name="currentQueueDepth">Current pending-write queue depth (used by drift corrector).</param>
    /// <returns>The corrected output frame count.</returns>
    public static int ComputeWriteFrames(
        int sourceFrameCount,
        int sourceSampleRate,
        int targetSampleRate,
        DriftCorrector? driftCorrector,
        int currentQueueDepth)
    {
        int nominal = ComputeNominalFrames(sourceFrameCount, sourceSampleRate, targetSampleRate);
        return driftCorrector != null
            ? driftCorrector.CorrectFrameCount(nominal, currentQueueDepth)
            : nominal;
    }

    /// <summary>
    /// Computes the rate-ratio-adjusted nominal output frame count (no drift correction).
    /// <para>
    /// Uses nearest-integer rounding so the long-term average output rate matches
    /// <paramref name="targetSampleRate"/> as closely as possible.
    /// </para>
    /// <para>
    /// Cross-buffer interpolation continuity is enforced inside
    /// <see cref="LinearResampler"/> (it preserves at least one pending source
    /// frame on cross-rate paths), so we can keep unbiased rounding here without
    /// reintroducing the old boundary-click distortion.
    /// </para>
    /// </summary>
    public static int ComputeNominalFrames(int sourceFrameCount, int sourceSampleRate, int targetSampleRate)
        => sourceSampleRate == targetSampleRate
            ? sourceFrameCount
            : (int)Math.Round((double)sourceFrameCount * targetSampleRate / sourceSampleRate);

    /// <summary>
    /// Cross-rate resample + drift correction.  Calls the resampler with the
    /// <em>nominal</em> (non-drift-inflated) output size so its internal phase
    /// advances by exactly one nominal buffer, then applies drift correction by
    /// last-frame-hold padding (when drift wants more frames than nominal) or
    /// truncation (when drift wants fewer).
    /// <para>
    /// Critically, when <paramref name="driftCorrectedFrames"/> equals the nominal
    /// count (i.e. drift correction is disabled or at unity) the resampler's
    /// natural output is forwarded <em>unchanged</em>.  Stateful resamplers like
    /// <see cref="LinearResampler"/> legitimately produce <c>nominal ± 1</c> frames
    /// on any given call due to fractional-phase accumulation; the long-term
    /// average is exactly nominal, so padding/truncating on every short call
    /// would duplicate then discard real audio data every other buffer — which
    /// accumulates into steady audible distortion.
    /// </para>
    /// <para>
    /// The other reason to call the resampler with nominal (not
    /// drift-inflated): stateful resamplers advance their phase by
    /// <c>step × requested-output-frames</c>, so a drift-inflated request
    /// over-advances the phase and desynchronises cross-buffer state.
    /// </para>
    /// </summary>
    /// <returns>Total output samples written to <paramref name="destination"/>.</returns>
    public static int ResampleWithDrift(
        IAudioResampler     resampler,
        ReadOnlySpan<float> source,
        Span<float>         destination,
        AudioFormat         sourceFormat,
        int                 targetSampleRate,
        int                 targetChannels,
        int                 driftCorrectedFrames)
    {
        int sourceFrames = sourceFormat.Channels > 0
            ? source.Length / sourceFormat.Channels
            : 0;
        int nominalFrames = ComputeNominalFrames(sourceFrames, sourceFormat.SampleRate, targetSampleRate);
        int destCapacityFrames = targetChannels > 0 ? destination.Length / targetChannels : 0;

        // Clamp nominal to destination capacity (defensive — callers size dest
        // for the drift-corrected count, which is ≥ nominal when drift wants
        // more and == nominal in the common no-drift case).
        int nominalInDest = Math.Min(nominalFrames, destCapacityFrames);
        int nominalSamples = nominalInDest * targetChannels;

        int producedFrames = nominalSamples > 0
            ? resampler.Resample(source, destination[..nominalSamples], sourceFormat, targetSampleRate)
            : 0;
        if (producedFrames < 0) producedFrames = 0;
        if (producedFrames > nominalInDest) producedFrames = nominalInDest;

        // Drift correction decision tree — keyed on drift-vs-nominal, NOT on
        // produced-vs-drift, so natural resampler short-counts don't get padded.
        if (driftCorrectedFrames > nominalFrames && producedFrames > 0)
        {
            // Drift actively wants MORE frames than the resampler would produce
            // at unity: pad with last-frame hold.
            int target = Math.Min(driftCorrectedFrames, destCapacityFrames);
            var lastFrame = destination.Slice((producedFrames - 1) * targetChannels, targetChannels);
            for (int f = producedFrames; f < target; f++)
                lastFrame.CopyTo(destination.Slice(f * targetChannels, targetChannels));
            return target * targetChannels;
        }

        if (driftCorrectedFrames < nominalFrames)
        {
            // Drift actively wants FEWER frames: truncate the resampler's output.
            int finalFrames = Math.Min(driftCorrectedFrames, producedFrames);
            return finalFrames * targetChannels;
        }

        // Drift at unity (or no corrector): forward the resampler's natural
        // fractional-phase output unchanged.
        return producedFrames * targetChannels;
    }

    /// <summary>
    /// Copies audio samples for same-rate output, adjusting for a drift-corrected
    /// frame count.  When <paramref name="writeFrames"/> exceeds
    /// <paramref name="sourceFrameCount"/>, the last source frame is held (repeated)
    /// to fill the extra frames.  When fewer frames are needed only the required
    /// frames are copied.
    /// </summary>
    /// <param name="source">Source interleaved audio buffer.</param>
    /// <param name="destination">
    /// Destination buffer — must have at least
    /// <paramref name="writeFrames"/> × <paramref name="channels"/> elements.
    /// </param>
    /// <param name="sourceFrameCount">Number of frames in the source.</param>
    /// <param name="writeFrames">
    /// Target output frame count (may differ by ±1 from source due to drift correction).
    /// </param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="clearTail">
    /// When <see langword="true"/> and the copy covers fewer samples than the full
    /// write region, the remainder is zero-filled. Default <see langword="false"/>.
    /// </param>
    public static void CopySameRate(
        ReadOnlySpan<float> source,
        Span<float> destination,
        int sourceFrameCount,
        int writeFrames,
        int channels,
        bool clearTail = false)
    {
        int copyFrames  = Math.Min(sourceFrameCount, writeFrames);
        int copySamples = copyFrames * channels;
        int writeSamples = writeFrames * channels;

        source[..copySamples].CopyTo(destination[..copySamples]);

        if (writeFrames > sourceFrameCount && sourceFrameCount > 0)
        {
            // Hold last frame for drift-correction extra frames.
            var lastFrame = source.Slice((sourceFrameCount - 1) * channels, channels);
            for (int f = copyFrames; f < writeFrames; f++)
                lastFrame.CopyTo(destination.Slice(f * channels, channels));
        }
        else if (clearTail && copySamples < writeSamples)
        {
            destination.Slice(copySamples, writeSamples - copySamples).Clear();
        }
    }
}
