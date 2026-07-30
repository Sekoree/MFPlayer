namespace S.Media.Routing;

/// <summary>
/// Multi-client fan-in for a physical <see cref="IAudioOutput"/>. Each lease has its own
/// single-producer buffer; one persistent <see cref="AudioRouter"/> mixes those buffers and is
/// the only producer that ever submits to the terminal output.
/// </summary>
/// <remarks>
/// This is the ownership boundary required for hardware outputs such as PortAudio, whose native
/// ring is deliberately single-producer. Sharing the terminal instance itself would corrupt that
/// contract, while opening one terminal per player creates duplicate operating-system audio nodes.
/// </remarks>
public sealed class SharedAudioOutput : IDisposable
{
    private const string TerminalId = "__shared_terminal";
    private const string SilenceId = "__shared_silence";

    private readonly Lock _gate = new();
    private readonly IAudioOutput _terminal;
    private readonly AudioRouter _mixer;
    private readonly TimeSpan _clientBufferDuration;
    private readonly int _chunkSamples;
    private readonly int _clientTargetQueueSamples;
    private readonly bool _disposeTerminalOutput;
    private readonly Dictionary<long, ClientInput> _clients = [];
    private long _nextClientId;
    private bool _disposed;

    /// <param name="terminalOutput">The one physical/backend output owned by the mixer.</param>
    /// <param name="disposeTerminalOutput">
    /// Whether disposing this owner also disposes <paramref name="terminalOutput"/>.
    /// </param>
    /// <param name="chunkSamples">Mixer chunk size in samples per channel.</param>
    /// <param name="pumpCapacityChunks">Short jitter queue in front of the physical output.</param>
    /// <param name="clientBufferDuration">Maximum buffer available to each independent producer.</param>
    /// <param name="clientTargetQueueChunks">
    /// Per-client refill reservoir. Hardware callbacks release capacity in bursts, so this must span
    /// more than the usual three-chunk steady-state jitter allowance.
    /// </param>
    public SharedAudioOutput(
        IAudioOutput terminalOutput,
        bool disposeTerminalOutput = false,
        int chunkSamples = 480,
        int pumpCapacityChunks = 4,
        TimeSpan? clientBufferDuration = null,
        int clientTargetQueueChunks = 8)
    {
        ArgumentNullException.ThrowIfNull(terminalOutput);
        terminalOutput.Format.Validate(nameof(terminalOutput));

        _terminal = terminalOutput;
        _disposeTerminalOutput = disposeTerminalOutput;
        _clientBufferDuration = clientBufferDuration ?? TimeSpan.FromMilliseconds(120);
        if (_clientBufferDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(clientBufferDuration), "must be > 0");
        if (clientTargetQueueChunks < 2)
            throw new ArgumentOutOfRangeException(nameof(clientTargetQueueChunks), "must be >= 2");
        _chunkSamples = chunkSamples;
        _clientTargetQueueSamples = checked(chunkSamples * clientTargetQueueChunks);

        _mixer = new AudioRouter(terminalOutput.Format.SampleRate, chunkSamples, pumpCapacityChunks)
        {
            AutoWirePrimary = true,
            // The permanent silence source keeps the device alive; natural EOF is not part of this
            // owner's lifecycle and must never flush a terminal used by a newly acquired client.
            FlushOutputsOnNaturalEof = false,
        };

        try
        {
            _mixer.AddOutput(terminalOutput, TerminalId, pumpCapacityChunks);
            _mixer.AddSource(new SilenceSource(terminalOutput.Format), SilenceId, autoResample: false);
            _mixer.AddRoute(SilenceId, TerminalId, ChannelMap.Identity(terminalOutput.Format.Channels));
            _mixer.Start();
        }
        catch
        {
            try { _mixer.Dispose(); }
            catch { /* preserve the construction failure */ }
            if (_disposeTerminalOutput)
            {
                try { (terminalOutput as IDisposable)?.Dispose(); }
                catch { /* preserve the construction failure */ }
            }
            throw;
        }
    }

    public AudioFormat Format => _terminal.Format;

    /// <summary>Number of independent playback clients currently feeding the terminal.</summary>
    public int ActiveLeaseCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>
    /// Creates an isolated endpoint for one producer. Disposing the returned lease removes only
    /// that producer; the physical output and every other client remain live.
    /// </summary>
    public SharedAudioOutputLease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var clientId = ++_nextClientId;
            var sourceId = $"client_{clientId}";
            var input = new ClientInput(this, _clientBufferDuration, _clientTargetQueueSamples,
                _terminal as IPlaybackClock);
            _mixer.AddSource(input, sourceId, autoResample: false);
            try
            {
                _mixer.AddRoute(sourceId, TerminalId, ChannelMap.Identity(Format.Channels));
                _clients.Add(clientId, input);
                return new SharedAudioOutputLease(input, () => Release(clientId, sourceId, input));
            }
            catch
            {
                _mixer.RemoveSource(sourceId);
                input.Dispose();
                throw;
            }
        }
    }

    private void Release(long clientId, string sourceId, ClientInput input)
    {
        lock (_gate)
        {
            if (!_clients.Remove(clientId))
                return;

            input.Dispose();
            if (!_disposed)
                _mixer.RemoveSource(sourceId); // also removes the client's route atomically
        }
    }

    public void Dispose()
    {
        ClientInput[] clients;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
            client.Dispose();
        try
        {
            _mixer.Dispose();
        }
        finally
        {
            if (_disposeTerminalOutput)
                (_terminal as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Audio between the mixer and the speaker, in ticks: chunks committed to the terminal's pump but
    /// not yet submitted, plus the terminal's own submit-to-speaker latency where it reports one
    /// (<see cref="IAudioOutputLatency"/>; hardware backends fold their ring depth into it). Together
    /// with a client's un-consumed bus backlog this is the lead a client clock must subtract - see
    /// <see cref="ClientInput.ElapsedSinceStart"/>. Read from the clock hot path: no exceptions, no
    /// allocations, one router-registry lock.
    /// </summary>
    private long DownstreamLatencyTicks()
    {
        var ticks = _mixer.TryGetPumpStats(TerminalId, out var pump)
            ? SamplesToTicks(pump.InFlight * _chunkSamples, Format.SampleRate)
            : 0;
        // AudioOutputLatency.Of degrades a terminal disposed mid-read to Zero, which matters because the
        // wall-clock fallback path reaches this call from OUTSIDE its own try/catch: the measurable
        // in-process depths still stand when the device does not.
        return ticks + AudioOutputLatency.Of(_terminal).Ticks;
    }

    private static long SamplesToTicks(long samples, int sampleRate) =>
        samples > 0 && sampleRate > 0 ? samples * TimeSpan.TicksPerSecond / sampleRate : 0;

    /// <summary>
    /// One SPSC client endpoint. It is clocked from the mixer's consumption of its private bus so
    /// an upstream router applies backpressure instead of slowly overflowing the buffer. Its
    /// <see cref="IPlaybackClock"/> is the terminal device clock minus a per-client epoch captured
    /// at attach and on <see cref="Flush"/> and minus the latency still between this client's
    /// submissions and the speaker, so each client sees its own transport starting at zero while
    /// still advancing with actually-played samples. Falls back to a wall-clock stopwatch when the
    /// terminal exposes no playback clock (or its reads fail).
    /// </summary>
    private sealed class ClientInput :
        IAudioOutput,
        IAudioOutputChannelCapabilities,
        IAudioSource,
        IClockedOutput,
        IFlushableOutput,
        IPlaybackClock,
        IAudioOutputLatency,
        IDisposable
    {
        private readonly SharedAudioOutput _owner;
        private readonly AudioBus _bus;
        private readonly int _targetQueueSamples;
        private readonly ManualResetEventSlim _spaceAvailable = new(false);
        private readonly IPlaybackClock? _terminalClock;
        private readonly System.Diagnostics.Stopwatch _fallbackElapsed = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Time constant of the DAC-lead low-pass, measured in the RAW clock's own domain (see
        /// <see cref="SmoothLeadTicks"/>). Long enough to average out the pump/bus sawtooth at production
        /// sizes (chunk 480, pump 4, client reservoir 8 - a ~40 ms pump swing on top of a ~80 ms bus one),
        /// short enough that a genuine device re-negotiation is tracked within a fraction of a second.</summary>
        private const double LeadSmoothingSeconds = 0.25;

        private readonly Lock _epochGate = new();
        private long _terminalEpochTicks;
        /// <summary><see cref="ClockReading.EpochId"/> of the terminal that <see cref="_terminalEpochTicks"/>
        /// was captured in. The terminal REPORTS its re-anchors, so recovery is an equality check rather than
        /// the old "a regression below the epoch must mean it re-anchored" guess.</summary>
        private long _terminalEpochId;
        /// <summary>This client's own <see cref="IPlaybackClock.EpochId"/>: a fresh id at attach and at every
        /// <see cref="Flush"/> - the two places <see cref="ElapsedSinceStart"/> restarts at zero. A terminal
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
        private int _disposed;
        // A timed Wait can inflate the event to a kernel-backed handle, so it IS disposed - but only
        // once the last waiter drained (review P3-1); disposing under a live Wait would race it.
        private int _activeWaiters;
        private int _eventDisposed;

        public ClientInput(SharedAudioOutput owner, TimeSpan bufferDuration, int targetQueueSamples,
            IPlaybackClock? terminalClock)
        {
            _owner = owner;
            _bus = new AudioBus(owner.Format, bufferDuration);
            _targetQueueSamples = Math.Min(_bus.CapacitySamples, targetQueueSamples);
            _terminalClock = terminalClock;
            ReanchorPlaybackEpoch();
        }

        public AudioFormat Format => _bus.Format;
        public AudioOutputChannelCapabilities ChannelCapabilities => _bus.ChannelCapabilities;
        public bool IsExhausted => false;

        /// <summary><see cref="IAudioOutputLatency.SubmitToOutputLatency"/> for one client: its own
        /// un-consumed bus backlog plus everything downstream of the mixer. That is exactly the lead
        /// <see cref="ReportAudible"/> subtracts - a client's samples are not at the speaker until the
        /// mixer has taken them AND the terminal has played them - so the two cannot disagree.
        /// <para>Reported RAW, not through <see cref="SmoothLeadTicks"/>: the low-pass exists to keep the
        /// monotonic clock steady against the pump/bus sawtooth, whereas a caller asking how far its
        /// submissions are from the speaker wants the current answer.</para></summary>
        public TimeSpan SubmitToOutputLatency
        {
            get
            {
                try
                {
                    return new TimeSpan(
                        SamplesToTicks(_bus.BufferedSamples, _bus.Format.SampleRate)
                        + _owner.DownstreamLatencyTicks());
                }
                catch
                {
                    return TimeSpan.Zero; // torn down mid-read - "unknown", per the interface contract
                }
            }
        }

        /// <summary>
        /// <see cref="IPlaybackClock.ElapsedSinceStart"/>: terminal device time minus this client's
        /// epoch, minus the client's DAC lead (see <see cref="ReportAudible"/>), clamped at zero. A
        /// terminal whose own clock re-anchored (device stop/start, an external flush) SAYS SO by handing
        /// back a different <see cref="ClockReading.EpochId"/>; the client epoch is then re-derived so this
        /// clock resumes from its high-water mark instead of freezing at zero until the terminal re-passes
        /// the stale epoch. Terminal reads go through its thread-safe surface only and never throw out of
        /// here - any failure degrades to the stopwatch domain.
        /// </summary>
        public TimeSpan ElapsedSinceStart => ReadRaw().elapsed;

        /// <inheritdoc />
        public long EpochId => Volatile.Read(ref _epochId);

        /// <inheritdoc />
        /// <remarks>Composed under <see cref="_epochGate"/>, the same gate every re-anchor writes
        /// <see cref="_epochId"/> under, so the id can never be paired with elapsed from the other side of a
        /// <see cref="Flush"/>.</remarks>
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
                    return (ReportAudible(ticks), Volatile.Read(ref _disposed) == 0 && terminal.IsAdvancing);
                }
                catch
                {
                    // Terminal stopped/disposed mid-read - fall through to the wall-clock domain, CONTINUING
                    // from the high-water rather than adopting the fallback stopwatch raw (see below).
                    RebaseFallbackToHighWater();
                }
            }

            return (ReportAudible(_fallbackElapsed.Elapsed.Ticks - Volatile.Read(ref _fallbackEpochTicks)),
                Volatile.Read(ref _disposed) == 0);
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
        private void RebaseFallbackToHighWater()
        {
            if (Interlocked.Exchange(ref _fallbackRebased, 1) != 0)
                return;
            lock (_epochGate)
            {
                Volatile.Write(ref _fallbackEpochTicks,
                    _fallbackElapsed.Elapsed.Ticks - Volatile.Read(ref _maxSinceEpochTicks));
            }
        }

        /// <summary>
        /// Maps raw since-epoch ticks to this client's <em>audible</em> position. The raw reading leads the
        /// speaker by everything still in flight - this client's un-consumed bus backlog plus
        /// <see cref="DownstreamLatencyTicks"/> - because the terminal clock advances with the device
        /// whether or not this client's samples got there yet; without the subtraction every client leads
        /// the DAC by the full pipeline depth (~100 ms at the defaults) and video scheduled against it runs
        /// early. The subtraction eases in quadratically over the first <c>2×lead</c> of a segment
        /// (<see cref="AudioLatencyCompensation.AudibleSeconds"/>, C¹-continuous) so a fresh client leaves
        /// zero smoothly instead of holding zero and jumping. A CAS high-water keeps the report monotonic
        /// when the estimate grows - the clock holds until the raw reading catches up - and is cleared by
        /// the same deliberate re-anchors that reset the epoch (attach, <see cref="Flush"/>).
        /// </summary>
        /// <remarks>
        /// The lead is <em>low-passed</em> before it is subtracted (<see cref="SmoothLeadTicks"/>). Both of
        /// its terms are instantaneous queue depths that swing hard at production sizes - the pump
        /// oscillates over its whole capacity every chunk and the client bus sawtooths between its refill
        /// reservoir and empty - and feeding an instantaneous depth into the monotonic high-water below
        /// turns the report into <c>max(raw − lead)</c> ≈ <c>raw − min(lead)</c> over a trailing window: the
        /// pump term is effectively never subtracted and a single under-run erases the bus term for good.
        /// Smoothing makes the steady-state subtraction the <em>mean</em> lead, which is what C1 is about,
        /// while the high-water still guarantees the report never steps backwards.
        /// </remarks>
        private TimeSpan ReportAudible(long rawTicks)
        {
            long audibleTicks = 0;
            if (rawTicks > 0)
            {
                var measuredLeadTicks = SamplesToTicks(_bus.BufferedSamples, _bus.Format.SampleRate)
                                        + _owner.DownstreamLatencyTicks();
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
        /// fake terminal. Two consequences follow for free: repeated reads without clock progress cannot
        /// move the estimate (so a burst of readers cannot converge it early), and a paused device freezes
        /// it instead of decaying it toward a queue depth nobody is draining.
        /// <para>Serialized on the epoch gate - uncontended in the steady state (one clock reader), and
        /// sharing it with the deliberate re-anchors is what keeps a <see cref="Flush"/> from interleaving
        /// a half-reset estimate. Still allocation-free and exception-free on the hot path.</para>
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
        /// readers re-derive once; a deliberate re-anchor (attach/Flush) that raced in first wins - its
        /// captured terminal epoch already matches on the re-check.</summary>
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
        /// domain (which advances). Reporting <c>false</c> here while <see cref="Read"/> reported
        /// <c>true</c> let a fan-in owner select this client and a composite deselect it from the same
        /// instant. This member stays free of <see cref="ReadRaw"/>'s side effects (high-waters, the DAC-lead
        /// filter) - it decides advancing only.</summary>
        public bool IsAdvancing
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
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
        /// takes a fresh <see cref="EpochId"/> - this IS the client clock's epoch boundary.</summary>
        private void ReanchorPlaybackEpoch()
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

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _bus.Submit(packedSamples);
        }

        public int ReadInto(Span<float> destination)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return 0;
            var read = _bus.ReadInto(destination);
            if (read > 0)
                SignalSpaceAvailable();
            return read;
        }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            if (chunkSamples <= 0)
                return Volatile.Read(ref _disposed) == 0 && !token.IsCancellationRequested;

            Interlocked.Increment(ref _activeWaiters);
            try
            {
                while (Volatile.Read(ref _disposed) == 0 && !token.IsCancellationRequested)
                {
                    var queued = _bus.BufferedSamples;
                    if (queued + chunkSamples <= _targetQueueSamples)
                        return true;

                    // Reset then re-check to close the race where the mixer consumes between the
                    // occupancy read and the reset. Unlike a duration estimate, this wakes on the
                    // exact consumption event and does not alternately oversleep/catch up.
                    _spaceAvailable.Reset();
                    // Dispose sets _disposed BEFORE its wake-up Set: if this Reset erased that Set,
                    // this re-check must observe _disposed and exit instead of sleeping out the wait.
                    if (Volatile.Read(ref _disposed) != 0)
                        return false;
                    if (_bus.BufferedSamples + chunkSamples <= _targetQueueSamples)
                        continue;
                    try
                    {
                        if (!_spaceAvailable.Wait(TimeSpan.FromSeconds(5), token))
                            return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }

                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _activeWaiters);
                DisposeEventOnceDrained();
            }
        }

        public void Flush()
        {
            _bus.Flush();
            // Same contract as the hardware outputs: Flush re-anchors IPlaybackClock to zero.
            ReanchorPlaybackEpoch();
            SignalSpaceAvailable();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _bus.Flush();
                SignalSpaceAvailable();
                DisposeEventOnceDrained();
            }
        }

        /// <summary>Wakes waiters; a lost race against the final event disposal is a harmless no-op
        /// (nobody can be waiting once the drain completed).</summary>
        private void SignalSpaceAvailable()
        {
            if (Volatile.Read(ref _eventDisposed) != 0)
                return;
            try
            {
                _spaceAvailable.Set();
            }
            catch (ObjectDisposedException)
            {
                // Raced DisposeEventOnceDrained - by then no waiter exists to wake.
            }
        }

        private void DisposeEventOnceDrained()
        {
            if (Volatile.Read(ref _disposed) != 0
                && Volatile.Read(ref _activeWaiters) == 0
                && Interlocked.Exchange(ref _eventDisposed, 1) == 0)
                _spaceAvailable.Dispose();
        }
    }

    private sealed class SilenceSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> destination)
        {
            destination.Clear();
            return destination.Length;
        }
    }
}

/// <summary>A single producer's borrowed endpoint into a <see cref="SharedAudioOutput"/>.</summary>
public sealed class SharedAudioOutputLease : IDisposable
{
    private Action? _release;

    internal SharedAudioOutputLease(IAudioOutput output, Action release)
    {
        Output = output;
        _release = release;
    }

    public IAudioOutput Output { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
