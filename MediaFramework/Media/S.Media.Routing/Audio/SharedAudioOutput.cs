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
        IPipelineLeadClock,
        IAudioOutputLatency,
        IDisposable
    {
        private readonly SharedAudioOutput _owner;
        private readonly AudioBus _bus;
        private readonly int _targetQueueSamples;
        private readonly ManualResetEventSlim _spaceAvailable = new(false);
        /// <summary>The epoch/lead/fallback machinery, extracted to <see cref="AudibleClientClock"/> so the
        /// patch bay's producer leases run the identical algorithm (HaCue plan, Phase 3). The lead this
        /// client injects is its un-consumed bus backlog plus everything downstream of the mixer - exactly
        /// what <see cref="SubmitToOutputLatency"/> reports raw.</summary>
        private readonly AudibleClientClock _clock;
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
            _clock = new AudibleClientClock(
                terminalClock,
                () => SamplesToTicks(_bus.BufferedSamples, _bus.Format.SampleRate)
                      + _owner.DownstreamLatencyTicks());
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

        /// <summary><see cref="IPlaybackClock.ElapsedSinceStart"/>: terminal device time minus this
        /// client's epoch, minus the client's low-passed DAC lead, monotonic and zero-clamped - the
        /// full contract lives on <see cref="AudibleClientClock"/>.</summary>
        public TimeSpan ElapsedSinceStart => _clock.ElapsedSinceStart;

        /// <inheritdoc />
        public long EpochId => _clock.EpochId;

        /// <inheritdoc />
        public ClockReading Read() => _clock.Read();

        /// <inheritdoc cref="AudibleClientClock.IsAdvancing" />
        public bool IsAdvancing => _clock.IsAdvancing;

        /// <inheritdoc cref="AudibleClientClock.CurrentPipelineLead" />
        public TimeSpan CurrentPipelineLead => _clock.CurrentPipelineLead;

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
            _clock.Reanchor();
            SignalSpaceAvailable();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _clock.Stop();
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
