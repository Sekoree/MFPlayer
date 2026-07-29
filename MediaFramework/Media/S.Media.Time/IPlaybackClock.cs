namespace S.Media.Time;

/// <summary>
/// One atomic sample of an <see cref="IPlaybackClock"/>: which epoch the clock is in, how far it has
/// advanced <em>within</em> that epoch, and whether it is still advancing. Consumers that must notice a
/// re-anchor (<see cref="MediaClock"/>'s fold, a fan-in owner's per-client epoch) compare
/// <see cref="EpochId"/> instead of inferring a boundary from an observed regression - the inference is
/// wrong for any master that dips and recovers.
/// </summary>
/// <param name="EpochId">
/// Identity of the current epoch. <see cref="PlaybackEpoch.Single"/> (0) means "this clock has exactly one
/// epoch and never re-anchors"; every other value comes from <see cref="PlaybackEpoch.Next"/> and is unique
/// process-wide, so ids from two different clocks can never compare equal by accident.
/// </param>
/// <param name="Elapsed">Playback duration accrued in this epoch (<see cref="IPlaybackClock.ElapsedSinceStart"/>).</param>
/// <param name="IsAdvancing">Whether the source is actively advancing (<see cref="IPlaybackClock.IsAdvancing"/>).</param>
public readonly record struct ClockReading(long EpochId, TimeSpan Elapsed, bool IsAdvancing);

/// <summary>Allocator for <see cref="ClockReading.EpochId"/> values.</summary>
public static class PlaybackEpoch
{
    /// <summary>The id of a clock that never re-anchors - reserved, never handed out by <see cref="Next"/>.</summary>
    public const long Single = 0;

    private static long _next;

    /// <summary>
    /// A fresh, process-wide-unique epoch id (never <see cref="Single"/>). Call it at every re-anchor:
    /// output Start/Flush, device loss or restart, a seek where the source coordinate jumps
    /// discontinuously, and ingest relocate.
    /// </summary>
    public static long Next() => Interlocked.Increment(ref _next);
}

/// <summary>
/// Read-only time source that <see cref="MediaClock"/> can slave to via
/// <c>MediaClock.SetMaster</c>. Typically implemented by the audio output that
/// owns the playback hardware (PortAudio output, CoreAudio, …): the output
/// reports how much audio it has actually played, and the clock derives its
/// position from that instead of a wall-clock <see cref="System.Diagnostics.Stopwatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Monotonicity is per epoch.</strong> <see cref="ElapsedSinceStart"/> never goes backwards while
/// <see cref="EpochId"/> is unchanged; it is free to restart from zero (or any other value) the moment the
/// epoch id changes. An epoch is one continuous run of the underlying source - it ends at every re-anchor:
/// an output's Start/Flush, device loss or restart, a seek whose source coordinate jumps discontinuously,
/// an ingest relocate, a composite handing off to another leaf. Implementations must take a new id at every
/// one of those and must NOT take one for anything position-continuous. Elapsed represents real playback
/// progress (samples consumed by the device divided by sample rate, etc.); pausing the underlying source
/// should freeze it, stopping should also freeze it.
/// </para>
/// <para>
/// <see cref="Read"/> is the sanctioned way to consume the pair: reading <see cref="EpochId"/> and
/// <see cref="ElapsedSinceStart"/> separately can tear across a re-anchor, and a torn pair is exactly the
/// bug class this contract exists to remove. The default implementation composes the members and is
/// therefore only correct for a clock whose epoch is constant - <strong>any implementation that ever takes
/// a new epoch id must override <see cref="Read"/></strong> and produce all three fields from one coherent
/// snapshot. Wrappers that forward the members must forward <see cref="Read"/> too, or they silently report
/// <see cref="PlaybackEpoch.Single"/> over a clock that re-anchors.
/// </para>
/// <para>
/// Implementations should be safe to read concurrently from <see cref="MediaClock"/>'s
/// driver thread. <see cref="ElapsedSinceStart"/> is read frequently - keep it
/// cheap (a couple of <see cref="System.Threading.Interlocked"/> reads + a
/// division is fine) and allocation-free.
/// </para>
/// </remarks>
public interface IPlaybackClock
{
    /// <summary>Playback duration accrued since the current epoch began; monotonic within that epoch.</summary>
    TimeSpan ElapsedSinceStart { get; }

    /// <summary>True when the source is actively advancing (playing, not paused/stopped/disposed).</summary>
    bool IsAdvancing { get; }

    /// <summary>
    /// Identity of the epoch <see cref="ElapsedSinceStart"/> is measured in. Defaults to
    /// <see cref="PlaybackEpoch.Single"/> for clocks that never re-anchor.
    /// </summary>
    long EpochId => PlaybackEpoch.Single;

    /// <summary>One atomic (epoch, elapsed, advancing) sample - see the interface remarks.</summary>
    ClockReading Read() => new(EpochId, ElapsedSinceStart, IsAdvancing);
}
