namespace S.Media.NDI;

/// <summary>
/// Lightweight shared A/V timing state for NDI sinks.
/// Video PTS observations seed and update the timeline; audio reserves timecode ranges
/// in sample-accurate steps so both streams share one media-time domain.
/// </summary>
public sealed class NDIAvTimingContext
{
    private long _latestVideoPtsTicks = long.MinValue;
    private long _nextAudioTimecodeTicks = long.MinValue;

    public void ObserveVideoPts(long ptsTicks)
    {
        long pts = ptsTicks < 0 ? 0 : ptsTicks;
        Volatile.Write(ref _latestVideoPtsTicks, pts);

        // Seed audio timeline from video the first time we observe a usable video PTS.
        // If audio has already started from a 0 seed (because it flowed before the first
        // video frame), re-origin it here so the two streams stay aligned — NDI receivers
        // tolerate a one-shot timecode discontinuity before audible media.
        long prev = Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, pts, long.MinValue);
        if (prev != long.MinValue && prev < pts)
        {
            // CAS-loop to advance the audio cursor to the video PTS.  Only bump forward;
            // never move backward (that would repeat timecodes and confuse receivers).
            while (true)
            {
                long current = Interlocked.Read(ref _nextAudioTimecodeTicks);
                if (current >= pts) break;
                if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, pts, current) == current) break;
            }
        }
    }

    public long ReserveAudioTimecode(int writtenFrames, int sampleRate)
    {
        if (writtenFrames <= 0 || sampleRate <= 0)
            return 0;

        long stepTicks = (long)Math.Round((double)writtenFrames * TimeSpan.TicksPerSecond / sampleRate);

        while (true)
        {
            long current = Interlocked.Read(ref _nextAudioTimecodeTicks);
            if (current == long.MinValue)
            {
                long seed = Volatile.Read(ref _latestVideoPtsTicks);
                if (seed == long.MinValue)
                    seed = 0;

                if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, seed, long.MinValue) != long.MinValue)
                    continue;

                current = seed;
            }

            long next = current + stepTicks;
            if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, next, current) == current)
                return current;
        }
    }
}

