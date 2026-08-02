using S.Media.Core.Audio;

namespace S.Media.Routing;

/// <summary>
/// The persistent project audio patch bay (HaCue extraction plan, "Target audio runtime"): owns one
/// <see cref="AudioRouter"/> at the project mix rate and one V-wide <see cref="ProgramBusSource"/>,
/// and is the ONLY producer into its real terminal outputs (resolved decision 3 - a patched line is
/// held exclusively, e.g. through <c>HaOutput</c>'s raw-terminal lease, never layered on the shared
/// fan-in mixer). Program voices acquire isolated bounded leases into the bus (N×V sends); each
/// terminal receives the ONE bus source through a dense V×R patch matrix - the P + R program-sum
/// topology whose cost the Phase 0 benchmarks measured.
/// <para>Live semantics come from the router's matrix reconciliation: patch updates apply in a
/// single state swap with one-chunk fades (changed cells ramp, new cells fade in, zeroed cells
/// stop), and never touch a producer, so a patch edit cannot interrupt a running voice. Terminals
/// add/remove while the bay runs.</para>
/// <para>Rates and the clock (plan "Clock policy"): a terminal whose native rate differs from the
/// mix rate is wrapped through the injected resampler factory; the CLOCK MASTER is never wrapped -
/// a master that cannot open natively at the mix rate is a named validation failure, because the
/// resampling wrapper does not report its own internal delay and would skew the program clock
/// silently. Terminal outputs are BORROWED (the host's lease owns the device); the bay disposes
/// only the resampler and adaptive-rate wrappers it created.</para>
/// </summary>
public sealed class AudioPatchBay : IDisposable
{
    private const int ChunkSamples = 480;

    private readonly AudioRouter _router;
    private readonly ProgramBusSource _bus;
    private readonly string _busId;
    private readonly int _mixSampleRate;
    private readonly Func<IAudioOutput, AudioFormat, IAudioOutput>? _resamplerFactory;
    private readonly AdaptiveRateOutputWrapper? _adaptiveRateWrapper;
    private readonly int _adaptiveRateMaxDeltaHz;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TerminalEntry> _terminals = new(StringComparer.Ordinal);
    // The clock-master triple the producer clocks read through the proxy/lead delegates. Volatile:
    // written on terminal add/remove (control plane), read from every clock read (hot path).
    private volatile IPlaybackClock? _masterTerminalClock;
    private volatile IAudioOutput? _masterTerminal;
    private volatile string? _masterTerminalId;
    private bool _disposed;

    private sealed record TerminalEntry(
        IAudioOutput Terminal,
        IAudioOutput Routed,
        bool IsClockMaster,
        float[,] Patch);

    private sealed record PreparedTerminal(IAudioOutput Routed, IDisposable? Cleanup);

    /// <param name="logicalChannels">V - the project's logical channel count.</param>
    /// <param name="mixSampleRate">The fixed project mix rate (plan decision 7).</param>
    /// <param name="resamplerFactory">Wraps a terminal at a foreign native rate to accept audio at
    /// the mix rate (the neutral seam for <c>ResamplingAudioOutput.Wrap</c>, which lives in the
    /// FFmpeg module). Null = foreign-rate terminals are rejected with a named error.</param>
    /// <param name="producerRingFrames">Per-producer bounded ring capacity in frames.</param>
    /// <param name="adaptiveRateWrapper">Optional registry-wired adaptive-rate wrapper. When set,
    /// every non-master terminal is wrapped for independent hardware-clock drift correction. The
    /// clock master is always attached directly at the project rate.</param>
    /// <param name="adaptiveRateMaxDeltaHz">Maximum adaptive correction in Hz.</param>
    public AudioPatchBay(
        int logicalChannels,
        int mixSampleRate,
        Func<IAudioOutput, AudioFormat, IAudioOutput>? resamplerFactory = null,
        int producerRingFrames = 4800,
        AdaptiveRateOutputWrapper? adaptiveRateWrapper = null,
        int adaptiveRateMaxDeltaHz = 3)
    {
        if (adaptiveRateMaxDeltaHz < 0)
            throw new ArgumentOutOfRangeException(nameof(adaptiveRateMaxDeltaHz));
        _mixSampleRate = mixSampleRate;
        _router = new AudioRouter(mixSampleRate, ChunkSamples);
        // The bay owns the clock policy explicitly. Letting AudioRouter auto-promote on removal can
        // silently turn a rate-adapted secondary into the pace master while the bay reports no master.
        _router.AutoWirePrimary = false;
        // Producer leases rebase their clocks from whatever terminal is CURRENTLY the clock master,
        // through this late-bound proxy: no master (yet, or after removal) degrades every producer
        // clock to the wall-clock fallback domain, and a master appearing later is picked up as an
        // announced re-anchor - both behaviors inherited from the extracted AudibleClientClock.
        _bus = new ProgramBusSource(
            logicalChannels,
            mixSampleRate,
            producerRingFrames,
            new ProgramBusClockContext(new MasterClockProxy(this), DownstreamLeadTicks));
        _busId = _router.AddSource(_bus, "program-bus");
        _resamplerFactory = resamplerFactory;
        _adaptiveRateWrapper = adaptiveRateWrapper;
        _adaptiveRateMaxDeltaHz = adaptiveRateMaxDeltaHz;
    }

    public int LogicalChannels => _bus.BusChannels;

    public int MixSampleRate => _bus.Format.SampleRate;

    public int TerminalCount
    {
        get { lock (_gate) return _terminals.Count; }
    }

    public int ProducerCount => _bus.ProducerCount;

    /// <summary>
    /// Turns per-logical-output metering on or off. Off by default: a host that never displays levels
    /// (headless runners, tests, the C ABI) pays nothing. Returns the meter so the caller can snapshot
    /// it; calling it again returns the existing meter rather than restarting the measurement.
    /// </summary>
    /// <remarks>Levels are measured on the program sum, which is the only place a logical output
    /// exists as audio - after this, the V×R patch fans each channel to N terminals, where the same
    /// signal would be counted once per terminal it reaches.</remarks>
    public ProgramBusMeter EnableProgramMetering()
    {
        lock (_gate)
        {
            var existing = _bus.Meter;
            if (existing is not null)
                return existing;
            var meter = new ProgramBusMeter(_bus.BusChannels, _bus.Format.SampleRate);
            _bus.Meter = meter;
            return meter;
        }
    }

    /// <summary>Stops metering and drops the meter. Safe while running.</summary>
    public void DisableProgramMetering()
    {
        lock (_gate)
            _bus.Meter = null;
    }

    /// <summary>The active program meter, or null when metering has not been enabled.</summary>
    public ProgramBusMeter? ProgramMeter => _bus.Meter;

    /// <summary>
    /// A whole-bay diagnostics snapshot: every terminal with its full pump counters and state, every
    /// producer lease with its input-side counters, and the program levels when metering is on.
    /// </summary>
    /// <remarks>
    /// Two gaps this closes. The counters existed but were being thrown away - the session-level query
    /// returned only enqueued and dropped per device, discarding processed, abandoned, evictions,
    /// in-flight and capacity. And the <b>input</b> side had no counters exposed at all, so "which lease
    /// is starving the bus" was unanswerable; that is what <see cref="ProducerDiagnostics"/> is for.
    /// <para>Cheap enough for a 1 Hz poll and safe from any thread. Counters are monotonic by design -
    /// a "since" baseline belongs to the caller, which is how the existing stats views already work.</para>
    /// </remarks>
    public AudioPatchBayDiagnostics SnapshotDiagnostics()
    {
        var quarantined = _router.StuckOutputPumpIds;
        var terminals = new List<TerminalDiagnostics>();
        string? masterId;

        lock (_gate)
        {
            masterId = _masterTerminalId;
            foreach (var (id, entry) in _terminals)
            {
                _router.TryGetPumpStats(id, out var stats);
                var inFlight = stats.Enqueued - stats.Processed - stats.Dropped - stats.Abandoned;
                var isMaster = string.Equals(id, masterId, StringComparison.Ordinal);
                var state = quarantined.Contains(id) || stats.IsStuck ? TerminalState.Quarantined
                    : stats.PumpCapacityChunks > 0 && inFlight >= stats.PumpCapacityChunks ? TerminalState.Behind
                    : isMaster ? TerminalState.AdvancingMaster
                    : TerminalState.Open;

                terminals.Add(new TerminalDiagnostics(
                    id, state, entry.Terminal.Format.Channels, entry.Terminal.Format.SampleRate,
                    isMaster, stats, Math.Max(0, inFlight)));
            }
        }

        var producers = _bus.SnapshotProducers();
        var levels = _bus.Meter is { } meter ? meter.Snapshot() : [];

        return new AudioPatchBayDiagnostics(
            MixSampleRate, LogicalChannels, masterId, terminals, producers, levels);
    }

    /// <summary>A terminal's pump failed submitting (device fault). The terminal keeps its patch;
    /// the router's failure isolation keeps every other terminal running.</summary>
    public event EventHandler<AudioRouterOutputErrorEventArgs>? TerminalErrored
    {
        add => _router.OutputErrored += value;
        remove => _router.OutputErrored -= value;
    }

    public event EventHandler<AudioRouterPumpPressureEventArgs>? TerminalPressure
    {
        add => _router.PumpPressure += value;
        remove => _router.PumpPressure -= value;
    }

    /// <summary>The pacing clock itself failed (typically the clock-master terminal died). First
    /// release policy per the plan: stop/fault visibly, never silently fall back to a default device.</summary>
    public event EventHandler<AudioRouterFaultedEventArgs>? Faulted
    {
        add => _router.Faulted += value;
        remove => _router.Faulted -= value;
    }

    /// <summary>
    /// Attaches a real terminal with its V×R patch (<c>patch[logicalChannel, realChannel]</c>).
    /// Safe while running - the terminal joins with a fade-in. The terminal output is BORROWED;
    /// remove it (or dispose the bay) before the host releases the underlying line.
    /// </summary>
    public void AddTerminal(string terminalId, IAudioOutput terminal, float[,] patch, bool isClockMaster = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(terminalId);
        ArgumentNullException.ThrowIfNull(terminal);
        ValidatePatch(patch, terminal.Format.Channels);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminals.ContainsKey(terminalId))
                throw new ArgumentException($"terminal '{terminalId}' already exists", nameof(terminalId));
            if (isClockMaster && _terminals.Values.Any(t => t.IsClockMaster))
                throw new InvalidOperationException("the bay already has a clock-master terminal");
            ValidateRate(terminalId, terminal, isClockMaster);
            var prepared = PrepareTerminal(terminalId, terminal, isClockMaster);
            AttachLocked(terminalId, terminal, prepared, patch, isClockMaster);
        }
    }

    /// <summary>
    /// Hot-swaps a terminal: detaches the line currently registered as <paramref name="terminalId"/>
    /// and attaches <paramref name="newTerminal"/> under the same id with the same patch (or
    /// <paramref name="patch"/> when given) and the same clock-master role. This is the recovery path
    /// for a WEDGED line (HaCue plan "terminal quarantine + hot-swap"): a drainer stuck in a native
    /// <c>Submit</c> is quarantined off the audio thread (its pump state is leaked and
    /// <see cref="TerminalErrored"/> reports it), so the swap never stalls the mix and never touches
    /// another terminal or a producer. Driven by health events - <see cref="TerminalErrored"/>,
    /// <see cref="TerminalPressure"/>, <see cref="TryGetTerminalStats"/> - not by silence.
    /// <para>The replacement is validated BEFORE the old line is detached: a failed swap throws and
    /// leaves the old terminal attached and flowing. Monitor inputs follow the stable terminal id across
    /// the swap. If the old
    /// line was the clock master, producer clocks ride the wall-clock fallback for the swap gap and
    /// re-anchor to the new master's clock on its first read.</para>
    /// </summary>
    public void ReplaceTerminal(string terminalId, IAudioOutput newTerminal, float[,]? patch = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(terminalId);
        ArgumentNullException.ThrowIfNull(newTerminal);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_terminals.TryGetValue(terminalId, out var old))
                throw new ArgumentException($"unknown terminal '{terminalId}'", nameof(terminalId));
            var newPatch = patch ?? old.Patch;
            ValidatePatch(newPatch, newTerminal.Format.Channels);
            ValidateRate(terminalId, newTerminal, old.IsClockMaster);

            // Factories and format checks can fail. Complete all of them before touching the live
            // route so ordinary replacement failures genuinely leave the old terminal flowing.
            var prepared = PrepareTerminal(terminalId, newTerminal, old.IsClockMaster);

            try
            {
                _router.ReplaceOutputKeepingRoutes(terminalId, prepared.Routed, prepared.Cleanup);
            }
            catch
            {
                prepared.Cleanup?.Dispose();
                throw;
            }

            _router.ApplyMatrix(_busId, terminalId, newPatch);
            _terminals[terminalId] = new TerminalEntry(
                newTerminal, prepared.Routed, old.IsClockMaster, (float[,])newPatch.Clone());
            if (old.IsClockMaster)
            {
                _masterTerminal = prepared.Routed;
                _masterTerminalClock = prepared.Routed as IPlaybackClock;
            }
        }
    }

    /// <summary>Output lines whose pump wedged in a native <c>Submit</c> and was leaked
    /// (quarantined). A successful <see cref="ReplaceTerminal"/> under the same id clears the id.</summary>
    public IReadOnlyList<string> QuarantinedTerminalIds => _router.StuckOutputPumpIds;

    /// <summary>The terminal currently pacing the router, or null when the bay has no clock master
    /// (producer clocks then ride the wall-clock fallback domain).</summary>
    public string? ClockMasterTerminalId => _masterTerminalId;

    /// <summary>
    /// Moves the clock-master role to an already-attached terminal without replacing any device.
    /// Running-safe: pacing follows the new master from its next chunk, no producer is interrupted,
    /// and no terminal is detached.
    /// </summary>
    /// <remarks>
    /// This is the cheap half of surviving a bad master. <see cref="ReplaceTerminal"/> needs the host
    /// to supply a working replacement device, which is exactly what it cannot do when an interface has
    /// wedged mid-show; promoting a line the bay already owns needs nothing from the host and is
    /// inaudible, because the program mix is unchanged - only which line the mix loop paces against.
    /// </remarks>
    /// <exception cref="ArgumentException">No such terminal, or it is not an <see cref="IClockedOutput"/>
    /// and so cannot pace at all.</exception>
    /// <exception cref="InvalidOperationException">The terminal does not run natively at the mix rate.
    /// A resampled master would skew the program clock silently (the wrapper does not report its own
    /// delay), so promotion is refused for exactly the reason attaching one is - see
    /// <see cref="AddTerminal"/>.</exception>
    public void PromoteClockMaster(string terminalId)
    {
        ArgumentException.ThrowIfNullOrEmpty(terminalId);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_terminals.TryGetValue(terminalId, out var promoted))
                throw new ArgumentException($"unknown terminal '{terminalId}'", nameof(terminalId));
            if (string.Equals(terminalId, _masterTerminalId, StringComparison.Ordinal))
                return;
            ValidateRate(terminalId, promoted.Terminal, isClockMaster: true);
            if (promoted.Terminal is not IClockedOutput)
                throw new ArgumentException(
                    $"terminal '{terminalId}' cannot pace the bay: it does not implement IClockedOutput.",
                    nameof(terminalId));
            PromoteClockMasterLocked(terminalId);
        }
    }

    /// <summary>Master handoff proper; callers hold <see cref="_gate"/> and have validated the target.</summary>
    private void PromoteClockMasterLocked(string terminalId)
    {
        var promoted = _terminals[terminalId];

        // With adaptive drift correction enabled, clock role is part of the wrapper topology: the
        // promoted line must become direct/native, and the old master becomes an adaptive secondary.
        // Both chains are staged before either live output changes; router replacement preserves program
        // and monitor routes under their stable output ids.
        if (_adaptiveRateWrapper is not null)
        {
            var promotedPrepared = PrepareTerminal(terminalId, promoted.Terminal, isClockMaster: true);
            PreparedTerminal? demotedPrepared = null;
            TerminalEntry? oldMaster = null;
            var oldMasterId = _masterTerminalId;
            try
            {
                if (oldMasterId is not null && _terminals.TryGetValue(oldMasterId, out oldMaster))
                    demotedPrepared = PrepareTerminal(oldMasterId, oldMaster.Terminal, isClockMaster: false);
            }
            catch
            {
                promotedPrepared.Cleanup?.Dispose();
                throw;
            }

            try
            {
                _router.ReplaceOutputKeepingRoutes(terminalId, promotedPrepared.Routed, promotedPrepared.Cleanup);
            }
            catch
            {
                promotedPrepared.Cleanup?.Dispose();
                demotedPrepared?.Cleanup?.Dispose();
                throw;
            }
            _terminals[terminalId] = promoted with
            {
                Routed = promotedPrepared.Routed,
                IsClockMaster = true,
            };

            _router.RetargetSlaveClock(terminalId);

            if (oldMasterId is not null && oldMaster is not null && demotedPrepared is not null)
            {
                try
                {
                    _router.ReplaceOutputKeepingRoutes(
                        oldMasterId, demotedPrepared.Routed, demotedPrepared.Cleanup);
                    _terminals[oldMasterId] = oldMaster with
                    {
                        Routed = demotedPrepared.Routed,
                        IsClockMaster = false,
                    };
                }
                catch
                {
                    demotedPrepared.Cleanup?.Dispose();
                    throw;
                }
            }

            _masterTerminal = promotedPrepared.Routed;
            _masterTerminalClock = promotedPrepared.Routed as IPlaybackClock;
            _masterTerminalId = terminalId;
            return;
        }

        // Retarget first: if it throws, the old master is still installed and still pacing.
        _router.RetargetSlaveClock(terminalId);

        if (_masterTerminalId is { } oldId && _terminals.TryGetValue(oldId, out var old))
            _terminals[oldId] = old with { IsClockMaster = false };

        _terminals[terminalId] = promoted with { IsClockMaster = true };
        _masterTerminal = promoted.Routed;
        _masterTerminalClock = promoted.Routed as IPlaybackClock;
        _masterTerminalId = terminalId;
    }

    /// <summary>
    /// Hands pacing to the healthiest eligible terminal, used by <see cref="ClockMasterWatchdog"/> when
    /// the current master stalls. "Healthiest" is the eligible line with the shallowest queue - the one
    /// most obviously still draining. Returns false when nothing else can pace, which is the honest
    /// unrecoverable case the plan keeps as a fallback rather than papering over.
    /// </summary>
    public bool TryPromoteHealthiestClockMaster(out string? promotedTerminalId)
    {
        lock (_gate)
        {
            promotedTerminalId = null;
            if (_disposed)
                return false;

            var best = default(string);
            var bestInFlight = long.MaxValue;
            foreach (var id in EligibleClockMastersLocked())
            {
                // An eligible line with no stats yet has never pumped; treat it as empty rather than
                // skipping it, since a fresh line is a perfectly good pacer.
                var inFlight = _router.TryGetPumpStats(id, out var stats)
                    ? stats.Enqueued - stats.Processed - stats.Dropped - stats.Abandoned
                    : 0;
                if (inFlight >= bestInFlight)
                    continue;
                bestInFlight = inFlight;
                best = id;
            }

            if (best is null)
                return false;

            PromoteClockMasterLocked(best);
            promotedTerminalId = best;
            return true;
        }
    }

    /// <summary>Terminals eligible to take over pacing: attached, natively at the mix rate, clocked,
    /// not the current master, and not quarantined.</summary>
    private List<string> EligibleClockMastersLocked()
    {
        var quarantined = _router.StuckOutputPumpIds;
        var eligible = new List<string>();
        foreach (var (id, entry) in _terminals)
        {
            if (entry.IsClockMaster || quarantined.Contains(id))
                continue;
            if (entry.Terminal.Format.SampleRate != MixSampleRate || entry.Terminal is not IClockedOutput)
                continue;
            eligible.Add(id);
        }
        return eligible;
    }

    /// <summary>Rate half of terminal validation, shared by add and replace. Throws the named
    /// errors the plan requires; never wraps here (wrapping happens in <see cref="PrepareTerminal"/>).</summary>
    private void ValidateRate(string terminalId, IAudioOutput terminal, bool isClockMaster)
    {
        if (terminal.Format.SampleRate == MixSampleRate)
            return;
        if (isClockMaster)
            throw new InvalidOperationException(
                $"clock-master terminal '{terminalId}' must open natively at the project mix rate " +
                $"{MixSampleRate} Hz but reports {terminal.Format.SampleRate} Hz - a resampled master " +
                "would skew the program clock silently (the wrapper does not report its own delay). " +
                "Choose a master that supports the project rate, or change the project rate.");
        if (_resamplerFactory is null)
            throw new InvalidOperationException(
                $"terminal '{terminalId}' runs at {terminal.Format.SampleRate} Hz but the bay mixes at " +
                $"{MixSampleRate} Hz and no resampler factory was provided.");
    }

    /// <summary>Builds the complete wrapper chain without changing live router state.</summary>
    private PreparedTerminal PrepareTerminal(string terminalId, IAudioOutput terminal, bool isClockMaster)
    {
        var routed = terminal;
        var owned = new List<IDisposable>(2);
        try
        {
            if (terminal.Format.SampleRate != MixSampleRate)
            {
                var wrapped = _resamplerFactory!(terminal, new AudioFormat(MixSampleRate, terminal.Format.Channels));
                if (wrapped is null)
                    throw new InvalidOperationException($"resampler factory returned null for terminal '{terminalId}'");
                routed = wrapped;
                if (!ReferenceEquals(routed, terminal) && routed is IDisposable disposable)
                    owned.Insert(0, disposable);
            }

            if (!isClockMaster && _adaptiveRateWrapper is not null)
            {
                var wrapped = _adaptiveRateWrapper(_router, routed, terminalId, _adaptiveRateMaxDeltaHz);
                if (wrapped is null)
                    throw new InvalidOperationException($"adaptive-rate factory returned null for terminal '{terminalId}'");
                if (!ReferenceEquals(wrapped, routed) && wrapped is IDisposable disposable)
                    owned.Insert(0, disposable);
                routed = wrapped;
            }

            routed.Format.Validate(nameof(terminal));
            if (routed.Format.SampleRate != MixSampleRate || routed.Format.Channels != terminal.Format.Channels)
                throw new InvalidOperationException(
                    $"terminal wrapper for '{terminalId}' reports {routed.Format}; expected " +
                    $"{MixSampleRate} Hz and {terminal.Format.Channels} channels");

            return new PreparedTerminal(routed, owned.Count == 0 ? null : new OwnedWrapperChain(owned));
        }
        catch
        {
            foreach (var wrapper in owned)
            {
                try { wrapper.Dispose(); }
                catch { /* preserve the factory/validation failure */ }
            }
            throw;
        }
    }

    /// <summary>Wires a fully prepared terminal into the router and records its entry.</summary>
    private void AttachLocked(
        string terminalId,
        IAudioOutput terminal,
        PreparedTerminal prepared,
        float[,] patch,
        bool isClockMaster)
    {
        var added = false;
        var cleanupRegistered = false;

        try
        {
            // The bay already applied the exact role-aware wrapper chain; suppress the router's
            // generic adaptive hook so a master can never be wrapped and a secondary cannot be doubled.
            _router.AddOutput(prepared.Routed, terminalId, adaptiveRateEligible: false);
            added = true;
            if (prepared.Cleanup is not null)
            {
                _router.RegisterOutputCleanup(terminalId, prepared.Cleanup);
                cleanupRegistered = true;
            }
            _router.ApplyMatrix(_busId, terminalId, patch);
            if (isClockMaster)
                _router.RetargetSlaveClock(terminalId);
        }
        catch
        {
            if (added)
            {
                if (cleanupRegistered)
                    _router.RemoveOutput(terminalId);
                else
                    _router.RemoveOutputAndDispose(terminalId, prepared.Cleanup);
            }
            else
                prepared.Cleanup?.Dispose();
            throw;
        }

        _terminals[terminalId] = new TerminalEntry(
            terminal, prepared.Routed, isClockMaster, (float[,])patch.Clone());
        if (isClockMaster)
        {
            // The master is never wrapped, so routed == terminal here; producer clocks follow it.
            _masterTerminal = prepared.Routed;
            _masterTerminalClock = prepared.Routed as IPlaybackClock;
            _masterTerminalId = terminalId;
        }
    }

    /// <summary>
    /// Replaces a terminal's V×R patch in one atomic reconciliation: changed cells ramp over the
    /// next chunk, newly non-zero cells fade in, zeroed cells stop. Producers are untouched - the
    /// live-patch-edit path the plan requires cannot interrupt a running voice.
    /// </summary>
    public void UpdatePatch(string terminalId, float[,] patch)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_terminals.TryGetValue(terminalId, out var entry))
                throw new ArgumentException($"unknown terminal '{terminalId}'", nameof(terminalId));
            ValidatePatch(patch, entry.Routed.Format.Channels);
            _router.ApplyMatrix(_busId, terminalId, patch);
            // Remember the live patch so a later ReplaceTerminal re-applies what is audible now.
            _terminals[terminalId] = entry with { Patch = (float[,])patch.Clone() };
        }
    }

    /// <summary>Detaches a terminal (patch routes die with it; other terminals unaffected). The
    /// borrowed terminal output is NOT disposed - only a resampler wrapper the bay created is.</summary>
    public bool RemoveTerminal(string terminalId)
    {
        TerminalEntry? entry;
        lock (_gate)
        {
            if (!_terminals.Remove(terminalId, out entry))
                return false;
            if (entry.IsClockMaster)
            {
                // Master loss is VISIBLE (plan first-release policy): producer clocks degrade to the
                // wall-clock fallback domain from the next read; the router surfaces the pacing fault.
                _masterTerminalClock = null;
                _masterTerminal = null;
                _masterTerminalId = null;
            }
        }

        _router.RemoveOutput(terminalId);
        return true;
    }

    /// <summary>Registers a program voice into the bus - an isolated bounded lease carrying the
    /// voice's N×V send matrix (<see cref="ProgramBusProducer.UpdateSends"/> is its live fade path).</summary>
    /// <param name="label">Optional name for diagnostics (a cue label, say). Without it a
    /// <see cref="SnapshotDiagnostics"/> row can report that a lease is starving the bus but not which.</param>
    public ProgramBusProducer AcquireProducer(
        int sourceChannels, ReadOnlySpan<float> sends, string? label = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bus.AcquireProducer(sourceChannels, sends, label);
        }
    }

    /// <summary>
    /// Direct MONITORING input to one terminal (HaCue plan: preview/audition is monitoring). The
    /// returned input routes to <paramref name="terminalId"/> only, bypassing the program bus and
    /// the V×R patch entirely - so auditioning through a bay-owned line never double-opens the
    /// device and never leaks into the program mix, and a patch/program edit cannot affect it.
    /// <paramref name="mix"/> is <c>[sourceChannel, terminalChannel]</c>. The lease's clock follows
    /// the MONITORED terminal when it exposes one (else the advancing wall-clock fallback); its
    /// reported latency covers its own backlog plus that terminal's pump and device latency.
    /// Dispose the lease to detach; removing the terminal strands the monitor silently (routes die
    /// with the terminal) until the lease is disposed.
    /// </summary>
    public AudioMonitorLease AcquireMonitorInput(string terminalId, int sourceChannels, float[,] mix)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_terminals.TryGetValue(terminalId, out var entry))
                throw new ArgumentException($"unknown terminal '{terminalId}'", nameof(terminalId));
            var terminalChannels = entry.Routed.Format.Channels;
            if (mix.GetLength(0) > sourceChannels || mix.GetLength(1) > terminalChannels)
                throw new ArgumentException(
                    $"mix is {mix.GetLength(0)}x{mix.GetLength(1)} but the monitor source has {sourceChannels} " +
                    $"channels and terminal '{terminalId}' has {terminalChannels}", nameof(mix));
            foreach (var gain in mix)
            {
                if (!float.IsFinite(gain))
                    throw new ArgumentException("mix gains must be finite", nameof(mix));
            }

            // A private single-producer bus at the TERMINAL's width, routed to that terminal alone.
            // Reuses the program-bus machinery wholesale (bounded ring, click-free send updates, the
            // extracted audible clock) without touching the shared program bus.
            var monitorId = $"monitor-{++_monitorCounter}";
            var monitorBus = new ProgramBusSource(
                terminalChannels,
                MixSampleRate,
                clockContext: new ProgramBusClockContext(
                    new TerminalClockProxy(this, terminalId),
                    () => TerminalLeadTicks(terminalId)));
            ProgramBusProducer input;
            try
            {
                // The producer's N×(terminal width) sends ARE the monitor mix.
                var sends = new float[terminalChannels * sourceChannels];
                for (var srcChannel = 0; srcChannel < mix.GetLength(0); srcChannel++)
                    for (var dstChannel = 0; dstChannel < mix.GetLength(1); dstChannel++)
                        sends[dstChannel * sourceChannels + srcChannel] = mix[srcChannel, dstChannel];
                input = monitorBus.AcquireProducer(sourceChannels, sends);
                _router.AddSource(monitorBus, monitorId);
            }
            catch
            {
                monitorBus.Dispose();
                throw;
            }

            try
            {
                _router.AddRoute(monitorId, terminalId, ChannelMap.Identity(terminalChannels));
            }
            catch
            {
                _router.RemoveSource(monitorId);
                monitorBus.Dispose();
                throw;
            }

            _monitorBuses.Add(monitorId, monitorBus);
            return new AudioMonitorLease(input, () =>
            {
                lock (_gate)
                {
                    _monitorBuses.Remove(monitorId);
                    if (!_disposed)
                        _router.RemoveSource(monitorId); // removes its route atomically
                }

                monitorBus.Dispose();
            });
        }
    }

    private long _monitorCounter;
    // Live monitor buses, so bay disposal revokes their producer leases exactly like program leases
    // (the same atomic-invalidation contract the program bus got in review finding #2).
    private readonly Dictionary<string, ProgramBusSource> _monitorBuses = new(StringComparer.Ordinal);

    /// <summary>Lead between a monitor input and ONE terminal's speaker: that terminal's pump
    /// in-flight plus its reported device latency. Hot-path safe like the master variant.</summary>
    private long TerminalLeadTicks(string terminalId)
    {
        IAudioOutput? terminal;
        lock (_gate)
            terminal = _terminals.GetValueOrDefault(terminalId)?.Routed;
        long ticks = 0;
        if (_router.TryGetPumpStats(terminalId, out var pump))
            ticks = pump.InFlight * (long)ChunkSamples * TimeSpan.TicksPerSecond / _mixSampleRate;
        return ticks + (terminal is null ? 0 : AudioOutputLatency.Of(terminal).Ticks);
    }

    private sealed class TerminalClockProxy(AudioPatchBay bay, string terminalId) : IPlaybackClock
    {
        private IPlaybackClock Inner
        {
            get
            {
                lock (bay._gate)
                    return bay._terminals.TryGetValue(terminalId, out var entry)
                           && entry.Routed is IPlaybackClock clock
                        ? clock
                        : throw new InvalidOperationException(
                            $"terminal '{terminalId}' exposes no playback clock");
            }
        }

        public TimeSpan ElapsedSinceStart => Inner.ElapsedSinceStart;
        public long EpochId => Inner.EpochId;
        public bool IsAdvancing => Inner.IsAdvancing;
        public ClockReading Read() => Inner.Read();
    }

    /// <summary>Per-terminal pump health (queue depth, drops, submit failures).</summary>
    public bool TryGetTerminalStats(string terminalId, out AudioRouter.OutputPumpStats stats) =>
        _router.TryGetPumpStats(terminalId, out stats);

    /// <summary>The NATIVE format of a registered terminal (device truth - the rate may differ from
    /// the mix rate when the bay resampler-wraps it). False for an unknown id.</summary>
    public bool TryGetTerminalFormat(string terminalId, out AudioFormat format)
    {
        lock (_gate)
        {
            if (_terminals.TryGetValue(terminalId, out var entry))
            {
                format = entry.Terminal.Format;
                return true;
            }
        }

        format = default;
        return false;
    }

    public void Play() => _router.Play();

    public void Stop() => _router.Stop();

    public void Dispose()
    {
        List<ProgramBusSource> monitors;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _terminals.Clear();
            monitors = _monitorBuses.Values.ToList();
            _monitorBuses.Clear();
        }

        // Revoke producer AND monitor endpoints before tearing down the router. Acquisition uses the
        // same lifecycle gate, so a returned endpoint is guaranteed invalid once Dispose completes.
        _bus.Dispose();
        foreach (var monitor in monitors)
            monitor.Dispose();
        _router.Dispose();
    }

    /// <summary>Everything between the program bus and the speaker on the MASTER path: chunks in the
    /// master terminal's pump plus the terminal's own reported submit-to-speaker latency. Hot-path
    /// safe: no exceptions, no allocations; no master = 0 (the fallback clock needs no subtraction
    /// for a speaker nothing reaches).</summary>
    private long DownstreamLeadTicks()
    {
        long ticks = 0;
        if (_masterTerminalId is { } masterId && _router.TryGetPumpStats(masterId, out var pump))
            ticks = pump.InFlight * (long)ChunkSamples * TimeSpan.TicksPerSecond / _mixSampleRate;
        if (_masterTerminal is { } terminal)
            ticks += AudioOutputLatency.Of(terminal).Ticks;
        return ticks;
    }

    /// <summary>Late-bound view of the CURRENT clock-master terminal's playback clock. Throws when no
    /// master is attached - the extracted client clock treats that exactly like a terminal outage
    /// (wall-clock fallback spliced onto the high-water) and recovers when a master appears, because
    /// the first successful read carries an unseen epoch id.</summary>
    private sealed class MasterClockProxy(AudioPatchBay bay) : IPlaybackClock
    {
        private IPlaybackClock Inner =>
            bay._masterTerminalClock
            ?? throw new InvalidOperationException("the bay has no clock-master terminal");

        public TimeSpan ElapsedSinceStart => Inner.ElapsedSinceStart;
        public long EpochId => Inner.EpochId;
        public bool IsAdvancing => Inner.IsAdvancing;
        public ClockReading Read() => Inner.Read();
    }

    private sealed class OwnedWrapperChain(List<IDisposable> wrappers) : IDisposable
    {
        private List<IDisposable>? _wrappers = wrappers;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _wrappers, null);
            if (owned is null) return;
            foreach (var wrapper in owned)
            {
                try { wrapper.Dispose(); }
                catch { /* best-effort teardown; terminal devices themselves are borrowed */ }
            }
        }
    }

    private void ValidatePatch(float[,] patch, int terminalChannels)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.GetLength(0) > LogicalChannels || patch.GetLength(1) > terminalChannels)
            throw new ArgumentException(
                $"patch is {patch.GetLength(0)}x{patch.GetLength(1)} but the bay has {LogicalChannels} logical " +
                $"channels and the terminal has {terminalChannels} channels", nameof(patch));
        foreach (var gain in patch)
        {
            if (!float.IsFinite(gain))
                throw new ArgumentException("patch gains must be finite", nameof(patch));
        }
    }
}

/// <summary>A monitoring input's lease: <see cref="Input"/> is the endpoint the previewing player
/// submits into (full clocked-output contract, like a program producer). Dispose to detach the
/// monitor from its terminal and release its private route.</summary>
public sealed class AudioMonitorLease : IDisposable
{
    private Action? _release;

    internal AudioMonitorLease(ProgramBusProducer input, Action release)
    {
        Input = input;
        _release = release;
    }

    public ProgramBusProducer Input { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
