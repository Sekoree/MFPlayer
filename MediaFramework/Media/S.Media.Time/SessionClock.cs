namespace S.Media.Time;

/// <summary>
/// The master time reference for one <strong>transport group</strong> (a cue, or a set fired/seeked
/// together) - D4. Every source in the group schedules against it via a <see cref="SourceTimeline"/>.
/// It advances from one reference <see cref="IPlaybackClock"/>:
/// <list type="bullet">
/// <item><b>file-led</b> - the group's master audio output (a clocked device): pass its clock.</item>
/// <item><b>live-led</b> - no file output to slave to: use <see cref="LiveWallClock"/> (a
///   <see cref="MonotonicWallClock"/>).</item>
/// </list>
/// When the reference stops advancing (<see cref="IsAdvancing"/> == false) the group idles - mastership
/// never floats to an unrelated source. Switching the reference is explicit (<see cref="SetReference"/>)
/// and continuity-preserving: <see cref="Now"/> does not jump across the swap.
/// </summary>
/// <remarks>
/// <para><strong>Monotonic by mechanism, not by convention.</strong> The reference reports its own epoch
/// boundaries (<see cref="ClockReading.EpochId"/>, plan D), and <see cref="Now"/> compares that id against
/// the one its anchor was taken in. A reference that re-anchors - a playhead seek, a loop wrap, an output
/// flush, a device restart - is rebaselined <em>on the spot</em>, preserving the group time already
/// reported. Group master time therefore stays monotonic without every seek path remembering to announce
/// itself: <see cref="RebaseReference"/> is now the belt to that braces (it still pins <see cref="Now"/> to
/// an exact caller-captured instant, which the automatic path can only approximate with the last value it
/// observed), not the sole mechanism. The reference is read through <see cref="IPlaybackClock.Read"/> so the
/// elapsed value can never be paired with an epoch a racing re-anchor already invalidated.</para>
/// <para>Swaps (<see cref="SetReference"/>/<see cref="RebaseReference"/>) must come from one writer (the
/// group's dispatcher); reads are tear-free from any thread - the reference, its epoch and its shift live in
/// one immutable anchor swapped atomically, so a reader can never combine a new reference with a stale shift
/// (which read as a time jump). The automatic rebase is published with the same atomic swap, so concurrent
/// readers converge on one rebased anchor instead of each inventing its own.</para>
/// </remarks>
public sealed class SessionClock
{
    // Now = Reference.Read().Elapsed + Shift, where Shift belongs to ReferenceEpochId (rebaselined on a
    // reference swap AND on any re-anchor the reference reports).
    private sealed record Anchor(IPlaybackClock Reference, long ReferenceEpochId, TimeSpan Shift);

    private Anchor _anchor;

    /// <summary>High-water of <see cref="Now"/> in ticks: the value an automatic rebase preserves, and the
    /// floor that enforces monotonicity inside one reference epoch (where the reference is monotonic by
    /// contract, so the clamp only ever catches a violation or a torn read). Reset - deliberately, possibly
    /// downwards - by <see cref="SetReference"/> and <see cref="RebaseReference"/>, which are authoritative
    /// about where group time now stands.</summary>
    private long _nowFloorTicks;

    /// <summary>Bounds the rebase retry loop. A reference that reported a new epoch on every single read
    /// would spin forever otherwise; after this many attempts <see cref="Now"/> reports the preserved floor,
    /// which is still monotonic.</summary>
    private const int MaxRebaseAttempts = 8;

    public SessionClock(IPlaybackClock reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var reading = reference.Read();
        _anchor = new Anchor(reference, reading.EpochId, TimeSpan.Zero);
        _nowFloorTicks = reading.Elapsed.Ticks;
    }

    /// <summary>Creates a live-led clock backed by a free-running <see cref="MonotonicWallClock"/>.</summary>
    public static SessionClock LiveWallClock() => new(new MonotonicWallClock());

    /// <summary>The current master time for this transport group.</summary>
    public TimeSpan Now
    {
        get
        {
            for (var attempt = 0; attempt < MaxRebaseAttempts; attempt++)
            {
                var anchor = Volatile.Read(ref _anchor);
                var reading = anchor.Reference.Read();
                if (reading.EpochId == anchor.ReferenceEpochId)
                    return RaiseFloor(reading.Elapsed + anchor.Shift);

                // The reference re-anchored and nobody told us. Its source coordinate may have jumped
                // anywhere (a seek target, zero after a flush); the master coordinate must not follow, so
                // rebase onto the new epoch preserving the group time already reported.
                var preserved = new TimeSpan(Volatile.Read(ref _nowFloorTicks));
                var rebased = new Anchor(anchor.Reference, reading.EpochId, preserved - reading.Elapsed);
                Interlocked.CompareExchange(ref _anchor, rebased, anchor);
                // Whoever won the swap, re-read through the anchor that is now published.
            }

            return new TimeSpan(Volatile.Read(ref _nowFloorTicks));
        }
    }

    /// <summary>True while the reference is advancing; false ⇒ the group is idle (paused/stopped).</summary>
    public bool IsAdvancing => Volatile.Read(ref _anchor).Reference.IsAdvancing;

    /// <summary>The current reference clock.</summary>
    public IPlaybackClock Reference => Volatile.Read(ref _anchor).Reference;

    /// <summary>
    /// Swap the reference (e.g. promote a new master output) without a time jump: the new reference's
    /// elapsed is rebaselined so <see cref="Now"/> is continuous across the swap.
    /// </summary>
    public void SetReference(IPlaybackClock reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var now = Now;
        // One atomic reading: an elapsed captured apart from its epoch would make the very next read look
        // like an unannounced re-anchor and rebase a second time.
        var reading = reference.Read();
        Volatile.Write(ref _nowFloorTicks, now.Ticks);
        Volatile.Write(ref _anchor, new Anchor(reference, reading.EpochId, now - reading.Elapsed));
    }

    /// <summary>
    /// Rebaseline the current reference after it discontinuously changed (for example a media playhead seek or
    /// loop wrap), preserving the supplied monotonic group time. The source coordinate may jump; the master
    /// coordinate must not. The owning <see cref="TransportTimeline"/> records the matching generation/anchor.
    /// </summary>
    /// <remarks><see cref="Now"/> already rebases itself when the reference REPORTS a re-anchor; this is the
    /// explicit form, and it wins: <paramref name="preservedNow"/> pins group time to an instant the caller
    /// captured before the jump, even when that is below what a read has since reported.</remarks>
    public void RebaseReference(TimeSpan preservedNow)
    {
        var anchor = Volatile.Read(ref _anchor);
        var reading = anchor.Reference.Read();
        Volatile.Write(ref _nowFloorTicks, preservedNow.Ticks);
        Volatile.Write(ref _anchor,
            new Anchor(anchor.Reference, reading.EpochId, preservedNow - reading.Elapsed));
    }

    /// <summary>
    /// How many times the reference went backwards inside one epoch and was held at the floor, and the
    /// worst single regression. Inside an epoch the reference is monotonic BY CONTRACT, so a non-zero
    /// count means the clock below this one is faulty (or a read tore across its re-anchor) - it is not
    /// a normal event and it is not this clock's fault.
    /// </summary>
    /// <remarks>
    /// The clamp itself stays: holding at the floor is the only safe response, and group time must never
    /// rewind. What changes is that it is no longer silent. Group time passes through several layers that
    /// each enforce this same contract, so a breach at the bottom gets absorbed by the first clamp above
    /// it and the symptom surfaces somewhere unrelated. Counting each clamp where it fires is what makes
    /// the actual culprit nameable instead of inferred.
    /// </remarks>
    public (long Count, TimeSpan Worst) ReferenceRegressions =>
        (Interlocked.Read(ref _referenceRegressions), new TimeSpan(Interlocked.Read(ref _worstRegressionTicks)));

    private long _referenceRegressions;
    private long _worstRegressionTicks;

    /// <summary>CAS high-water on <see cref="_nowFloorTicks"/>; returns the resulting master time.</summary>
    private TimeSpan RaiseFloor(TimeSpan now)
    {
        var ticks = now.Ticks;
        var seen = Volatile.Read(ref _nowFloorTicks);
        while (ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref _nowFloorTicks, ticks, seen);
            if (previous == seen)
                return now;
            seen = previous;
        }

        if (ticks < seen)
            NoteRegression(seen - ticks);
        return new TimeSpan(seen);
    }

    private void NoteRegression(long backwardsTicks)
    {
        Interlocked.Increment(ref _referenceRegressions);
        var worst = Interlocked.Read(ref _worstRegressionTicks);
        while (backwardsTicks > worst)
        {
            var previous = Interlocked.CompareExchange(ref _worstRegressionTicks, backwardsTicks, worst);
            if (previous == worst)
                return;
            worst = previous;
        }
    }
}
