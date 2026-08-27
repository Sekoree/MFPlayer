namespace S.Media.NDI.Audio;

/// <summary>
/// The NDI audio jitter-buffer policy numbers and math, shared by <see cref="NDISource"/>'s combined
/// receive path and <see cref="NDIAudioJitterBuffer"/>. (Formerly statics on the standalone
/// <c>NDIAudioReceiver</c>, which was an unused parallel receive path - deleted; <see cref="NDISource"/>
/// is the one receiver.)
/// </summary>
public static class NDIAudioBuffering
{
    /// <summary>Default jitter-buffer prime threshold. Covers the worst-case inter-NDI-frame gap
    /// (33 ms at 30p) plus a margin so router pulls can ride over burst timing.</summary>
    public static readonly TimeSpan DefaultMinBufferedDuration = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Computes the jitter-buffer holdback in frames-per-channel for a given sample rate and ring
    /// capacity. Capped at half the ring capacity so the holdback can never starve the consumer of
    /// the entire ring; clamped to zero for non-positive durations so callers can opt out.
    /// </summary>
    internal static int ComputeMinBufferedFrames(TimeSpan minBufferedDuration, int sampleRate, int capacityFrames)
    {
        if (minBufferedDuration <= TimeSpan.Zero || sampleRate <= 0 || capacityFrames <= 0)
            return 0;
        var requested = (int)(minBufferedDuration.TotalSeconds * sampleRate);
        if (requested <= 0) return 0;
        var cap = Math.Max(0, capacityFrames / 2);
        return Math.Min(cap, requested);
    }

    /// <summary>
    /// Jitter-buffer read policy. The holdback is a startup/recovery threshold, not a permanent floor:
    /// once primed, the consumer may read from the buffered reserve so NDI's video-frame-sized audio
    /// bursts can be smoothed into smaller router chunks.
    /// </summary>
    internal static int ComputeReadCount(int requestedFloats, int availableFloats, int minBufferedFloats, ref bool primed)
    {
        if (requestedFloats <= 0 || availableFloats <= 0)
            return 0;

        if (minBufferedFloats <= 0)
        {
            primed = true;
            return Math.Min(requestedFloats, availableFloats);
        }

        if (!primed)
        {
            if (availableFloats < minBufferedFloats)
                return 0;
            primed = true;
        }

        var toRead = Math.Min(requestedFloats, availableFloats);
        if (toRead < requestedFloats)
            primed = false;
        return toRead;
    }
}
