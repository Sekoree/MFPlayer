namespace S.Media.Core.Audio;

/// <summary>Shared math for reporting an <em>audible</em> playback position from a consumed one.</summary>
public static class AudioLatencyCompensation
{
    /// <summary>
    /// Maps the consumed/elapsed time of a playback segment to the reported audible time. Steady state
    /// is <c>elapsed − latency</c>. During the startup window (<c>elapsed &lt; 2×latency</c>, while the
    /// buffers between the producer and the speaker are still filling) it reports the quadratic ease-in
    /// <c>elapsed²∕(4×latency)</c>: starts at 0, strictly monotonic in <paramref name="elapsedSeconds"/>,
    /// meets <c>elapsed − latency</c> at the window edge with matching value <em>and</em> slope (both are
    /// <c>latency</c> resp. 1 at <c>elapsed = 2×latency</c>), and always lies between the true audible
    /// position and the consumed position. Replaces a clamp at zero, which reports 0 for the whole first
    /// <c>latency</c> and then jumps.
    /// </summary>
    public static double AudibleSeconds(double elapsedSeconds, double latencySeconds)
    {
        if (elapsedSeconds <= 0)
            return 0;
        if (latencySeconds <= 0)
            return elapsedSeconds;
        if (elapsedSeconds < 2 * latencySeconds)
            return elapsedSeconds * elapsedSeconds / (4 * latencySeconds);
        return elapsedSeconds - latencySeconds;
    }
}
