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
/// <remarks>Swaps (<see cref="SetReference"/>/<see cref="RebaseReference"/>) must come from one
/// writer (the group's dispatcher); reads are tear-free from any thread - the reference and its
/// shift live in one immutable anchor swapped atomically, so a reader can never combine a new
/// reference with a stale shift (which read as a time jump).</remarks>
public sealed class SessionClock
{
    // Now = Reference.ElapsedSinceStart + Shift (rebaselined on reference swap)
    private sealed record Anchor(IPlaybackClock Reference, TimeSpan Shift);

    private Anchor _anchor;

    public SessionClock(IPlaybackClock reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _anchor = new Anchor(reference, TimeSpan.Zero);
    }

    /// <summary>Creates a live-led clock backed by a free-running <see cref="MonotonicWallClock"/>.</summary>
    public static SessionClock LiveWallClock() => new(new MonotonicWallClock());

    /// <summary>The current master time for this transport group.</summary>
    public TimeSpan Now
    {
        get
        {
            var anchor = Volatile.Read(ref _anchor);
            return anchor.Reference.ElapsedSinceStart + anchor.Shift;
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
        Volatile.Write(ref _anchor, new Anchor(reference, now - reference.ElapsedSinceStart));
    }

    /// <summary>
    /// Rebaseline the current reference after it discontinuously changed (for example a media playhead seek or
    /// loop wrap), preserving the supplied monotonic group time. The source coordinate may jump; the master
    /// coordinate must not. The owning <see cref="TransportTimeline"/> records the matching generation/anchor.
    /// </summary>
    public void RebaseReference(TimeSpan preservedNow)
    {
        var anchor = Volatile.Read(ref _anchor);
        Volatile.Write(ref _anchor, anchor with { Shift = preservedNow - anchor.Reference.ElapsedSinceStart });
    }
}
