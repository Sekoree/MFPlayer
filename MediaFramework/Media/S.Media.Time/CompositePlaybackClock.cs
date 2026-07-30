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

    private readonly Lock _blendGate = new();
    private long _transitionStartTicks;
    private int _blendWinnerIdx = -1;
    private TimeSpan _blendFromEmitted;
    private TimeSpan _lastEmitted;
    private bool _hasEmitted;
    private long _blendEpochId;
    // Wall tick of last co-advance EMA sample; -1 = prime next co read (0 is a valid Stopwatch tick).
    private long _coAdvanceLastSampleTicks = -1;

    private readonly Lock _epochGate = new();
    private long _epochId = PlaybackEpoch.Next();
    /// <summary>The (winner index, winner epoch) the current <see cref="_epochId"/> stands for. Either half
    /// changing is a discontinuity in what this clock emits - a handoff swaps to a leaf at an unrelated
    /// elapsed, a leaf re-anchoring rewinds it, and the neutral idle state (index -1) reports zero - so the
    /// composite takes a fresh id for all three rather than letting a consumer infer them.</summary>
    private int _epochWinnerIdx = -2;
    private long _epochWinnerEpochId;

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

    public bool IsAdvancing
    {
        get
        {
            foreach (var c in _candidates)
            {
                if (c.Clock.IsAdvancing) return true;
            }

            return false;
        }
    }

    public TimeSpan ElapsedSinceStart => Read().Elapsed;

    /// <inheritdoc />
    public long EpochId => Read().EpochId;

    /// <inheritdoc />
    public ClockReading Read()
    {
        var advCount = 0;
        var winnerIdx = -1;
        var winner = default(ClockReading);
        for (var i = 0; i < _candidates.Length; i++)
        {
            // Read the advancing flag and elapsed/epoch as one sample. Selecting through IsAdvancing and
            // then reading the winner again lets a stop/re-anchor between those calls return a stale winner
            // as advancing with an unrelated elapsed value.
            var reading = _candidates[i].Clock.Read();
            if (!reading.IsAdvancing) continue;
            advCount++;
            if (winnerIdx < 0)
            {
                winnerIdx = i;
                winner = reading;
            }
        }

        if (winnerIdx < 0)
        {
            lock (_blendGate)
            {
                _blendWinnerIdx = -1;
                _hasEmitted = false;
                _coAdvanceLastSampleTicks = -1;
            }

            return new ClockReading(EpochFor(-1, PlaybackEpoch.Single), TimeSpan.Zero, false);
        }

        var epochId = EpochFor(winnerIdx, winner.EpochId);
        var targetNow = winner.Elapsed;
        var hasHandoff = _blend.HasHandoffCrossFade && _blend.HandoffCrossFade.TotalSeconds > 0;
        var hasCo = _blend.HasCoAdvanceSmoothing && _blend.CoAdvanceSmoothingTau.TotalSeconds > 0;

        if (!hasHandoff && !hasCo)
            return new ClockReading(epochId, targetNow, true);

        var nowTicks = _nowTicks();
        lock (_blendGate)
        {
            if (!_hasEmitted)
            {
                _lastEmitted = targetNow;
                _blendFromEmitted = targetNow;
                _blendWinnerIdx = winnerIdx;
                _transitionStartTicks = nowTicks;
                _coAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
                _blendEpochId = epochId;
                _hasEmitted = true;
                return new ClockReading(epochId, targetNow, true);
            }

            if (winnerIdx != _blendWinnerIdx)
            {
                _blendFromEmitted = _lastEmitted;
                _blendWinnerIdx = winnerIdx;
                _transitionStartTicks = nowTicks;
                _coAdvanceLastSampleTicks = -1;
                _blendEpochId = epochId;
                if (targetNow < _lastEmitted)
                {
                    // A handoff already starts a fresh composite epoch, so this is the one safe point at
                    // which the elapsed coordinate may move backwards. A downward cross-fade cannot be
                    // represented by a monotonic clock: holding until the fade ends and then snapping
                    // would merely defer the same-epoch regression. Snap on the first reading instead.
                    _lastEmitted = targetNow;
                    _blendFromEmitted = targetNow;
                    _coAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
                    return new ClockReading(epochId, targetNow, true);
                }
            }
            else if (epochId != _blendEpochId)
            {
                // The same leaf explicitly re-anchored. A new epoch may restart at any coordinate, so do
                // not blend it against the prior epoch's high-water.
                _lastEmitted = targetNow;
                _blendFromEmitted = targetNow;
                _transitionStartTicks = nowTicks;
                _coAdvanceLastSampleTicks = hasCo ? nowTicks : -1;
                _blendEpochId = epochId;
                return new ClockReading(epochId, targetNow, true);
            }

            if (hasHandoff)
            {
                var elapsedSec = (nowTicks - _transitionStartTicks) / (double)Stopwatch.Frequency;
                var t = elapsedSec / _blend.HandoffCrossFade.TotalSeconds;
                if (t < 1.0)
                {
                    var w = SmoothStep01(t);
                    var lerped = LerpTimeSpan(_blendFromEmitted, targetNow, w);
                    // Leaf clocks are monotonic within an epoch, but rounding should not make this wrapper
                    // regress by a tick.
                    if (lerped < _lastEmitted)
                        lerped = _lastEmitted;
                    _lastEmitted = lerped;
                    return new ClockReading(epochId, lerped, true);
                }
            }

            if (hasCo && advCount >= 2)
            {
                if (_coAdvanceLastSampleTicks < 0)
                {
                    _lastEmitted = targetNow;
                    _coAdvanceLastSampleTicks = nowTicks;
                    return new ClockReading(epochId, targetNow, true);
                }

                var dt = (nowTicks - _coAdvanceLastSampleTicks) / (double)Stopwatch.Frequency;
                _coAdvanceLastSampleTicks = nowTicks;
                if (dt <= 0) dt = 1e-9;
                var tau = _blend.CoAdvanceSmoothingTau.TotalSeconds;
                var alpha = 1.0 - Math.Exp(-dt / tau);
                if (alpha > 0.95) alpha = 0.95;
                var smoothed = LerpTimeSpan(_lastEmitted, targetNow, alpha);
                if (smoothed < _lastEmitted)
                    smoothed = _lastEmitted;
                _lastEmitted = smoothed;
                return new ClockReading(epochId, smoothed, true);
            }

            _coAdvanceLastSampleTicks = -1;
            _lastEmitted = targetNow;
            return new ClockReading(epochId, targetNow, true);
        }
    }

    /// <summary>Current epoch for a (winner, winner epoch) pair, taking a fresh id whenever it changes.</summary>
    private long EpochFor(int winnerIdx, long winnerEpochId)
    {
        lock (_epochGate)
        {
            if (winnerIdx != _epochWinnerIdx || winnerEpochId != _epochWinnerEpochId)
            {
                _epochWinnerIdx = winnerIdx;
                _epochWinnerEpochId = winnerEpochId;
                _epochId = PlaybackEpoch.Next();
            }

            return _epochId;
        }
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
