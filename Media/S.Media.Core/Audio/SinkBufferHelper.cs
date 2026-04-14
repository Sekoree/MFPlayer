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
        int nominal = sourceSampleRate == targetSampleRate
            ? sourceFrameCount
            : (int)Math.Round((double)sourceFrameCount * targetSampleRate / sourceSampleRate);

        return driftCorrector != null
            ? driftCorrector.CorrectFrameCount(nominal, currentQueueDepth)
            : nominal;
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

