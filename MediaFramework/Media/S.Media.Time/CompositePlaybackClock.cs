using System.Diagnostics;

namespace S.Media.Time;

/// <summary>
/// Public master clock: merges several <see cref="IPlaybackClock"/> instances by <strong>priority</strong>:
/// the active clock is the highest-priority candidate whose <see cref="IPlaybackClock.IsAdvancing"/>
/// is <c>true</c>. <see cref="ElapsedSinceStart"/> follows that clock unless a <see cref="CompositePlaybackClockBlend"/>
/// enables <see cref="CompositePlaybackClockBlend.HandoffCrossFade"/> and/or <see cref="CompositePlaybackClockBlend.CoAdvanceSmoothingTau"/>.
/// </summary>
/// <remarks>
/// <para>
/// When no candidate is advancing, <see cref="IsAdvancing"/> is <c>false</c> and
/// <see cref="ElapsedSinceStart"/> returns <see cref="TimeSpan.Zero"/> (neutral idle state).
/// </para>
/// <para>
/// When several candidates have <see cref="IPlaybackClock.IsAdvancing"/> <c>true</c> at once,
/// <see cref="ElapsedSinceStart"/> is driven by the single highest-<see cref="PlaybackClockCandidate.Priority"/> entry,
/// optionally smoothed: <see cref="CompositePlaybackClockBlend.HandoffCrossFade"/> on winner changes,
/// <see cref="CompositePlaybackClockBlend.CoAdvanceSmoothingTau"/> while multiple clocks keep advancing.
/// </para>
/// <para>
/// Use with <see cref="MediaClockExtensions.SetMasterChain"/> to feed <see cref="MediaClock"/> from several
/// clocks (hardware audio, PTS+wall, NDI ingest, …) with explicit priority.
/// </para>
/// <para>
/// Candidates are evaluated in registration order within the same priority value
/// (first registered wins ties - the constructor sorts by priority descending, then by registration index ascending).
/// </para>
/// <para>
/// <see cref="Read"/> is the authority: one atomic sample of every candidate, and the only member that
/// advances the blend. The three <see cref="IPlaybackClock"/> members project the same snapshot without
/// advancing it, so observing this clock cannot alter what it reports next.
/// </para>
/// <para>
/// Priority merge affects <see cref="IPlaybackClock.ElapsedSinceStart"/> / <see cref="IPlaybackClock.IsAdvancing"/> only;
/// graph-wide coordinated master PPM and synchronized multi-output drop/repeat remain host-owned - see
/// <see cref="MediaClock"/> and <see cref="S.Media.Core.Audio.AudioRouter"/>.
/// </para>
/// </remarks>
public sealed class CompositePlaybackClock : IPlaybackClock
{
    private readonly PlaybackClockCandidate[] _candidates;
    private readonly CompositePlaybackClockBlend _blend;
    private readonly Func<long> _nowTicks;

    /// <summary>
    /// The ONE gate a reading is taken under: the candidate sweep, the epoch resolution and the blend all
    /// live inside it. They used to sit behind two separate locks with the sweep outside both, so two
    /// concurrent readers interleaved - each pairing a freshly allocated epoch id with the other's winner -
    /// and the id then churned on EVERY read. A <see cref="MediaClock"/> mastered on this clock re-anchors at
    /// each new id, so that churn silently discarded the accrual between reads.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>Blend-filter state. Advanced ONLY by <see cref="Read"/>; a member read takes a copy and
    /// throws it away (see <see cref="Sample"/>).</summary>
    private struct BlendState
    {
        public long TransitionStartTicks;
        public int WinnerIdx;
        public TimeSpan FromEmitted;
        public TimeSpan LastEmitted;
        public bool HasEmitted;
        public long EpochId;
        // Wall tick of last co-advance EMA sample; -1 = prime next co read (0 is a valid Stopwatch tick).
        public long CoAdvanceLastSampleTicks;
    }

    private BlendState _blendState = new() { WinnerIdx = -1, CoAdvanceLastSampleTicks = -1 };

    private long _epochId = PlaybackEpoch.Next();
    /// <summary>The (winner index, winner epoch) the current <see cref="_epochId"/> stands for. Either half
    /// changing is a discontinuity in what this clock emits - a handoff swaps to a leaf at an unrelated
    /// elapsed, a leaf re-anchoring rewinds it, and the neutral idle state (index -1) reports zero - so the
    /// composite takes a fresh id for all three rather than letting a consumer infer them.</summary>
    private int _epochWinnerIdx = -2;
    private long _epochWinnerEpochId;
    /// <summary>Highest elapsed emitted in the current <see cref="_epochId"/>. This clock owes its consumers
    /// the same per-epoch monotonic contract it demands of its leaves (<see cref="IPlaybackClock"/> remarks),
    /// and forwarding a winner's elapsed verbatim did not honour it: a leaf regressing inside its own epoch
    /// passed straight through as a same-epoch regression of the COMPOSITE. Reset with the id, because a new
    /// epoch may legitimately start at any coordinate.</summary>
    private TimeSpan _epochHighWater;

    /// <param name="candidates">Registration list (tie-break: earlier entry wins at equal priority).</param>
    public CompositePlaybackClock(params PlaybackClockCandidate[] candidates)
        : this(CompositePlaybackClockBlend.Disabled, candidates, null) { }

    /// <param name="blend">Optional handoff crossfade and/or co-advance smoothing.</param>
    /// <param name="candidates">Registration list (tie-break: earlier entry wins at equal priority).</param>
    public CompositePlaybackClock(CompositePlaybackClockBlend blend, params PlaybackClockCandidate[] candidates)
        : this(blend, candidates, null) { }

    internal CompositePlaybackClock(CompositePlaybackClockBlend blend, Func<long> clockTicks, params PlaybackClockCandidate[] candidates)
        : this(blend, candidates, clockTicks ?? throw new ArgumentNullException(nameof(clockTicks)))
    {
    }

    internal CompositePlaybackClock(CompositePlaybackClockBlend blend, PlaybackClockCandidate[] candidates, Func<long>? nowTicks)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Length == 0)
            throw new ArgumentException("at least one playback clock candidate is required", nameof(candidates));

        _blend = blend;
        _nowTicks = nowTicks ?? (() => Stopwatch.GetTimestamp());
        _candidates = SortCandidates(candidates);
    }

    private static PlaybackClockCandidate[] SortCandidates(PlaybackClockCandidate[] candidates)
    {
        var indexed = new (PlaybackClockCandidate cand, int reg)[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            indexed[i] = (candidates[i], i);
        Array.Sort(indexed, static (a, b) =>
        {
            var p = b.cand.Priority.CompareTo(a.cand.Priority);
            return p != 0 ? p : a.reg.CompareTo(b.reg);
        });
        var sorted = new PlaybackClockCandidate[candidates.Length];
        for (var i = 0; i < indexed.Length; i++)
            sorted[i] = indexed[i].cand;
        return sorted;
    }

    /// <inheritdoc />
    /// <remarks>Decided by the same atomic candidate sweep <see cref="Read"/> uses, not by the candidates'
    /// individual <see cref="IPlaybackClock.IsAdvancing"/> members: selecting through the members and then
    /// sampling again lets a clock that stops in between be reported as advancing here while
    /// <see cref="Read"/> deselects it from the same instant.</remarks>
    public bool IsAdvancing => Sample(commit: false).IsAdvancing;

    /// <inheritdoc />
    /// <remarks>A projection - see <see cref="Read"/> for why the members do not advance the blend.</remarks>
    public TimeSpan ElapsedSinceStart => Sample(commit: false).Elapsed;

    /// <inheritdoc />
    /// <remarks>A projection - see <see cref="Read"/> for why the members do not advance the blend.</remarks>
    public long EpochId => Sample(commit: false).EpochId;

    /// <inheritdoc />
    /// <remarks>
    /// <para><strong>Read is the authority.</strong> It is the only member that advances the blend filters
    /// (cross-fade start, co-advance EMA, last-emitted). The three member reads take the same coherent
    /// snapshot but discard the state update, so merely OBSERVING this clock cannot change what it goes on to
    /// report: reading <see cref="EpochId"/> used to run all of Read, which started the hand-off cross-fade
    /// at the moment of the observation and sampled the co-advance EMA an extra time per poll.</para>
    /// <para>Epoch identity and the monotonic high-water are NOT part of that state: they record a
    /// discontinuity that genuinely happened and are idempotent for a given sample, so a member read
    /// resolves them exactly as Read would - never allocating a second id for the same
    /// (winner, winner epoch) pair.</para>
    /// </remarks>
    public ClockReading Read() => Sample(commit: true);

    private ClockReading Sample(bool commit)
    {
        var nowTicks = _nowTicks();
        lock (_gate)
        {
            var advCount = 0;
            var winnerIdx = -1;
            var winner = default(ClockReading);
            for (var i = 0; i < _candidates.Length; i++)
            {
                // Read the advancing flag and elapsed/epoch as one sample. Selecting through IsAdvancing and
                // then reading the winner again lets a stop/re-anchor between those calls return a stale
                // winner as advancing with an unrelated elapsed value.
                var reading = _candidates[i].Clock.Read();
                if (!reading.IsAdvancing) continue;
                advCount++;
                if (winnerIdx < 0)
                {
                    winnerIdx = i;
                    winner = reading;
                }
            }

            var epochId = EpochForUnlocked(winnerIdx, winnerIdx < 0 ? PlaybackEpoch.Single : winner.EpochId);
            if (winnerIdx < 0)
            {
                if (commit)
                {
                    _blendState.WinnerIdx = -1;
                    _blendState.HasEmitted = false;
                    _blendState.CoAdvanceLastSampleTicks = -1;
                }

                return new ClockReading(epochId, TimeSpan.Zero, false);
            }

            var emitted = BlendUnlocked(commit, nowTicks, winnerIdx, winner.Elapsed, epochId, advCount);
            return new ClockReading(epochId, RaiseEpochHighWaterUnlocked(emitted), true);
        }
    }

    /// <summary>The blended coordinate for one sample. Operates on a COPY of <see cref="_blendState"/> and
    /// publishes it only when <paramref name="commit"/> - the logic is otherwise unchanged.</summary>
    private TimeSpan BlendUnlocked(
        bool commit, long nowTicks, int winnerIdx, TimeSpan targetNow, long epochId, int advCount)
    {
        var hasHandoff = _blend.HasHandoffCrossFade && _blend.HandoffCrossFade.TotalSeconds > 0;
        var hasCo = _blend.HasCoAdvanceSmoothing && _blend.CoAdvanceSmoothingTau.TotalSeconds > 0;
        if (!hasHandoff && !hasCo)
            return targetNow;

        var s = _blendState;
        TimeSpan emitted;
        if (!s.HasEmitted)
        {
            s.LastEmitted = targetNow;
            s.FromEmitted = targetNow;
            s.WinnerIdx = winnerIdx;
            s.TransitionStartTicks = nowTicks;
            s.CoAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
            s.EpochId = epochId;
            s.HasEmitted = true;
            emitted = targetNow;
        }
        else if (winnerIdx != s.WinnerIdx && targetNow < s.LastEmitted)
        {
            // A handoff already starts a fresh composite epoch, so this is the one safe point at which the
            // elapsed coordinate may move backwards. A downward cross-fade cannot be represented by a
            // monotonic clock: holding until the fade ends and then snapping would merely defer the
            // same-epoch regression. Snap on the first reading instead.
            s.LastEmitted = targetNow;
            s.FromEmitted = targetNow;
            s.WinnerIdx = winnerIdx;
            s.TransitionStartTicks = nowTicks;
            s.CoAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
            s.EpochId = epochId;
            emitted = targetNow;
        }
        else if (winnerIdx == s.WinnerIdx && epochId != s.EpochId)
        {
            // The same leaf explicitly re-anchored. A new epoch may restart at any coordinate, so do not
            // blend it against the prior epoch's high-water.
            s.LastEmitted = targetNow;
            s.FromEmitted = targetNow;
            s.TransitionStartTicks = nowTicks;
            s.CoAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
            s.EpochId = epochId;
            emitted = targetNow;
        }
        else
        {
            if (winnerIdx != s.WinnerIdx)
            {
                // Upward handoff: cross-fade from what this clock last emitted to the new winner.
                s.FromEmitted = s.LastEmitted;
                s.WinnerIdx = winnerIdx;
                s.TransitionStartTicks = nowTicks;
                s.CoAdvanceLastSampleTicks = -1;
                s.EpochId = epochId;
            }

            emitted = BlendCore(ref s, nowTicks, targetNow, advCount, hasHandoff, hasCo);
        }

        if (commit)
            _blendState = s;
        return emitted;
    }

    private TimeSpan BlendCore(
        ref BlendState s, long nowTicks, TimeSpan targetNow, int advCount, bool hasHandoff, bool hasCo)
    {
        if (hasHandoff)
        {
            var elapsedSec = (nowTicks - s.TransitionStartTicks) / (double)Stopwatch.Frequency;
            var t = elapsedSec / _blend.HandoffCrossFade.TotalSeconds;
            if (t < 1.0)
            {
                var w = SmoothStep01(t);
                var lerped = LerpTimeSpan(s.FromEmitted, targetNow, w);
                // Leaf clocks are monotonic within an epoch, but rounding should not make this wrapper
                // regress by a tick.
                if (lerped < s.LastEmitted)
                    lerped = s.LastEmitted;
                s.LastEmitted = lerped;
                return lerped;
            }
        }

        if (hasCo && advCount >= 2)
        {
            if (s.CoAdvanceLastSampleTicks < 0)
            {
                s.LastEmitted = targetNow;
                s.CoAdvanceLastSampleTicks = nowTicks;
                return targetNow;
            }

            var dt = (nowTicks - s.CoAdvanceLastSampleTicks) / (double)Stopwatch.Frequency;
            s.CoAdvanceLastSampleTicks = nowTicks;
            if (dt <= 0) dt = 1e-9;
            var tau = _blend.CoAdvanceSmoothingTau.TotalSeconds;
            var alpha = 1.0 - Math.Exp(-dt / tau);
            if (alpha > 0.95) alpha = 0.95;
            var smoothed = LerpTimeSpan(s.LastEmitted, targetNow, alpha);
            if (smoothed < s.LastEmitted)
                smoothed = s.LastEmitted;
            s.LastEmitted = smoothed;
            return smoothed;
        }

        s.CoAdvanceLastSampleTicks = -1;
        s.LastEmitted = targetNow;
        return targetNow;
    }

    /// <summary>Current epoch for a (winner, winner epoch) pair, taking a fresh id whenever it changes and
    /// restarting the monotonic high-water with it. Must be called under <see cref="_gate"/>.</summary>
    private long EpochForUnlocked(int winnerIdx, long winnerEpochId)
    {
        if (winnerIdx != _epochWinnerIdx || winnerEpochId != _epochWinnerEpochId)
        {
            _epochWinnerIdx = winnerIdx;
            _epochWinnerEpochId = winnerEpochId;
            _epochId = PlaybackEpoch.Next();
            _epochHighWater = TimeSpan.Zero;
        }

        return _epochId;
    }

    /// <summary>Enforces this clock's own per-epoch monotonic contract. Must be called under
    /// <see cref="_gate"/>. Applied to member reads as well as <see cref="Read"/>: it is the guarantee the
    /// interface makes to consumers, not part of the blend state a member read must leave alone.</summary>
    private TimeSpan RaiseEpochHighWaterUnlocked(TimeSpan emitted)
    {
        if (emitted < _epochHighWater)
            return _epochHighWater;
        _epochHighWater = emitted;
        return emitted;
    }

    private static double SmoothStep01(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static TimeSpan LerpTimeSpan(TimeSpan a, TimeSpan b, double w)
    {
        var x = a.Ticks + (b.Ticks - a.Ticks) * w;
        return TimeSpan.FromTicks((long)x);
    }
}

/// <summary>Entry for <see cref="CompositePlaybackClock"/>.</summary>
/// <param name="Clock">Underlying clock implementing <see cref="IPlaybackClock"/> (e.g. hardware audio output or <see cref="VideoPtsClock"/>).</param>
/// <param name="Priority">Higher wins when multiple clocks are advancing simultaneously.</param>
public readonly record struct PlaybackClockCandidate(IPlaybackClock Clock, int Priority);
