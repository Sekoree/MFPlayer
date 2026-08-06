using S.Media.Core.Audio;

namespace S.Media.Routing;

/// <summary>
/// The V-wide logical program bus of the project audio patch (HaCue extraction plan, resolved
/// decision 1: "program sum, not per-pair matrices"). An <see cref="IAudioSource"/> whose
/// <see cref="ReadInto"/> sums every registered producer's pending audio through that producer's
/// N×V send matrix - P send passes per chunk. A host router then routes THIS single V-channel
/// source to each real terminal with a dense V×R patch matrix (R passes), so the whole topology
/// costs P + R matrix passes per chunk instead of P × R.
/// <para>The bus itself is the destination span of the read - per-chunk scratch, not a queue:
/// registering more producers never changes any terminal's reported latency. The only buffering is
/// each producer's own bounded ring (one <see cref="FrameAlignedFloatRing"/> per lease), which is
/// the producer-isolation boundary the plan's target topology names: "one bounded client ring per
/// producer and one output pump per terminal".</para>
/// <para>Thread model: one reader (the owning router's run loop) calls <see cref="ReadInto"/>;
/// each producer submits from its own playback thread (SPSC per ring); send-matrix updates and
/// producer acquire/dispose may come from any control thread. Send updates are click-free: the
/// next chunk ramps from the previous gains with the router's exact sample-mid interpolation, then
/// settles.</para>
/// </summary>
public sealed record ProgramBusClockContext(
    IPlaybackClock TerminalClock,
    Func<long> DownstreamLeadTicks);

public sealed class ProgramBusSource : IAudioSource, IDisposable
{
    private readonly int _busChannels;
    private readonly int _sampleRate;
    private readonly int _producerRingFrames;
    private readonly ProgramBusClockContext? _clockContext;
    private readonly Lock _producersGate = new();
    private ProgramBusProducer[] _producers = [];
    private ProgramBusMeter? _meter;
    private bool _disposed;

    /// <param name="busChannels">V - the project's logical channel count.</param>
    /// <param name="sampleRate">The project mix rate; every producer submits at this rate.</param>
    /// <param name="producerRingFrames">Per-producer ring capacity in frames (default 4800 =
    /// 100 ms at 48 kHz). Overflow drops the OLDEST frames (live policy) and is counted.</param>
    /// <param name="clockContext">The master-terminal clock plus the downstream-lead measurement the
    /// owning bay provides; producer leases rebase their <see cref="IPlaybackClock"/> from it. Null =
    /// producer clocks run in the wall-clock fallback domain (headless hosts still advance).</param>
    public ProgramBusSource(
        int busChannels,
        int sampleRate,
        int producerRingFrames = 4800,
        ProgramBusClockContext? clockContext = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(busChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(producerRingFrames, 1);
        _busChannels = busChannels;
        _sampleRate = sampleRate;
        _producerRingFrames = producerRingFrames;
        _clockContext = clockContext;
    }

    public AudioFormat Format => new(_sampleRate, _busChannels);

    /// <summary>A live bus is never exhausted; with no producers it reads silence.</summary>
    public bool IsExhausted => false;

    public int BusChannels => _busChannels;

    public int ProducerCount
    {
        get { lock (_producersGate) return _producers.Length; }
    }

    /// <summary>
    /// Optional per-logical-channel metering of the program sum. Null (the default) means the meter
    /// pass is skipped entirely, so a headless or test host pays nothing for it. Set it once at
    /// construction time in practice; the field is volatile so attaching it on a live bus is safe and
    /// simply starts metering on the next chunk.
    /// </summary>
    /// <remarks>Metered here rather than at the terminals on purpose: this is the only point where a
    /// logical channel exists as audio. See <see cref="ProgramBusMeter"/>.</remarks>
    public ProgramBusMeter? Meter
    {
        get => Volatile.Read(ref _meter);
        set
        {
            if (value is not null && value.Channels != _busChannels)
                throw new ArgumentException(
                    $"meter has {value.Channels} channels, bus has {_busChannels}", nameof(value));
            Volatile.Write(ref _meter, value);
        }
    }

    /// <summary>
    /// Registers a producer voice. <paramref name="sends"/> is the producer's N×V send matrix in
    /// the fused-kernel layout <c>gains[busChannel * sourceChannels + sourceChannel]</c>; every
    /// value must be finite. Dispose the lease to remove the producer - only its own contribution
    /// stops, mid-chunk-safe for every other producer.
    /// </summary>
    public ProgramBusProducer AcquireProducer(
        int sourceChannels, ReadOnlySpan<float> sends, string? label = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ValidateSends(sends, sourceChannels);

        lock (_producersGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var producer = new ProgramBusProducer(
                this, sourceChannels, sends.ToArray(), _producerRingFrames, _clockContext, label);
            var next = new ProgramBusProducer[_producers.Length + 1];
            _producers.CopyTo(next, 0);
            next[^1] = producer;
            _producers = next;
            return producer;
        }
    }

    /// <summary>
    /// Sums each producer's pending frames into <paramref name="destination"/> through its send
    /// matrix. Producers with less buffered audio than the chunk contribute what they have and
    /// SILENCE for the rest (counted as an underrun once they have been observed flowing) - never
    /// stale samples. Always returns the full requested length: a program bus supplies silence,
    /// not a short read, so downstream pacing never stalls on an idle stage.
    /// </summary>
    public int ReadInto(Span<float> destination)
    {
        if (destination.Length % _busChannels != 0)
            throw new ArgumentException(
                $"length {destination.Length} is not a multiple of bus channel count {_busChannels}",
                nameof(destination));

        destination.Clear();
        var frames = destination.Length / _busChannels;
        if (frames == 0)
            return 0;

        ProgramBusProducer[] producers;
        lock (_producersGate)
            producers = _producers;

        foreach (var producer in producers)
            producer.MixInto(destination, frames, _busChannels);

        // After every producer has summed and before the bay's V×R pass fans this out: `destination`
        // now IS the logical program bus, which is the only point where a per-logical-output level is
        // meaningful (see ProgramBusMeter). Skipped entirely when no meter is attached.
        Volatile.Read(ref _meter)?.Observe(destination, frames);

        return destination.Length;
    }

    /// <summary>
    /// How far ahead of the speaker a voice's audio currently sits BEFORE the bus - the deepest live
    /// producer ring, in ticks. Zero when nothing is playing.
    /// </summary>
    /// <remarks>
    /// This is the lead a sounding voice's own clock already subtracts. A voice with no producer at all
    /// (a silent video cue) has to borrow it, or its picture runs early by exactly this much: the audio
    /// for the frame on screen has been mixed but is still queued. Deepest rather than average on
    /// purpose - aligning to the last audio to become audible is what keeps the picture from LEADING
    /// any part of the programme.
    /// </remarks>
    internal long DeepestProducerLeadTicks()
    {
        ProgramBusProducer[] producers;
        lock (_producersGate)
            producers = _producers;

        var frames = 0;
        foreach (var producer in producers)
            frames = Math.Max(frames, producer.BufferedFrames);
        return frames > 0 ? (long)frames * TimeSpan.TicksPerSecond / _sampleRate : 0;
    }

    /// <summary>One diagnostics row per live producer lease. Snapshots the array under the gate, then
    /// reads each producer's own volatile counters - so it never blocks a submit.</summary>
    internal IReadOnlyList<ProducerDiagnostics> SnapshotProducers()
    {
        ProgramBusProducer[] producers;
        lock (_producersGate)
            producers = _producers;

        var rows = new ProducerDiagnostics[producers.Length];
        for (var i = 0; i < producers.Length; i++)
        {
            var p = producers[i];
            rows[i] = new ProducerDiagnostics(
                p.Label, p.BufferedFrames, p.OverflowFloats, p.UnderrunFloats,
                p.SubmitToOutputLatency, p.EpochId, p.IsAdvancing);
        }
        return rows;
    }

    internal void RemoveProducer(ProgramBusProducer producer)
    {
        lock (_producersGate)
        {
            var index = Array.IndexOf(_producers, producer);
            if (index < 0)
                return;
            var next = new ProgramBusProducer[_producers.Length - 1];
            Array.Copy(_producers, 0, next, 0, index);
            Array.Copy(_producers, index + 1, next, index, next.Length - index);
            _producers = next;
        }
    }

    /// <summary>
    /// Closes the bus and invalidates every producer lease atomically with respect to acquisition.
    /// A producer returned before this call is unusable when this call returns; an acquisition that
    /// loses the race observes <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        ProgramBusProducer[] producers;
        lock (_producersGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            producers = _producers;
            _producers = [];
        }

        foreach (var producer in producers)
            producer.InvalidateFromOwner();
    }

    internal void ValidateSends(ReadOnlySpan<float> sends, int sourceChannels)
    {
        if (sends.Length != _busChannels * sourceChannels)
            throw new ArgumentException(
                $"send matrix length {sends.Length} != busChannels({_busChannels}) * sourceChannels({sourceChannels})",
                nameof(sends));
        foreach (var gain in sends)
        {
            if (!float.IsFinite(gain))
                throw new ArgumentException("send gains must be finite", nameof(sends));
        }
    }
}

/// <summary>
/// One voice's lease on the program bus: an <see cref="IAudioOutput"/> the voice's player submits
/// interleaved N-channel audio into, buffered by a bounded SPSC ring and mixed into the bus by the
/// reader through the lease's N×V send matrix. <see cref="UpdateSends"/> is the live fade path -
/// the plan's gain-composition rule rides a voice's send gains, never the wide patch - and applies
/// click-free (one-chunk sample-mid ramp, the router's exact interpolation). Dispose to release.
/// </summary>
public sealed class ProgramBusProducer :
    IAudioOutput,
    IClockedOutput,
    IFlushableOutput,
    IPlaybackClock,
    IAudioOutputLatency,
    IDisposable
{
    private readonly ProgramBusSource _owner;
    private readonly int _sourceChannels;
    private readonly FrameAlignedFloatRing _ring;
    /// <summary>The extracted SharedAudioOutput client clock (HaCue plan, Phase 3): master terminal
    /// time rebased to this producer's epoch minus the low-passed lead still between this producer's
    /// submissions and the speaker (own ring backlog + the bay's downstream measurement).</summary>
    private readonly AudibleClientClock _clock;
    private readonly Func<long> _leadTicks;
    private readonly ManualResetEventSlim _spaceAvailable = new(false);
    private int _activeWaiters;
    private int _eventDisposed;

    // Reader-owned ramp state: _currentGains is mutated only by the bus reader; _targetGains is
    // replaced atomically by UpdateSends and compared by reference to detect a pending ramp.
    private readonly float[] _currentGains;
    private volatile float[] _targetGains;

    private float[] _scratch = [];
    private long _overflowFloats;
    private long _underrunFloats;
    /// <summary>Floats this producer has GRANTED to <see cref="WaitForCapacity"/> but not yet seen in
    /// <see cref="Submit"/> - the audio in flight between the upstream router's mix loop and this ring
    /// (its output pump queues whole chunks). Without this term the same free space is handed out once
    /// per pump slot and the router overproduces by the pump depth, permanently overflowing the ring.</summary>
    private long _grantedFloats;
    private bool _observedFlowing;
    private int _disposed;

    internal ProgramBusProducer(
        ProgramBusSource owner,
        int sourceChannels,
        float[] sends,
        int ringFrames,
        ProgramBusClockContext? clockContext,
        string? label = null)
    {
        Label = label;
        _owner = owner;
        _sourceChannels = sourceChannels;
        _currentGains = (float[])sends.Clone();
        _targetGains = sends;
        _ring = new FrameAlignedFloatRing(sourceChannels, (long)ringFrames * sourceChannels);
        var rate = owner.Format.SampleRate;
        _leadTicks = clockContext is { } context
            ? () => RingTicks(rate) + context.DownstreamLeadTicks()
            : () => RingTicks(rate);
        _clock = new AudibleClientClock(clockContext?.TerminalClock, _leadTicks);
    }

    private long RingTicks(int sampleRate)
    {
        var framesBuffered = _ring.BufferedFloats / _sourceChannels;
        return framesBuffered > 0 ? framesBuffered * TimeSpan.TicksPerSecond / sampleRate : 0;
    }

    public AudioFormat Format => new(_owner.Format.SampleRate, _sourceChannels);

    /// <summary>Frames currently buffered between the voice and the bus (health surface).</summary>
    /// <summary>Optional host-supplied name for diagnostics (a cue label, say). Diagnostics rows are of
    /// little use without it - "one of five leases is starving the bus" is not an actionable statement.</summary>
    public string? Label { get; }

    public int BufferedFrames => _ring.BufferedFrames;

    /// <summary>Floats dropped because the voice outran the bus (oldest-first, live policy).</summary>
    public long OverflowFloats => Interlocked.Read(ref _overflowFloats);

    /// <summary>Floats of silence substituted because the voice fell behind after having flowed.</summary>
    public long UnderrunFloats => Interlocked.Read(ref _underrunFloats);

    /// <summary>Submits interleaved N-channel audio at the bus rate. On overflow the OLDEST frames
    /// are dropped so the newest audio always lands (live policy, counted).</summary>
    public void Submit(ReadOnlySpan<float> samples)
    {
        if (_disposed != 0)
            return;

        // This audio has arrived, so it is no longer "in flight" against a capacity grant. Clamped at
        // zero because a router that never paces from this producer (wall clock, or a non-primary
        // attachment) submits without ever having asked - it simply never accrues a grant.
        ReleaseGrant(samples.Length);

        var written = _ring.Write(samples);
        if (written == samples.Length)
        {
            // Disposal may race a submission that passed the fast pre-check. Re-clear after the
            // write so no audio can remain queued once owner invalidation has completed.
            if (_disposed != 0)
                _ring.Clear();
            return;
        }

        // Ring full: rebase to the newest audio. Keep room for the remainder, then write it.
        var rest = samples[written..];
        if (rest.Length >= _ring.CapacityFloats)
        {
            // The submission alone exceeds the whole ring - keep only its newest ring-full.
            Interlocked.Add(ref _overflowFloats, _ring.BufferedFloats + rest.Length - _ring.CapacityFloats);
            _ring.Clear();
            rest = rest[^_ring.CapacityFloats..];
        }
        else
        {
            Interlocked.Add(ref _overflowFloats, _ring.DropOldestKeepingFloats(_ring.CapacityFloats - rest.Length));
        }

        _ring.Write(rest);
        if (_disposed != 0)
            _ring.Clear();
    }

    /// <summary>Replaces the N×V send matrix (same layout/validation as the acquire). The next bus
    /// chunk ramps from the previous gains to these, then settles - click-free like a route fade.</summary>
    public void UpdateSends(ReadOnlySpan<float> sends)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _owner.ValidateSends(sends, _sourceChannels);
        _targetGains = sends.ToArray();
    }

    /// <summary><see cref="IAudioOutputLatency.SubmitToOutputLatency"/>: this producer's un-consumed
    /// ring backlog plus everything the bay measures downstream of the bus - exactly the lead the
    /// clock subtracts, reported RAW (the low-pass is a clock-steadiness concern, not a latency one).</summary>
    public TimeSpan SubmitToOutputLatency
    {
        get
        {
            try
            {
                return new TimeSpan(_leadTicks());
            }
            catch
            {
                return TimeSpan.Zero; // torn down mid-read - "unknown", per the interface contract
            }
        }
    }

    /// <summary>Master-rebased audible position: zero at acquire and at every <see cref="Flush"/>,
    /// advancing with actually-played samples. Full contract on <see cref="AudibleClientClock"/>.</summary>
    public TimeSpan ElapsedSinceStart => _clock.ElapsedSinceStart;

    /// <inheritdoc />
    public long EpochId => _clock.EpochId;

    /// <inheritdoc />
    public ClockReading Read() => _clock.Read();

    /// <inheritdoc cref="AudibleClientClock.IsAdvancing" />
    public bool IsAdvancing => _clock.IsAdvancing;

    /// <summary>
    /// Blocks until a chunk fits in the producer ring (the upstream router's backpressure hook).
    /// Returns false on cancel/teardown - the ClientInput reset-then-recheck pattern.
    /// </summary>
    /// <remarks>
    /// <para>A grant counts the audio ALREADY in flight (<see cref="_grantedFloats"/>), not just what is
    /// sitting in the ring. The router does not submit the chunk it is granted - it mixes it into its
    /// output pump, and a drainer thread submits it some time later. Answering "does one more chunk
    /// fit?" from the ring alone hands the same free space to every pump slot in turn, so the router is
    /// waved through <c>pumpCapacityChunks</c> times per chunk actually consumed. That is an OPEN LOOP:
    /// production settles at the drainer's speed rather than the bus's, the ring pins at capacity and
    /// <see cref="Submit"/> drops oldest frames for the rest of the run (measured ~3.4x overproduction
    /// and 240% of the stream discarded at a pump depth of 8).</para>
    /// <para>Pacing targets HALF the ring rather than all of it: the remainder is the jitter headroom a
    /// drainer hiccup spends instead of dropping audio. Steady state is therefore ~half the ring of
    /// latency, and the producer is genlocked to whatever consumes the bus - transitively, the show's
    /// master device clock.</para>
    /// </remarks>
    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0)
            return _disposed == 0 && !token.IsCancellationRequested;

        var needFloats = chunkSamples * _sourceChannels;
        // Half the ring, but never so tight that the producer cannot keep one chunk buffered while a
        // second is in flight - on a ring only a couple of chunks deep, half of it would leave the
        // router waiting on a few frames of slack. Capped at the real capacity so an oversized chunk is
        // still granted against a drained ring rather than stalling the run loop forever.
        var targetFloats = Math.Clamp(_ring.CapacityFloats / 2, Math.Min(needFloats * 2, _ring.CapacityFloats), _ring.CapacityFloats);
        Interlocked.Increment(ref _activeWaiters);
        try
        {
            while (_disposed == 0 && !token.IsCancellationRequested)
            {
                if (TryTakeGrant(needFloats, targetFloats))
                    return true;

                _spaceAvailable.Reset();
                if (_disposed != 0)
                    return false;
                if (TryTakeGrant(needFloats, targetFloats))
                    return true;
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

    /// <summary>Accrues a capacity grant when the ring plus everything already in flight leaves room for
    /// one more chunk below the pacing target.</summary>
    private bool TryTakeGrant(int needFloats, int targetFloats)
    {
        if (_ring.BufferedFloats + Volatile.Read(ref _grantedFloats) + needFloats > targetFloats)
            return false;
        Interlocked.Add(ref _grantedFloats, needFloats);
        return true;
    }

    /// <summary>Retires a grant as its audio lands, without letting an ungranted submit drive the
    /// counter negative (a wall-clock-paced router never calls <see cref="WaitForCapacity"/>).</summary>
    private void ReleaseGrant(int floats)
    {
        while (true)
        {
            var granted = Volatile.Read(ref _grantedFloats);
            var next = Math.Max(0, granted - floats);
            if (Interlocked.CompareExchange(ref _grantedFloats, next, granted) == granted)
                return;
        }
    }

    /// <summary>Discards buffered audio and re-anchors the producer clock to zero with a fresh
    /// <see cref="EpochId"/> - the seek/flush contract, with no backwards read (high-waters reset
    /// with the epoch, not against it).</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _ring.Clear();
        // Drop outstanding capacity grants with the audio they were issued for. AudioRouter's
        // FlushOutputBuffers abandons the output pump's queue and then calls this, so those chunks will
        // never arrive to retire their own grants; carrying them would leak pacing credit on every
        // pause/seek until the producer could no longer be granted a chunk at all.
        Volatile.Write(ref _grantedFloats, 0);
        _clock.Reanchor();
        SignalSpaceAvailable();
    }

    internal void MixInto(Span<float> destination, int frames, int busChannels)
    {
        if (_disposed != 0)
            return;

        var need = frames * _sourceChannels;
        if (_scratch.Length < need)
            _scratch = new float[need];
        var read = _ring.Read(_scratch.AsSpan(0, need));
        if (read > 0)
            SignalSpaceAvailable();
        var framesRead = read / _sourceChannels;

        if (framesRead < frames && _observedFlowing)
            Interlocked.Add(ref _underrunFloats, (frames - framesRead) * _sourceChannels);
        if (framesRead == 0)
            return;
        _observedFlowing = true;

        var target = _targetGains;
        var src = (ReadOnlySpan<float>)_scratch.AsSpan(0, framesRead * _sourceChannels);
        var dst = destination[..(framesRead * busChannels)];
        if (ReferenceEquals(target, _currentGains) || target.AsSpan().SequenceEqual(_currentGains))
        {
            AudioRouter.ApplyFusedMatrixSettled(src, _sourceChannels, dst, busChannels, _currentGains, framesRead);
        }
        else
        {
            AudioRouter.ApplyFusedMatrixRamp(src, _sourceChannels, dst, busChannels, _currentGains, target, framesRead);
            target.CopyTo(_currentGains.AsSpan());
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _clock.Stop();
        _owner.RemoveProducer(this);
        _ring.Clear();
        SignalSpaceAvailable();
        DisposeEventOnceDrained();
    }

    internal void InvalidateFromOwner()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _clock.Stop();
        _ring.Clear();
        SignalSpaceAvailable();
        DisposeEventOnceDrained();
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
