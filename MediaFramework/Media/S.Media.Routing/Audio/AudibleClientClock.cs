namespace S.Media.Routing;

/// <summary>
/// The client-side audible playback clock, extracted verbatim from <c>SharedAudioOutput.ClientInput</c>
/// (HaCue plan, Phase 3: "reuse ClientInput's epoch/lead machinery rather than re-deriving it") so the
/// patch bay's producer leases and the shared output's client leases run the SAME battle-tested
/// algorithm. It rebases a terminal's device clock to a per-client epoch (zero at attach and at every
/// deliberate <see cref="Reanchor"/>), subtracts the low-passed lead still between the client's
/// submissions and the speaker (<paramref name="measuredLeadTicks"/> - the caller measures its own
/// backlog plus everything downstream), eases that subtraction in C¹-continuously at segment start,
/// holds monotonic against estimate growth and terminal misbehavior via CAS high-waters, recovers
/// from ANNOUNCED terminal re-anchors by resuming from the high-water, and degrades to a wall-clock
/// stopwatch domain spliced onto the high-water when the terminal clock is absent or fails mid-read.
/// </summary>
/// <remarks>
/// Thread model is unchanged from the origin: any thread may read; re-anchors and the lead filter
/// serialize on one internal gate; the hot path is allocation-free and exception-free. The
/// <paramref name="measuredLeadTicks"/> callback runs on the clock hot path and must not throw.
/// </remarks>
internal sealed class AudibleClientClock : IPipelineLeadClock
{
    private readonly IPlaybackClock? _terminalClock;
    private readonly Func<long> _measuredLeadTicks;
    private readonly System.Diagnostics.Stopwatch _fallbackElapsed = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Time constant of the DAC-lead low-pass, measured in the RAW clock's own domain (see
    /// <see cref="SmoothLeadTicks"/>). Long enough to average out the pump/bus sawtooth at production
    /// sizes, short enough that a genuine device re-negotiation is tracked within a fraction of a second.</summary>
    private const double LeadSmoothingSeconds = 0.25;

    private readonly Lock _epochGate = new();
    private long _terminalEpochTicks;
    /// <summary><see cref="ClockReading.EpochId"/> of the terminal that <see cref="_terminalEpochTicks"/>
    /// was captured in. The terminal REPORTS its re-anchors, so recovery is an equality check rather than
    /// the old "a regression below the epoch must mean it re-anchored" guess.</summary>
    private long _terminalEpochId;
    /// <summary>This clock's own <see cref="IPlaybackClock.EpochId"/>: a fresh id at attach and at every
    /// <see cref="Reanchor"/> - the two places <see cref="ElapsedSinceStart"/> restarts at zero. A terminal
    /// re-anchor does NOT take one: the client clock stays continuous across it by design.</summary>
    private long _epochId = PlaybackEpoch.Next();
    private long _fallbackEpochTicks;
    /// <summary>1 once <see cref="RebaseFallbackToHighWater"/> spliced the stopwatch domain onto the
    /// terminal's high-water for the current outage; cleared by the next successful terminal read and by
    /// every deliberate re-anchor, so a later outage re-splices instead of inheriting a stale baseline.</summary>
    private int _fallbackRebased;
    /// <summary>Low-passed DAC lead in ticks - the value actually subtracted. Guarded by
    /// <see cref="_epochGate"/> together with <see cref="_leadSampledAtRawTicks"/>.</summary>
    private long _leadTicks;
    /// <summary>Raw since-epoch reading the lead estimate was last integrated at; -1 = unseeded
    /// (fresh segment), which makes the next reading adopt the measurement outright.</summary>
    private long _leadSampledAtRawTicks = -1;
    /// <summary>High-water mark of raw since-epoch ticks since the last deliberate re-anchor - the
    /// resume point when a terminal-clock regression forces the epoch to be re-derived.</summary>
    private long _maxSinceEpochTicks;
    /// <summary>High-water mark of the latency-compensated value actually reported - the clock holds
    /// here instead of stepping backwards when the lead estimate grows.</summary>
    private long _maxAudibleTicks;
    private int _stopped;

    public AudibleClientClock(IPlaybackClock? terminalClock, Func<long> measuredLeadTicks)
    {
        ArgumentNullException.ThrowIfNull(measuredLeadTicks);
        _terminalClock = terminalClock;
        _measuredLeadTicks = measuredLeadTicks;
        Reanchor();
    }

    /// <summary>Marks the owning endpoint disposed: <see cref="IsAdvancing"/> and the advancing half of
    /// <see cref="Read"/> report false from here on. Reads stay safe forever.</summary>
    public void Stop() => Volatile.Write(ref _stopped, 1);

    /// <summary>
    /// The audio path depth currently between production and the speaker — the RAW measurement, not
    /// the low-passed value the clock subtracts.
    /// </summary>
    /// <remarks>
    /// A voice that has no audio of its own needs this number once, at start: its clock is anchored
    /// to this (already-advancing) show clock the moment Play runs, while a SOUNDING voice's clock
    /// holds at zero until its first sample actually leaves the speaker — one pipeline depth later.
    /// Deferring the silent voice's start by this much is what makes its picture land on the audible
    /// programme instead of leading it by the whole audio path (measured ~150–500 ms on the incident
    /// rig — a fixed, rate-locked video lead for the length of the cue).
    /// </remarks>
    public TimeSpan CurrentPipelineLead
    {
        get
        {
            try
            {
                return new TimeSpan(Math.Max(0, _measuredLeadTicks()));
            }
            catch
            {
                return TimeSpan.Zero; // torn down mid-read — "unknown", same answer latency gives
            }
        }
    }

    /// <summary>
    /// Terminal device time minus this client's epoch, minus the client's DAC lead, clamped at zero
    /// and monotonic. See the origin's <c>ClientInput.ElapsedSinceStart</c> doc for the full contract.
    /// </summary>
    public TimeSpan ElapsedSinceStart => ReadRaw().elapsed;

    /// <inheritdoc />
    public long EpochId => Volatile.Read(ref _epochId);

    /// <inheritdoc />
    /// <remarks>Composed under <see cref="_epochGate"/>, the same gate every re-anchor writes
    /// <see cref="_epochId"/> under, so the id can never be paired with elapsed from the other side of a
    /// <see cref="Reanchor"/>.</remarks>
    public ClockReading Read()
    {
        lock (_epochGate)
        {
            var (elapsed, advancing) = ReadRaw();
            return new ClockReading(_epochId, elapsed, advancing);
        }
    }

    /// <summary>
    /// The client clock proper. The terminal's epoch id is compared, never inferred: an id change means
    /// the terminal restarted its segment, and only then is our epoch re-derived from the high-water so
    /// this clock continues monotonically. A regression WITHOUT an id change is a broken terminal (or a
    /// torn read of one mid-re-anchor) - clamped to the high-water, which keeps the report monotonic
    /// without inventing an epoch that was never announced.
    /// </summary>
    private (TimeSpan elapsed, bool advancing) ReadRaw()
    {
        if (_terminalClock is not null)
        {
            try
            {
                var terminal = _terminalClock.Read();
                var ticks = terminal.EpochId != Volatile.Read(ref _terminalEpochId)
                    ? RecoverFromTerminalReanchor(terminal)
                    : terminal.Elapsed.Ticks - Volatile.Read(ref _terminalEpochTicks);
                // High-water mark: the resume point for a later re-anchor recovery, and the clamp that
                // holds the report steady if the terminal breaks its per-epoch monotonic contract.
                ticks = RaiseHighWater(ref _maxSinceEpochTicks, ticks);
                if (Volatile.Read(ref _fallbackRebased) != 0)
                    Volatile.Write(ref _fallbackRebased, 0); // terminal is authoritative again
                return (ReportAudible(ticks), Volatile.Read(ref _stopped) == 0 && terminal.IsAdvancing);
            }
            catch
            {
                // Terminal stopped/disposed mid-read - fall through to the wall-clock domain, CONTINUING
                // from the high-water rather than adopting the fallback stopwatch raw (see below).
                RebaseFallbackToHighWater();
            }
        }

        return (ReportAudible(_fallbackElapsed.Elapsed.Ticks - Volatile.Read(ref _fallbackEpochTicks)),
            Volatile.Read(ref _stopped) == 0);
    }

    /// <summary>
    /// Splices the wall-clock fallback onto the raw high-water the terminal domain had reached, once per
    /// outage. <see cref="_fallbackElapsed"/> runs from attach and counts every second the DEVICE spent
    /// paused, so switching domains outright reported wall-time-since-attach in the SAME epoch: a device
    /// paused 30 s and then lost handed the consumer a monotonic +30 s jump that the high-water cannot
    /// catch (it only clamps regressions). Re-baselining makes the fallback resume where the device left
    /// off and advance at wall rate from there. If the terminal recovers, its (lower) reading is held at
    /// the high-water until it catches up - bounded by the outage, not by the whole session.
    /// </summary>
    /// <remarks>
    /// Ordering matters: the flag is published LAST, after the epoch is written, both under
    /// <see cref="_epochGate"/>. The original code claimed the flag first (Interlocked.Exchange) and
    /// wrote the epoch second, which opened a two-reader race at the first terminal failure of an
    /// outage: A won the exchange and blocked on the gate; B saw the flag already set, returned
    /// immediately, and computed elapsed from the STALE pre-splice epoch - i.e. wall-time-since-attach,
    /// potentially minutes ahead. That reading flowed through <see cref="ReportAudible"/> and PERMANENTLY
    /// ratcheted <see cref="_maxAudibleTicks"/> (the monotonic high-water cannot come back down), pinning
    /// the clock far in the future for the rest of the session. With flag-last ordering a reader either
    /// observes the flag set - the volatile read/write pair guarantees it then also observes the spliced
    /// epoch - or takes the gate and finds the splice done (or does it itself). The common no-outage
    /// path is untouched and stays lock-free: successful terminal reads only ever Volatile.Read the flag.
    /// </remarks>
    private void RebaseFallbackToHighWater()
    {
        if (Volatile.Read(ref _fallbackRebased) != 0)
            return; // already spliced for this outage - the fast path for every subsequent fallback read
        lock (_epochGate)
        {
            if (_fallbackRebased != 0)
                return; // lost the race; the winner wrote the epoch before it published the flag
            FallbackSpliceUnderGateForTest?.Invoke();
            Volatile.Write(ref _fallbackEpochTicks,
                _fallbackElapsed.Elapsed.Ticks - Volatile.Read(ref _maxSinceEpochTicks));
            Volatile.Write(ref _fallbackRebased, 1);
        }
    }

    /// <summary>
    /// Test seam: runs under <see cref="_epochGate"/> before the outage splice writes the fallback
    /// epoch (and before the flag is published). Lets a test hold the first faller inside the
    /// transition and prove a concurrent reader cannot observe the pre-splice epoch - the exact
    /// interleaving of the flag-before-epoch bug this method's remarks describe. Never set in
    /// production; the null-conditional invoke costs one branch on the (already rare) outage path.
    /// </summary>
    internal Action? FallbackSpliceUnderGateForTest;

    /// <summary>
    /// Maps raw since-epoch ticks to this client's <em>audible</em> position. The raw reading leads the
    /// speaker by everything still in flight (the injected lead measurement); the subtraction eases in
    /// quadratically over the first <c>2×lead</c> of a segment
    /// (<see cref="AudioLatencyCompensation.AudibleSeconds"/>, C¹-continuous) so a fresh client leaves
    /// zero smoothly instead of holding zero and jumping. A CAS high-water keeps the report monotonic
    /// when the estimate grows - the clock holds until the raw reading catches up - and is cleared by
    /// the same deliberate re-anchors that reset the epoch (attach, <see cref="Reanchor"/>).
    /// </summary>
    /// <remarks>
    /// The lead is <em>low-passed</em> before it is subtracted (<see cref="SmoothLeadTicks"/>). Its
    /// terms are instantaneous queue depths that swing hard at production sizes, and feeding an
    /// instantaneous depth into the monotonic high-water below turns the report into
    /// <c>max(raw − lead)</c> ≈ <c>raw − min(lead)</c> over a trailing window. Smoothing makes the
    /// steady-state subtraction the <em>mean</em> lead, while the high-water still guarantees the
    /// report never steps backwards.
    /// </remarks>
    private TimeSpan ReportAudible(long rawTicks)
    {
        long audibleTicks = 0;
        if (rawTicks > 0)
        {
            var measuredLeadTicks = _measuredLeadTicks();
            var leadTicks = SmoothLeadTicks(rawTicks, measuredLeadTicks);
            audibleTicks = (long)(AudioLatencyCompensation.AudibleSeconds(
                rawTicks / (double)TimeSpan.TicksPerSecond,
                leadTicks / (double)TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond);
        }

        return new TimeSpan(RaiseHighWater(ref _maxAudibleTicks, audibleTicks));
    }

    /// <summary>
    /// Integrates one lead measurement into the exponential low-pass and returns the value to subtract.
    /// The filter's time base is the RAW clock itself, not wall time: raw is device time (or the
    /// stopwatch domain in the fallback), so the response is a real <see cref="LeadSmoothingSeconds"/>
    /// time constant in production <em>and</em> fully deterministic under a test harness that advances a
    /// fake terminal. Repeated reads without clock progress cannot move the estimate, and a paused
    /// device freezes it instead of decaying it toward a queue depth nobody is draining.
    /// </summary>
    private long SmoothLeadTicks(long rawTicks, long measuredLeadTicks)
    {
        lock (_epochGate)
        {
            var sampledAt = _leadSampledAtRawTicks;
            _leadSampledAtRawTicks = rawTicks;
            if (sampledAt < 0)
            {
                // First reading of a segment: adopt the measurement so the quadratic ease-in starts
                // from the real lead instead of ramping the subtraction itself up from zero.
                _leadTicks = measuredLeadTicks;
                return measuredLeadTicks;
            }

            var elapsedTicks = rawTicks - sampledAt;
            if (elapsedTicks <= 0)
                return _leadTicks; // no clock progress - the filter has nothing to integrate

            var alpha = 1 - Math.Exp(-elapsedTicks / (LeadSmoothingSeconds * TimeSpan.TicksPerSecond));
            _leadTicks += (long)Math.Round((measuredLeadTicks - _leadTicks) * alpha);
            return _leadTicks;
        }
    }

    /// <summary>Lock-free CAS max; returns the resulting maximum.</summary>
    private static long RaiseHighWater(ref long field, long value)
    {
        var seen = Volatile.Read(ref field);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref field, value, seen);
            if (previous == seen)
                return value;
            seen = previous;
        }

        return seen;
    }

    /// <summary>Re-derives the epoch after the terminal announced a new one: epoch = terminal-now −
    /// high-water mark, so the client clock continues monotonically from the furthest position it ever
    /// reported rather than freezing. Returns the recovered elapsed ticks. Serialized so concurrent
    /// readers re-derive once; a deliberate re-anchor (attach/<see cref="Reanchor"/>) that raced in
    /// first wins - its captured terminal epoch already matches on the re-check.</summary>
    private long RecoverFromTerminalReanchor(ClockReading terminal)
    {
        lock (_epochGate)
        {
            if (terminal.EpochId == Volatile.Read(ref _terminalEpochId))
                return terminal.Elapsed.Ticks - Volatile.Read(ref _terminalEpochTicks); // already fixed
            var resume = Volatile.Read(ref _maxSinceEpochTicks);
            Volatile.Write(ref _terminalEpochTicks, terminal.Elapsed.Ticks - resume);
            Volatile.Write(ref _terminalEpochId, terminal.EpochId);
            return resume;
        }
    }

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: mirrors the terminal's; wall-clock fallback
    /// always advances. Every branch matches <see cref="ReadRaw"/>'s advancing flag exactly - including
    /// the throwing one. A terminal that throws mid-read is not a stopped clock, it is a clock this
    /// client can no longer see, and <see cref="ReadRaw"/> answers that by degrading to the stopwatch
    /// domain (which advances). This member stays free of <see cref="ReadRaw"/>'s side effects
    /// (high-waters, the DAC-lead filter) - it decides advancing only.</summary>
    public bool IsAdvancing
    {
        get
        {
            if (Volatile.Read(ref _stopped) != 0)
                return false;
            if (_terminalClock is null)
                return true;
            try
            {
                return _terminalClock.IsAdvancing;
            }
            catch
            {
                return true; // == ReadRaw's fallback branch: the stopwatch domain always advances
            }
        }
    }

    /// <summary>Re-captures both epoch baselines so <see cref="ElapsedSinceStart"/> restarts at zero, and
    /// takes a fresh <see cref="EpochId"/> - this IS the client clock's epoch boundary (attach and Flush).</summary>
    public void Reanchor()
    {
        lock (_epochGate)
        {
            Volatile.Write(ref _fallbackEpochTicks, _fallbackElapsed.Elapsed.Ticks);
            Volatile.Write(ref _fallbackRebased, 0);
            // A deliberate reset-to-zero clears both resume points (re-anchor recovery, monotonic report).
            Volatile.Write(ref _maxSinceEpochTicks, 0);
            Volatile.Write(ref _maxAudibleTicks, 0);
            Volatile.Write(ref _epochId, PlaybackEpoch.Next());
            // ...and re-seeds the lead filter: the raw domain it integrates over just restarted, and the
            // pre-flush queue depths say nothing about the segment that is about to start.
            _leadTicks = 0;
            _leadSampledAtRawTicks = -1;
            if (_terminalClock is null)
                return;
            try
            {
                // Both halves from one reading: an epoch captured apart from its elapsed would make the
                // very next read look like an unannounced re-anchor.
                var terminal = _terminalClock.Read();
                Volatile.Write(ref _terminalEpochTicks, terminal.Elapsed.Ticks);
                Volatile.Write(ref _terminalEpochId, terminal.EpochId);
            }
            catch
            {
                // Keep the previous baseline; reads degrade to the stopwatch domain anyway.
            }
        }
    }
}
