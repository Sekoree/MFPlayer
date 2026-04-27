namespace S.Media.NDI;

/// <summary>
/// Lightweight shared A/V timing state for NDI sinks.
/// <para/>
/// Audio timecodes are driven by delivered-sample-count at the target rate
/// (<see cref="ReserveAudioTimecode"/>): in steady state the router pushes samples at
/// exactly wall-clock rate, so the cursor tracks wall-clock too — and because video
/// timecodes come from the video decoder's PTS (which also advances at wall-clock
/// under the router's tick pacing), the two streams stay in the same media-time
/// domain on the wire.
    /// <para/>
    /// When the media-time gap between the latest observed video PTS and the audio
    /// sample cursor would exceed the threshold, we <b>pull the cursor up gradually</b>
    /// (capped per buffer).  Jumping in one go to the latest video PTS was wrong for
    /// NDIClock: router/decoder can publish several video frames in a wall-time burst, so
    /// <c>LatestVideoPts</c> is sometimes several frame-times ahead; a full snap made the
    /// next read look ~165ms behind again, repeating visible lead/lag.  Only
    /// &gt;=<see cref="CatastrophicFullSnapAheadMs"/> (true stall) still does a one-shot jump.
/// <para/>
/// For producers that carry explicit stream-PTS and want that stamped directly,
/// <see cref="AdvanceAudioCursorTo"/> provides a PTS-anchored alternative.
/// </summary>
public sealed class NDIAvTimingContext
{
    /// <summary>
    /// Default threshold beyond which a lagging audio cursor is snapped to the latest
    /// video PTS (underrun recovery).  120 ms — wide enough to avoid the steady ~80ms
    /// NDIClock/router limit-cycle (false “recoveries” every ~2s).  Override via
    /// <see cref="SetUnderrunRecoveryThresholdMs"/> or <c>NDIAVSinkOptions</c>.
    /// </summary>
    public static readonly long UnderrunRecoveryThresholdTicks = TimeSpan.FromMilliseconds(120).Ticks;

    private long _latestVideoPtsTicks = long.MinValue;
    private long _nextAudioTimecodeTicks = long.MinValue;
    private long _underrunRecoveries;
    private long _thresholdOverrideTicks; // 0 = use static default
    /// <summary>From the last <see cref="ReserveAudioTimecode"/> full catastrophic snap: how far ahead video was (media ticks).</summary>
    private long _lastUnderrunVideoAheadTicks;

    /// <summary>Latest observed video PTS in ticks, or <see cref="long.MinValue"/> if none.</summary>
    public long LatestVideoPtsTicks => Volatile.Read(ref _latestVideoPtsTicks);

    /// <summary>Next audio-cursor timecode in ticks, or <see cref="long.MinValue"/> if unseeded.</summary>
    public long NextAudioTimecodeTicks => Interlocked.Read(ref _nextAudioTimecodeTicks);

    /// <summary>Number of full catastrophic alignments (one-shot jump to latest video PTS) since reset.</summary>
    public long UnderrunRecoveries => Interlocked.Read(ref _underrunRecoveries);

    /// <summary>Gap (media ticks) before the last full catastrophic snap, or 0 if none since reset.</summary>
    public long LastUnderrunVideoAheadTicks => Volatile.Read(ref _lastUnderrunVideoAheadTicks);

    /// <summary>Maximum media-time the cursor may advance toward video PTS in one <see cref="ReserveAudioTimecode"/> when above threshold (millisecond cap).</summary>
    public static int MaxUnderrunPullPerCallMs { get; set; } = 40;

    /// <summary>Video lead beyond this (ms) triggers a one-shot jump to the latest video PTS (true underrun). Default 500; set 0 to never full-snap (creep only).</summary>
    public static int CatastrophicFullSnapAheadMs { get; set; } = 500;

    /// <summary>Keep nudging the audio cursor until the video lead is at most this many milliseconds (per-buffer creep only).</summary>
    public static int AlignUntilBelowMs { get; set; } = 25;

    /// <summary>
    /// The currently-effective recovery threshold in ticks (either the per-instance
    /// override from <see cref="SetUnderrunRecoveryThresholdMs"/> or the static default).
    /// </summary>
    public long EffectiveUnderrunRecoveryThresholdTicks
    {
        get
        {
            long t = Interlocked.Read(ref _thresholdOverrideTicks);
            return t > 0 ? t : UnderrunRecoveryThresholdTicks;
        }
    }

    /// <summary>
    /// Override the underrun recovery threshold for this instance.  Values &lt;= 0 revert
    /// to the static default (<see cref="UnderrunRecoveryThresholdTicks"/>).  Call before
    /// playback starts or at any time — subsequent reservations pick up the new threshold.
    /// </summary>
    public void SetUnderrunRecoveryThresholdMs(int thresholdMs)
    {
        long ticks = thresholdMs > 0 ? TimeSpan.FromMilliseconds(thresholdMs).Ticks : 0;
        Interlocked.Exchange(ref _thresholdOverrideTicks, ticks);
    }

    /// <summary>
    /// Record the latest video PTS.  Used both to seed the audio cursor on first use
    /// and as the "floor" the audio cursor is snapped up to on underrun recovery.
    /// Negative values are ignored.
    /// </summary>
    public void ObserveVideoPts(long ptsTicks)
    {
        if (ptsTicks < 0) return;
        Volatile.Write(ref _latestVideoPtsTicks, ptsTicks);
    }

    /// <summary>
    /// Anchor the cursor to a producer-supplied PTS and advance it by the delivered
    /// duration.  Monotonic: the cursor is never moved backward (repeating timecodes
    /// would confuse NDI receivers).  Returns the effective timecode for this buffer
    /// (always <paramref name="ptsTicks"/> when non-negative).
    /// </summary>
    public long AdvanceAudioCursorTo(long ptsTicks, int writtenFrames, int sampleRate)
    {
        if (ptsTicks < 0 || writtenFrames <= 0 || sampleRate <= 0)
            return ptsTicks < 0 ? 0 : ptsTicks;

        long stepTicks = (long)Math.Round((double)writtenFrames * TimeSpan.TicksPerSecond / sampleRate);
        long desiredNext = ptsTicks + stepTicks;

        while (true)
        {
            long current = Interlocked.Read(ref _nextAudioTimecodeTicks);
            // Never move the cursor backwards.
            long next = current == long.MinValue || desiredNext > current ? desiredNext : current;
            if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, next, current) == current)
                return ptsTicks;
        }
    }

    /// <summary>
    /// Reset both cursors to the unseeded state.  Call on sink restart / seek so the
    /// next session reseeds from scratch instead of inheriting a stale cursor.
    /// </summary>
    public void Reset()
    {
        Volatile.Write(ref _latestVideoPtsTicks, long.MinValue);
        Interlocked.Exchange(ref _nextAudioTimecodeTicks, long.MinValue);
        Interlocked.Exchange(ref _underrunRecoveries, 0);
        Volatile.Write(ref _lastUnderrunVideoAheadTicks, 0);
    }

    /// <summary>
    /// Reserve a sample-accurate timecode range starting at the current cursor and
    /// advance the cursor by <paramref name="writtenFrames"/> / <paramref name="sampleRate"/>
    /// ticks.  Seeded from the latest observed video PTS (or 0) on first use.
    /// <para/>
    /// <b>Alignment:</b> if the media gap exceeds <see cref="AlignUntilBelowMs"/>, the cursor moves
    /// up by at most <see cref="MaxUnderrunPullPerCallMs"/> per call, except when
    /// lead is catastrophic — then a one-shot jump to the latest video PTS (see
    /// <see cref="CatastrophicFullSnapAheadMs"/>).  Gradual pull avoids the NDIClock + burst
    /// frame pattern that made “full to LatestVideo” oscillate and left video looking far ahead of audio.
    /// </summary>
    public long ReserveAudioTimecode(int writtenFrames, int sampleRate)
    {
        if (writtenFrames <= 0 || sampleRate <= 0)
            return 0;

        long stepTicks = (long)Math.Round((double)writtenFrames * TimeSpan.TicksPerSecond / sampleRate);
        long videoPts = Volatile.Read(ref _latestVideoPtsTicks);
        long alignStopTicks = TimeSpan.FromMilliseconds(System.Math.Max(1, AlignUntilBelowMs)).Ticks;
        long pullCapTicks = TimeSpan.FromMilliseconds(System.Math.Max(1, MaxUnderrunPullPerCallMs)).Ticks;
        long catSnapTicks = CatastrophicFullSnapAheadMs > 0
            ? TimeSpan.FromMilliseconds(CatastrophicFullSnapAheadMs).Ticks
            : long.MaxValue; // 0: never one-shot to raw video; creep only

        while (true)
        {
            long current = Interlocked.Read(ref _nextAudioTimecodeTicks);
            long effective = current;

            if (effective == long.MinValue)
            {
                // Seed from latest video PTS (or 0).
                effective = videoPts == long.MinValue ? 0 : videoPts;
            }
            else if (videoPts != long.MinValue && videoPts - effective > alignStopTicks)
            {
                long ahead = videoPts - effective;
                bool fullSnap = CatastrophicFullSnapAheadMs > 0 && ahead >= catSnapTicks;
                if (fullSnap)
                {
                    Volatile.Write(ref _lastUnderrunVideoAheadTicks, ahead);
                    effective = videoPts;
                    Interlocked.Increment(ref _underrunRecoveries);
                }
                else
                {
                    // Incremental re-align: never jump to LatestVideo in one buffer when
                    // the excess is a burst of frames (e.g. ~5× frame at ~30 fps = ~165ms).
                    long pull = System.Math.Min(ahead, pullCapTicks);
                    if (pull > 0)
                        effective = current + pull;
                }
            }

            long next = effective + stepTicks;
            if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, next, current) == current)
                return effective;
        }
    }

    /// <summary>
    /// Stamp this buffer from the router/decoder’s stream read-head PTS (100 ns ticks).
    /// Uses the same media-time origin as the video path’s frame PTS, so NDI timecodes
    /// reflect the file’s A/V interleave instead of converging to a video-led floor from
    /// <see cref="ReserveAudioTimecode"/>'s creep (which is capped by
    /// <see cref="AlignUntilBelowMs"/> and could leave tens of ms of false “video ahead”
    /// when the router master suppresses auto A/V drift correction and no decoder-side
    /// nudge runs).
    /// <para/>
    /// Monotonic: if the decoder’s head lags the cursor (resampler phase / duplicate
    /// delivery), the start is at least the previous buffer end so NDI timecodes do not
    /// go backward.
    /// </summary>
    public long StampFromDecoderStreamHead(long streamHeadPts, int writtenFrames, int sampleRate)
    {
        if (writtenFrames <= 0 || sampleRate <= 0)
            return 0;
        if (streamHeadPts < 0)
            return ReserveAudioTimecode(writtenFrames, sampleRate);

        long stepTicks = (long)Math.Round((double)writtenFrames * TimeSpan.TicksPerSecond / sampleRate);

        while (true)
        {
            long expectNextStart = Interlocked.Read(ref _nextAudioTimecodeTicks);
            // First buffer: trust streamHeadPts from the router (read-head before FillBuffer) so
            // we do not reintroduce a fixed offset vs video when the decoder starts audio at 0
            // while the first video frame is later in media time.
            long start = streamHeadPts;
            if (expectNextStart != long.MinValue && start < expectNextStart)
                start = expectNextStart;

            long end = start + stepTicks;
            if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, end, expectNextStart) == expectNextStart)
                return start;
        }
    }
}

