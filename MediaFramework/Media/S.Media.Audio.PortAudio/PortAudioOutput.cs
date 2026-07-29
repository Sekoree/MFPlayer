using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace S.Media.Audio.PortAudio;

/// <summary>
/// Audio output backed by a PortAudio output stream. Producers call
/// <see cref="Submit"/> with packed float32 frames; PortAudio's audio-thread
/// callback drains an SPSC ring buffer and fills silence on underrun.
/// </summary>
/// <remarks>
/// <para>
/// Single-producer / single-consumer: the producer is whoever owns the
/// decoder/mixer thread; the consumer is PortAudio's internal audio thread.
/// The ring buffer is a power-of-two float array indexed via mask. Reads and
/// writes are <see cref="Volatile"/>-ordered around two monotonic counters.
/// </para>
/// <para>
/// <see cref="Dispose"/> calls <see cref="Stop"/> then <see cref="PortAudioRuntime.Release"/>; each step is wrapped so <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues best-effort.
/// </para>
/// </remarks>
public sealed unsafe class PortAudioOutput : IAudioOutput, IAudioOutputChannelCapabilities, IClockedOutput, IFlushableOutput, IPlaybackClock, IAudioOutputPlaybackStats, IAudioOutputLatency, IDisposable
{
    private readonly AudioFormat _format;
    private readonly int _deviceIndex;
    private readonly int _maxOutputChannels;
    private readonly double _suggestedLatency;
    private readonly nuint _framesPerBuffer;
    private readonly FrameAlignedFloatRing _ring;

    private long _playedSamples;
    private long _droppedSamples;
    private long _underrunSamples;
    private long _callbackCount;
    private long _lastSubmitDropLogTicks;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;
    /// <summary>1 once the stream went inactive while we believe it should be running (device lost/removed). Latched until the next <see cref="Start"/>.</summary>
    private int _deviceLost;

    private nint _stream;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Serializes native stream-lifecycle transitions so two managed threads can never call
    /// Pa_AbortStream / Pa_StopStream / Pa_StartStream on the same handle concurrently - which
    /// wedges the PortAudio backend. <see cref="Submit"/> runs on the router's per-output drainer
    /// thread while <see cref="WaitForCapacity"/> runs on the router's run-loop thread, and right
    /// after a <see cref="Flush"/> both can reach <see cref="EnsureStreamRunningAfterFlush"/>.
    /// </summary>
    private readonly Lock _streamLifecycleGate = new();

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.PortAudio.PortAudioOutput");
    private long _waitForCapacityWarnTicks;

    /// <summary>How often <see cref="WaitForCapacity"/> probes stream health while blocked on a full ring.</summary>
    private const int DeviceHealthProbeIntervalMs = 250;

    /// <summary>Pa_GetStreamTime at <see cref="_segmentPlayed0Samples"/> - set on first callback after Start/Flush.</summary>
    private double _segmentStreamT0;
    /// <summary><see cref="PlayedSamples"/> snapshot paired with <see cref="_segmentStreamT0"/>.</summary>
    private long _segmentPlayed0Samples;
    /// <summary>1 after first callback calibrates stream time vs played samples for this stream segment.</summary>
    private int _streamSmoothCalibrated;
    /// <summary>
    /// <see cref="PlayedSamples"/> baseline for <see cref="IPlaybackClock"/> - reset on each
    /// <see cref="Start"/>/<see cref="Flush"/> so pause/resume drift math sees segment-local time,
    /// not lifetime output counters that keep advancing on underrun silence.
    /// </summary>
    private long _playbackEpochSamples;
    /// <summary>
    /// <see cref="IPlaybackClock.EpochId"/> paired with <see cref="_playbackEpochSamples"/>: a fresh
    /// <see cref="PlaybackEpoch.Next"/> id at every re-anchor of this clock (<see cref="Start"/>,
    /// <see cref="Flush"/>, device loss). Written under <see cref="_streamLifecycleGate"/> together with the
    /// sample epoch, so <see cref="Read"/> cannot hand out an id from one segment with elapsed from another.
    /// </summary>
    private long _playbackEpochId = PlaybackEpoch.Next();
    /// <summary>1 after <see cref="Flush"/> stops the PA stream until the next producer call restarts it.</summary>
    private int _streamStoppedAfterFlush;

    public AudioFormat Format => _format;
    public AudioOutputChannelCapabilities ChannelCapabilities =>
        new(CurrentChannels: _format.Channels, MinChannels: 1, MaxChannels: _maxOutputChannels,
            SupportsRuntimeChannelReconfigure: false);
    public bool IsRunning => Volatile.Read(ref _isRunning);
    public int DeviceIndex => _deviceIndex;

    /// <summary>Total frames (samples per channel) PortAudio has already played from this output. Monotonic across Stop/Start.</summary>
    public long PlayedSamples => Volatile.Read(ref _playedSamples);
    /// <summary>Approximate samples-per-channel currently sitting in the ring buffer.</summary>
    public int QueuedSamples => _ring.BufferedFrames;
    public int CapacitySamples => _ring.CapacityFrames;
    /// <summary>Samples dropped on Submit because the ring buffer was full.</summary>
    public long DroppedSamples => Volatile.Read(ref _droppedSamples);
    /// <summary>Samples zeroed by the callback because the ring was empty.</summary>
    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);
    /// <summary>How many times the PA callback has fired (debug).</summary>
    public long CallbackCount => Volatile.Read(ref _callbackCount);
    /// <summary>Non-zero if the native stream callback caught an exception (diagnostics only; never throws across native boundary).</summary>
    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    /// <summary>
    /// First exception caught in the PortAudio stream callback, if <see cref="CallbackFaulted"/> is true.
    /// Cleared when <see cref="Start"/> begins a new stream session. Never throws from the callback thread;
    /// retain only for inspection on another thread.
    /// </summary>
    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);

    /// <summary>
    /// True once the stream read inactive while it should have been running - PortAudio has no
    /// device-loss callback on all hostapis, so loss is detected by this cheap health check on the
    /// reads the clock/pacing paths already make (<see cref="StreamActive"/>,
    /// <see cref="ElapsedSinceStart"/>, <see cref="WaitForCapacity"/>). While latched,
    /// <see cref="Submit"/> throws, <see cref="WaitForCapacity"/> reports no capacity and
    /// <see cref="IsAdvancing"/> is false, so the router surfaces the loss via
    /// <c>OutputErrored</c>/<c>Faulted</c> instead of freezing the mastered clock silently.
    /// Cleared by the next <see cref="Start"/>.
    /// </summary>
    public bool DeviceLost => Volatile.Read(ref _deviceLost) != 0;

    /// <summary>1 = PA reports stream active, 0 = inactive, negative = error/closed.</summary>
    public int StreamActive
    {
        get
        {
            // Native calls must not race Stop()'s Pa_CloseStream (use-after-free); the gate is
            // reentrant and only contended during rare lifecycle transitions.
            lock (_streamLifecycleGate)
            {
                var active = _stream != nint.Zero ? (int)Native.Pa_IsStreamActive(_stream) : -1;
                MaybeLatchDeviceLostUnderGate(active);
                return active;
            }
        }
    }

    /// <summary>
    /// The device-loss decision, pure for unit testing: a stream we started (and did not stop via
    /// Flush, and whose callback did not abort with its own latched fault) must report active;
    /// reading inactive (exactly 0 - negative is an unopened/errored handle query, handled by the
    /// lifecycle paths) means the host stopped it under us: device removed or backend error.
    /// </summary>
    internal static bool ShouldLatchDeviceLost(bool isRunning, bool stoppedAfterFlush, bool callbackFaulted, int streamActive) =>
        isRunning && !stoppedAfterFlush && !callbackFaulted && streamActive == 0;

    /// <summary>
    /// Must be called with <see cref="_streamLifecycleGate"/> held so the flag reads are serialized
    /// against Flush/Stop mid-transition (Pa_AbortStream and <c>_streamStoppedAfterFlush</c> are
    /// written under the same gate - no window where a deliberate stop reads as loss).
    /// </summary>
    private void MaybeLatchDeviceLostUnderGate(int streamActive)
    {
        if (!ShouldLatchDeviceLost(
                Volatile.Read(ref _isRunning),
                Volatile.Read(ref _streamStoppedAfterFlush) != 0,
                Volatile.Read(ref _callbackFaulted) != 0,
                streamActive))
            return;
        LatchDeviceLost();
    }

    private void LatchDeviceLost()
    {
        if (Interlocked.Exchange(ref _deviceLost, 1) != 0)
            return;
        // The segment this clock was reporting is over; whatever a restart produces belongs to a new one.
        Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
        Trace.LogError(
            "PortAudio stream on device {Device} went inactive while it should be running (device lost/removed); failing Submit/WaitForCapacity so the router surfaces OutputErrored",
            _deviceIndex);
    }

    internal void ForceDeviceLostForTest() => LatchDeviceLost();

    /// <summary>PortAudio's stream clock - wall-clock seconds since the stream started.</summary>
    public double StreamTime
    {
        get
        {
            lock (_streamLifecycleGate)
                return _stream != nint.Zero ? Native.Pa_GetStreamTime(_stream) : 0.0;
        }
    }

    // The negotiated output (DAC) latency in ticks, captured at Start; the master clock subtracts it so it
    // reports the audible position rather than the consumed one (see ElapsedSinceStart).
    private long _outputLatencyTicks;

    /// <summary>
    /// <see cref="IPlaybackClock.ElapsedSinceStart"/>: monotonic <strong>audible</strong> playback time - the
    /// consumed-sample position (aligned with <see cref="PlayedSamples"/>, advanced with <c>Pa_GetStreamTime</c>
    /// between callbacks so it isn't stuck for a whole buffer) <em>minus the output buffer latency</em>. The
    /// subtraction is what keeps A/V in sync: the device holds ~outputLatency of audio after we hand it over, so
    /// the consumed count leads the speaker by that much - without it, video scheduled against the master clock
    /// leads the audio (lip-sync drift, pronounced on high-latency hosts like JACK/ALSA). Falls back to sample
    /// counts before the first callback. During the startup window (first 2×outputLatency of a segment) the
    /// latency subtraction is eased in quadratically instead of clamped at zero - see
    /// <see cref="ComputeAudibleSeconds"/> - so the playhead advances smoothly from 0 instead of holding 0 for
    /// ~outputLatency and then jumping.
    /// </summary>
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            var epoch = Volatile.Read(ref _playbackEpochSamples);
            var playedNow = Volatile.Read(ref _playedSamples) - epoch;
            if (playedNow < 0) playedNow = 0;
            var sampleElapsedSec = playedNow / (double)_format.SampleRate;
            var elapsedSec = sampleElapsedSec;

            // This getter is polled from MediaClock's driver thread; the native reads must not
            // race Stop()'s Pa_CloseStream. The gate is only held long here while a lifecycle
            // transition is mid-flight, in which case blocking briefly is the correct outcome.
            lock (_streamLifecycleGate)
            {
                if (_stream != nint.Zero)
                {
                    var active = (int)Native.Pa_IsStreamActive(_stream);
                    // This ~30 Hz poll doubles as the device-loss health check (PortAudio has no
                    // loss callback on all hostapis); the flags are read under the gate we hold.
                    MaybeLatchDeviceLostUnderGate(active);
                    if (active == 1 && Volatile.Read(ref _streamSmoothCalibrated) != 0)
                    {
                        Thread.MemoryBarrier();
                        var st = Native.Pa_GetStreamTime(_stream);
                        if (double.IsFinite(st))
                        {
                            // _segmentPlayed0Samples is already segment-local (played - epoch at calibration).
                            var segmentPlayed0 = Volatile.Read(ref _segmentPlayed0Samples);
                            if (segmentPlayed0 < 0) segmentPlayed0 = 0;
                            var streamElapsedSec = segmentPlayed0 / (double)_format.SampleRate + (st - _segmentStreamT0);
                            if (streamElapsedSec < 0)
                                streamElapsedSec = 0;
                            // After Pa_AbortStream + Pa_StartStream, Pa_GetStreamTime can stall while callbacks
                            // still drain the ring - never let the master clock lag behind sample progress.
                            elapsedSec = Math.Max(sampleElapsedSec, streamElapsedSec);
                        }
                    }
                }
            }

            // Report the audible (speaker) position, easing the latency subtraction in over the startup window.
            var latencySec = Volatile.Read(ref _outputLatencyTicks) / (double)TimeSpan.TicksPerSecond;
            return TimeSpan.FromSeconds(ComputeAudibleSeconds(elapsedSec, latencySec));
        }
    }

    /// <inheritdoc cref="AudioLatencyCompensation.AudibleSeconds"/>
    internal static double ComputeAudibleSeconds(double elapsedSeconds, double outputLatencySeconds) =>
        AudioLatencyCompensation.AudibleSeconds(elapsedSeconds, outputLatencySeconds);

    /// <summary>
    /// <see cref="IAudioOutputLatency.SubmitToOutputLatency"/>: the managed ring backlog plus the
    /// negotiated device latency - what a fan-in owner clocking off <see cref="ElapsedSinceStart"/>
    /// must subtract for its own clients (this output's clock already reports its audible position,
    /// but a sample submitted now is that far from the speaker). No native calls on this path.
    /// </summary>
    public TimeSpan SubmitToOutputLatency =>
        new(QueuedSamples * (long)TimeSpan.TicksPerSecond / _format.SampleRate
            + Volatile.Read(ref _outputLatencyTicks));

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: true when the PA stream is open, reporting active, and the device has not been lost.</summary>
    public bool IsAdvancing
    {
        get
        {
            if (Volatile.Read(ref _deviceLost) != 0)
                return false;
            return StreamActive == 1;
        }
    }

    /// <inheritdoc />
    public long EpochId => Volatile.Read(ref _playbackEpochId);

    /// <summary>
    /// <see cref="IPlaybackClock.Read"/>: taken under <see cref="_streamLifecycleGate"/>, which is also
    /// where every re-anchor writes the epoch pair, so the id and the elapsed always come from the same
    /// segment. <see cref="LatchDeviceLost"/> can still bump the id from the native side outside the gate;
    /// that direction is benign because a lost device's elapsed does not rewind, and the consumer's rule for
    /// a same-epoch regression is to hold rather than fold.
    /// </summary>
    public ClockReading Read()
    {
        lock (_streamLifecycleGate)
            return new ClockReading(Volatile.Read(ref _playbackEpochId), ElapsedSinceStart, IsAdvancing);
    }

    /// <summary>
    /// <see cref="IFlushableOutput.Flush"/>: aborts the PortAudio stream
    /// (discards anything in the OS buffer), zeroes the ring counters, re-anchors
    /// <see cref="ElapsedSinceStart"/> to zero for this segment, and stops the
    /// stream until the next <see cref="Submit"/>/<see cref="WaitForCapacity"/>.
    /// </summary>
    public void Flush()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Flush", slowWarningMs: 250);
        if (_disposed || !Volatile.Read(ref _isRunning) || _stream == nint.Zero) return;
        lock (_streamLifecycleGate)
        {
            // Re-check under the gate: a concurrent EnsureStreamRunningAfterFlush may be mid-restart,
            // or Stop/Dispose may have closed the stream while we waited for the gate.
            if (_disposed || !Volatile.Read(ref _isRunning) || _stream == nint.Zero) return;
            Trace.LogDebug("Flush: aborting stream (queued={Queued}f target={Target}f played={Played}f epoch={Epoch}f elapsed={Elapsed} active={Active})",
                QueuedSamples, TargetQueueSamples, Volatile.Read(ref _playedSamples), Volatile.Read(ref _playbackEpochSamples),
                ElapsedSinceStart, StreamActive);
            Native.Pa_AbortStream(_stream);
            // Reset the ring so the queue starts empty; _playedSamples is preserved
            // (lifetime stat) but _playbackEpochSamples re-anchors IPlaybackClock to zero.
            _ring.Clear();
            Interlocked.Exchange(ref _underrunSamples, 0);
            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            // Abort stops the stream; do not restart until the next producer call so
            // underrun silence during pause cannot advance ElapsedSinceStart.
            Volatile.Write(ref _streamStoppedAfterFlush, 1);
            timing?.SetOutcome($"device={_deviceIndex} queued={QueuedSamples}");
        }
    }

    /// <summary>
    /// Target queue depth (samples per channel) maintained by
    /// <see cref="WaitForCapacity"/>. Defaults to half the ring's capacity -
    /// enough headroom to absorb producer jitter without piling up enough
    /// latency to feel sluggish. Set before <see cref="Start"/>.
    /// </summary>
    public int TargetQueueSamples { get; set; }

    public PortAudioOutput(
        AudioFormat format,
        int? deviceIndex = null,
        double? suggestedLatency = null,
        int framesPerBuffer = 0,
        int ringCapacityFrames = 16384)
    {
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        if (framesPerBuffer < 0) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer));
        if (ringCapacityFrames < 64) throw new ArgumentOutOfRangeException(nameof(ringCapacityFrames), "must be >= 64");

        _format = format;
        _framesPerBuffer = (nuint)framesPerBuffer;

        _ring = new FrameAlignedFloatRing(format.Channels, (long)ringCapacityFrames * format.Channels);
        TargetQueueSamples = _ring.CapacityFrames / 2;

        PortAudioRuntime.Acquire();
        try
        {
            // The catalog's resolution honors MFP_PORTAUDIO_HOST_API; calling Pa_GetDefaultOutputDevice
            // directly here would bypass the operator's host-API override on default-device opens.
            _deviceIndex = deviceIndex ?? PortAudioDeviceCatalog.ResolveDefaultOutputDevice();
            if (_deviceIndex < 0)
                throw new InvalidOperationException("no default PortAudio output device available");

            var devInfo = Native.Pa_GetDeviceInfo(_deviceIndex)
                ?? throw new InvalidOperationException($"invalid PortAudio device index {_deviceIndex}");
            _maxOutputChannels = Math.Max(1, devInfo.maxOutputChannels);
            if (devInfo.maxOutputChannels < format.Channels)
                throw new InvalidOperationException(
                    $"device '{devInfo.Name}' supports {devInfo.maxOutputChannels} output channels, requested {format.Channels}");

            // Default to defaultHighOutputLatency: managed producers can't reliably
            // sustain the sub-5ms periods that defaultLowOutputLatency negotiates
            // on PulseAudio/ALSA. Callers who own their threading can opt in to lower.
            _suggestedLatency = suggestedLatency ?? devInfo.defaultHighOutputLatency;
        }
        catch
        {
            PortAudioRuntime.Release();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Start", slowWarningMs: 1000);
        lock (_streamLifecycleGate)
        {
            if (_isRunning)
            {
                timing?.SetOutcome($"device={_deviceIndex} already-running");
                return;
            }

            Interlocked.Exchange(ref _callbackFaultException, null);
            Volatile.Write(ref _callbackFaulted, 0);
            Volatile.Write(ref _deviceLost, 0);

            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

            var outParams = new PaStreamParameters
            {
                device = _deviceIndex,
                channelCount = _format.Channels,
                sampleFormat = PaSampleFormat.paFloat32,
                suggestedLatency = _suggestedLatency,
                hostApiSpecificStreamInfo = nint.Zero,
            };

            delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> cbPtr = &Callback;

            var err = Native.Pa_OpenStream(
                out _stream,
                inputParameters: null,
                outputParameters: outParams,
                sampleRate: _format.SampleRate,
                framesPerBuffer: _framesPerBuffer,
                streamFlags: PaStreamFlags.paNoFlag,
                streamCallback: cbPtr,
                userData: GCHandle.ToIntPtr(_selfHandle));

            if (err != PaError.paNoError)
            {
                _selfHandle.Free();
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_OpenStream));
            }

            err = Native.Pa_StartStream(_stream);
            if (err != PaError.paNoError)
            {
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
                _selfHandle.Free();
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_StartStream));
            }

            // Capture the negotiated DAC latency so the master clock can report the audible position (A/V sync).
            var latencySec = Native.Pa_GetStreamInfo(_stream)?.outputLatency ?? _suggestedLatency;
            Volatile.Write(ref _outputLatencyTicks, latencySec > 0 ? (long)(latencySec * TimeSpan.TicksPerSecond) : 0);

            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            Volatile.Write(ref _streamStoppedAfterFlush, 0);
            Volatile.Write(ref _isRunning, true);
            Trace.LogDebug("Start: device={Device} channels={Ch} rate={Rate}Hz framesPerBuffer={Fpb} suggestedLatency={Latency}s ringCap={RingCapFrames}f targetQueue={TargetFrames}f",
                _deviceIndex, _format.Channels, _format.SampleRate, _framesPerBuffer, _suggestedLatency,
                _ring.CapacityFrames, TargetQueueSamples);
            timing?.SetOutcome($"device={_deviceIndex} format={_format} ring={_ring.CapacityFrames} target={TargetQueueSamples}");
        }
    }

    public void Stop()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Stop", slowWarningMs: 1000);
        lock (_streamLifecycleGate)
        {
            if (!Volatile.Read(ref _isRunning))
            {
                timing?.SetOutcome($"device={_deviceIndex} not-running");
                return;
            }
            Trace.LogDebug("Stop: device={Device} played={Played}f underrun={Underrun}f dropped={Dropped}f callbacks={Callbacks}",
                _deviceIndex, Volatile.Read(ref _playedSamples), Volatile.Read(ref _underrunSamples),
                Volatile.Read(ref _droppedSamples), Volatile.Read(ref _callbackCount));
            try
            {
                if (_stream != nint.Zero)
                {
                    Native.Pa_StopStream(_stream);
                    Native.Pa_CloseStream(_stream);
                    _stream = nint.Zero;
                }
            }
            finally
            {
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                Volatile.Write(ref _isRunning, false);
                Volatile.Write(ref _streamSmoothCalibrated, 0);
                Volatile.Write(ref _streamStoppedAfterFlush, 0);
                _ring.Clear();
            }
            timing?.SetOutcome($"device={_deviceIndex} played={Volatile.Read(ref _playedSamples)} underrun={Volatile.Read(ref _underrunSamples)} dropped={Volatile.Read(ref _droppedSamples)}");
        }
    }

    /// <summary>Convenience overload: submits a frame's samples after validating its format.</summary>
    public void Submit(in AudioFrame frame)
    {
        if (frame.Format != _format)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match output format {_format}", nameof(frame));
        Submit(frame.Samples.Span);
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCallbackFaulted();
        ThrowIfDeviceLost();
        EnsureStreamRunningAfterFlush();
        if (packedSamples.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_format.Channels}",
                nameof(packedSamples));

        var toWrite = _ring.Write(packedSamples);

        var dropped = packedSamples.Length - toWrite;
        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedSamples, dropped);
            var now = Environment.TickCount64;
            var prev = Volatile.Read(ref _lastSubmitDropLogTicks);
            if (now - prev >= 2000 || prev == 0)
            {
                if (Interlocked.CompareExchange(ref _lastSubmitDropLogTicks, now, prev) == prev)
                {
                    var frames = dropped / _format.Channels;
                    var total = Volatile.Read(ref _droppedSamples);
                    MediaDiagnostics.LogWarning(
                        $"PortAudioOutput: ring full - dropped {dropped} floats (~{frames} frames this Submit); " +
                        $"total DroppedSamples={total}. Prefill / TargetQueueSamples / stream-not-started windows can cause bursts.");
                }
            }
        }
    }

    /// <summary>
    /// Fills the ring from <paramref name="source"/> before <see cref="Start"/> (and before
    /// <see cref="AudioRouter.Start"/>) so the first callback has PCM ready. Optionally mirrors the
    /// same packed floats into <paramref name="mirrorPackedFloats"/> (same <see cref="AudioFormat"/>).
    /// </summary>
    /// <remarks>
    /// Target queue depth defaults to <c>max(sampleRate/10, chunkSamples×4)</c> samples per channel
    /// (same heuristic as the smoke tools). Stops if the ring stops accepting data (full / drops) so
    /// an oversized target cannot spin forever.
    /// </remarks>
    public void PrefillFrom(
        IAudioSource source,
        TimeSpan timeout,
        int chunkSamples,
        IAudioOutput? mirrorPackedFloats = null,
        int? targetQueuedSamplesOverride = null)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.PrefillFrom", slowWarningMs: 500);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format != _format)
            throw new ArgumentException("Source format must match this output's format.", nameof(source));
        if (mirrorPackedFloats is not null && mirrorPackedFloats.Format != _format)
            throw new ArgumentException("Mirror output format must match this output's format.", nameof(mirrorPackedFloats));
        if (chunkSamples < 16)
            throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var targetQueued = targetQueuedSamplesOverride ?? Math.Max(_format.SampleRate / 10, chunkSamples * 4);
        var ch = _format.Channels;
        var bufFloats = Math.Min(65536, Math.Max(chunkSamples * ch * 8, 8192 * ch));
        var buf = System.Buffers.ArrayPool<float>.Shared.Rent(bufFloats);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (QueuedSamples < targetQueued && DateTime.UtcNow < deadline)
            {
                var read = source.ReadInto(buf.AsSpan(0, bufFloats));
                if (read == 0) break;
                var span = buf.AsSpan(0, read);
                var q0 = QueuedSamples;
                Submit(span);
                mirrorPackedFloats?.Submit(span);
                if (QueuedSamples == q0 && read > 0) break;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(buf);
        }
        timing?.SetOutcome($"device={_deviceIndex} queued={QueuedSamples} target={targetQueued}");
    }

    /// <summary>
    /// <see cref="IClockedOutput"/> implementation: paces the router against the
    /// device's actual playback rate. Returns when adding
    /// <paramref name="chunkSamples"/> per channel would still leave the queued
    /// total at or below <see cref="TargetQueueSamples"/>; otherwise sleeps
    /// for as long as the device needs to consume the excess.
    /// </summary>
    /// <summary>
    /// A faulted stream callback returned <c>paAbort</c>, so PortAudio has killed the stream and the
    /// ring will never drain again. Surfacing it as a Submit exception routes the failure into the
    /// router's <c>OutputErrored</c> path instead of leaving silence with a frozen clock.
    /// </summary>
    private void ThrowIfCallbackFaulted()
    {
        if (Volatile.Read(ref _callbackFaulted) == 0)
            return;
        throw new InvalidOperationException(
            $"PortAudio stream callback faulted on device {_deviceIndex}; the stream was aborted",
            Volatile.Read(ref _callbackFaultException));
    }

    /// <summary>
    /// A lost device (stream went inactive under us) will never drain the ring again. Surfacing it as
    /// a Submit exception routes the failure into the router's <c>OutputErrored</c> path instead of
    /// leaving silence with a frozen clock.
    /// </summary>
    private void ThrowIfDeviceLost()
    {
        if (Volatile.Read(ref _deviceLost) == 0)
            return;
        throw new InvalidOperationException(
            $"PortAudio output device {_deviceIndex} was lost (stream stopped unexpectedly); the output is no longer draining");
    }

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0) return !token.IsCancellationRequested;

        // A faulted callback means the ring can never drain - fail pacing immediately instead of
        // burning the full 5s timeout below on every chunk.
        if (Volatile.Read(ref _callbackFaulted) != 0)
        {
            Trace.LogWarning("WaitForCapacity: stream callback faulted (device={Device}) - reporting no capacity", _deviceIndex);
            return false;
        }

        // Same for a lost device (checked before the not-running early-out: a latched output must
        // never report capacity again).
        if (Volatile.Read(ref _deviceLost) != 0)
        {
            Trace.LogWarning("WaitForCapacity: device lost (device={Device}) - reporting no capacity", _deviceIndex);
            return false;
        }

        // Before the stream is started PA isn't draining yet - pretend ready,
        // so prebuffering can fill the ring up to the target before Start().
        if (!Volatile.Read(ref _isRunning))
        {
            if (Trace.IsEnabled(LogLevel.Trace))
                Trace.LogTrace("WaitForCapacity: stream not running yet - returning ready (chunk={Chunk})", chunkSamples);
            return !token.IsCancellationRequested;
        }

        EnsureStreamRunningAfterFlush();

        var target = TargetQueueSamples;
        var startTicks = Environment.TickCount64;
        var deadlineTicks = startTicks + (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
        var nextHealthProbeTicks = startTicks + DeviceHealthProbeIntervalMs;
        while (!token.IsCancellationRequested)
        {
            // Cheap health probe while blocked on a full ring: if the device died, the ring never
            // drains and we would otherwise burn the whole 5s timeout before the router notices.
            // StreamActive latches _deviceLost under the lifecycle gate.
            if (Environment.TickCount64 >= nextHealthProbeTicks)
            {
                nextHealthProbeTicks = Environment.TickCount64 + DeviceHealthProbeIntervalMs;
                _ = StreamActive;
                if (Volatile.Read(ref _deviceLost) != 0)
                {
                    Trace.LogWarning("WaitForCapacity: device lost while waiting (device={Device}) - reporting no capacity", _deviceIndex);
                    return false;
                }
            }

            if (Environment.TickCount64 >= deadlineTicks)
            {
                var now = Environment.TickCount64;
                var prev = Volatile.Read(ref _waitForCapacityWarnTicks);
                if (now - prev >= 2000 || prev == 0)
                {
                    if (Interlocked.CompareExchange(ref _waitForCapacityWarnTicks, now, prev) == prev)
                        Trace.LogWarning("WaitForCapacity: timed out after 5s (queued={Queued}f target={Target}f played={Played}f underrun={Underrun}f cbCount={CB} streamActive={Active}) - router pacing will stall",
                            QueuedSamples, target, Volatile.Read(ref _playedSamples), Volatile.Read(ref _underrunSamples),
                            Volatile.Read(ref _callbackCount), StreamActive);
                }
                return false;
            }

            var queued = QueuedSamples;
            if (queued + chunkSamples <= target) return true;

            // Estimate how long until the device drains the excess. Add a 1ms
            // floor so we don't spin when we're only marginally over.
            var excessSamples = queued + chunkSamples - target;
            var waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excessSamples / _format.SampleRate));
            if (token.WaitHandle.WaitOne(waitMs)) return false;
        }
        return false;
    }

    /// <summary>
    /// Test-only drain: reads up to <paramref name="dst"/>.Length samples
    /// out of the ring buffer (bypassing the audio callback path).
    /// Used to verify wraparound and ring-buffer accounting without a real device.
    /// </summary>
    internal int TryDrainForTest(Span<float> dst) => _ring.Read(dst);

    private void EnsureStreamRunningAfterFlush()
    {
        if (Volatile.Read(ref _streamStoppedAfterFlush) == 0 || _stream == nint.Zero)
            return;

        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.RestartAfterFlush", slowWarningMs: 250);
        lock (_streamLifecycleGate)
        {
            // Re-check under the gate. Both the drainer (Submit) and run-loop (WaitForCapacity) threads
            // observe _streamStoppedAfterFlush==1 after a Flush and would otherwise each issue their own
            // Pa_StopStream + Pa_StartStream on the same handle; doing that concurrently wedges the native
            // backend (the deadlock this gate prevents). Whichever thread wins clears the flag, so the
            // other returns here without touching the stream.
            if (Volatile.Read(ref _streamStoppedAfterFlush) == 0 || _stream == nint.Zero)
                return;

            Trace.LogDebug("EnsureStreamRunningAfterFlush: restarting PA stream (played={Played}f epoch={Epoch}f)",
                Volatile.Read(ref _playedSamples), Volatile.Read(ref _playbackEpochSamples));
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            // Abort leaves the stream stopped; Stop+Start is more reliable than Start alone on some
            // backends when rebinding stream time after a flush segment reset.
            var err = Native.Pa_StopStream(_stream);
            if (err != PaError.paNoError && err != PaError.paStreamIsStopped)
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_StopStream));
            err = Native.Pa_StartStream(_stream);
            if (err != PaError.paNoError)
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_StartStream));
            Volatile.Write(ref _streamStoppedAfterFlush, 0);
            timing?.SetOutcome($"device={_deviceIndex} active={StreamActive}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(
        nint inputBuffer, nint outputBuffer, nuint frames,
        nint timeInfo, PaStreamCallbackFlags flags, nint userData)
    {
        PortAudioOutput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not PortAudioOutput s)
                return (int)PaStreamCallbackResult.paAbort;
            self = s;

            Interlocked.Increment(ref self._callbackCount);
            var totalFloats = (int)frames * self._format.Channels;
            var output = new Span<float>((float*)outputBuffer, totalFloats);

            var toRead = self._ring.Read(output);

            if (toRead > 0)
            {
                // _playedSamples is the lifetime per-channel frame counter
                // (preserved across Stop/Start) - separate from the ring read
                // pointer, which may reset on Stop or Flush.
                Interlocked.Add(ref self._playedSamples, toRead / self._format.Channels);
            }

            if (toRead < totalFloats)
            {
                output[toRead..].Clear();
                var underrunFrames = (totalFloats - toRead) / self._format.Channels;
                if (underrunFrames > 0)
                    Interlocked.Add(ref self._underrunSamples, underrunFrames);
            }

            if (Volatile.Read(ref self._streamSmoothCalibrated) == 0 && timeInfo != nint.Zero)
            {
                // PortAudio forbids Pa_GetStreamTime inside the stream callback; timeInfo is
                // synchronized with that clock (see portaudio.h PaStreamCallbackTimeInfo).
                var ti = *(PaStreamCallbackTimeInfo*)timeInfo;
                var st = ti.currentTime;
                if (double.IsFinite(st))
                {
                    var playedNow = Volatile.Read(ref self._playedSamples);
                    self._segmentPlayed0Samples = playedNow - Volatile.Read(ref self._playbackEpochSamples);
                    if (self._segmentPlayed0Samples < 0) self._segmentPlayed0Samples = 0;
                    self._segmentStreamT0 = st;
                    Thread.MemoryBarrier();
                    Volatile.Write(ref self._streamSmoothCalibrated, 1);
                }
            }

            return (int)PaStreamCallbackResult.paContinue;
        }
        catch (Exception ex)
        {
            // Throwing across the unmanaged boundary would crash the process.
            if (self is not null)
            {
                Interlocked.CompareExchange(ref self._callbackFaultException, ex, null);
                Volatile.Write(ref self._callbackFaulted, 1);
            }

            return (int)PaStreamCallbackResult.paAbort;
        }
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome($"device={_deviceIndex} already-disposed");
            return;
        }
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "PortAudioOutput.Dispose: Stop");
        MediaDiagnostics.SwallowDisposeErrors(PortAudioRuntime.Release, "PortAudioOutput.Dispose: PortAudioRuntime.Release");
        timing?.SetOutcome($"device={_deviceIndex} played={Volatile.Read(ref _playedSamples)} dropped={Volatile.Read(ref _droppedSamples)} underrun={Volatile.Read(ref _underrunSamples)}");
    }
}
