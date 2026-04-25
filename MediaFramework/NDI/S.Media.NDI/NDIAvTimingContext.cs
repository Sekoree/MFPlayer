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
/// On audio-decoder starvation (CPU spike / GC pause) the router briefly pushes
/// fewer / no buffers, so the sample-counted cursor stops advancing.  If that lag
/// exceeds <see cref="UnderrunRecoveryThresholdTicks"/> relative to the latest
/// observed video PTS, the next reservation <b>snaps the cursor forward</b> to the
/// video PTS.  This re-anchors audio to video on the wire so the receiver doesn't
/// see a permanent A/V offset after a transient glitch — the missed audio window
/// manifests as a short silence on the receiver rather than a growing skew.
/// <para/>
/// For producers that carry explicit stream-PTS and want that stamped directly,
/// <see cref="AdvanceAudioCursorTo"/> provides a PTS-anchored alternative.
/// </summary>
public sealed class NDIAvTimingContext
{
    /// <summary>
    /// Default threshold beyond which a lagging audio cursor is snapped to the latest
    /// video PTS (underrun recovery).  80 ms — tight enough that post-stall residual
    /// lag gets actively corrected (not parked just below the threshold forever), wide
    /// enough to ride out routine audio-pipeline jitter (~20-35 ms steady-state in the
    /// reference configuration).  Can be overridden per-sink via the options record.
    /// </summary>
    public static readonly long UnderrunRecoveryThresholdTicks = TimeSpan.FromMilliseconds(80).Ticks;

    private long _latestVideoPtsTicks = long.MinValue;
    private long _nextAudioTimecodeTicks = long.MinValue;
    private long _underrunRecoveries;
    private long _thresholdOverrideTicks; // 0 = use static default

    /// <summary>Latest observed video PTS in ticks, or <see cref="long.MinValue"/> if none.</summary>
    public long LatestVideoPtsTicks => Volatile.Read(ref _latestVideoPtsTicks);

    /// <summary>Next audio-cursor timecode in ticks, or <see cref="long.MinValue"/> if unseeded.</summary>
    public long NextAudioTimecodeTicks => Interlocked.Read(ref _nextAudioTimecodeTicks);

    /// <summary>Number of underrun recoveries (cursor snapped forward to video PTS) since reset.</summary>
    public long UnderrunRecoveries => Interlocked.Read(ref _underrunRecoveries);

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
    }

    /// <summary>
    /// Reserve a sample-accurate timecode range starting at the current cursor and
    /// advance the cursor by <paramref name="writtenFrames"/> / <paramref name="sampleRate"/>
    /// ticks.  Seeded from the latest observed video PTS (or 0) on first use.
    /// <para/>
    /// <b>Underrun recovery:</b> if the cursor has lagged the latest video PTS by more
    /// than <see cref="UnderrunRecoveryThresholdTicks"/> (typically because the audio
    /// decoder stalled), the cursor snaps up to the video PTS before reservation.  The
    /// returned timecode reflects the snapped value so the NDI wire stream stays
    /// aligned with video even after an audio-decoder hiccup.
    /// </summary>
    public long ReserveAudioTimecode(int writtenFrames, int sampleRate)
    {
        if (writtenFrames <= 0 || sampleRate <= 0)
            return 0;

        long stepTicks = (long)Math.Round((double)writtenFrames * TimeSpan.TicksPerSecond / sampleRate);
        long videoPts = Volatile.Read(ref _latestVideoPtsTicks);
        long threshold = EffectiveUnderrunRecoveryThresholdTicks;

        while (true)
        {
            long current = Interlocked.Read(ref _nextAudioTimecodeTicks);
            long effective = current;

            if (effective == long.MinValue)
            {
                // Seed from latest video PTS (or 0).
                effective = videoPts == long.MinValue ? 0 : videoPts;
            }
            else if (videoPts != long.MinValue && videoPts - effective > threshold)
            {
                // Cursor has fallen too far behind the video timeline — almost certainly
                // an audio-decoder underrun.  Snap forward so the receiver doesn't see a
                // permanent A/V offset after the transient glitch.  Record the recovery
                // for diagnostics; the missed audio window becomes silence at the receiver.
                effective = videoPts;
                Interlocked.Increment(ref _underrunRecoveries);
            }

            long next = effective + stepTicks;
            if (Interlocked.CompareExchange(ref _nextAudioTimecodeTicks, next, current) == current)
                return effective;
        }
    }
}

