using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace S.Media.Audio.MiniAudio;

public sealed unsafe class MiniAudioOutput :
    IAudioOutput,
    IAudioOutputChannelCapabilities,
    IClockedOutput,
    IFlushableOutput,
    IPlaybackClock,
    IAudioOutputPlaybackStats,
    IAudioOutputLatency,
    IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.MiniAudio.MiniAudioOutput");
    private static readonly delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void> CallbackPtr = &Callback;
    private static readonly delegate* unmanaged[Cdecl]<nint, void> StopCallbackPtr = &StopNotification;

    /// <summary>Fallback period estimate before the first callback reveals the real one (miniaudio's low-latency default is 10 ms).</summary>
    private const double DefaultPeriodSeconds = 0.010;

    /// <summary>Device-side buffering in periods, used by <see cref="SubmitToOutputLatency"/>. The
    /// <c>periods</c> count is chosen natively and never surfaced to the managed side, so this is
    /// miniaudio's default (<c>MA_DEFAULT_PERIODS</c>).</summary>
    private const int EstimatedDevicePeriods = 3;

    private readonly AudioFormat _format;
    private readonly string? _deviceId;
    private readonly uint _periodSizeFrames;
    private readonly FrameAlignedFloatRing _ring;
    private readonly Lock _deviceLifecycleGate = new();

    private long _playedSamples;
    private long _droppedSamples;
    private long _underrunSamples;
    private long _callbackCount;
    private long _playbackEpochSamples;
    /// <summary>
    /// <see cref="IPlaybackClock.EpochId"/> paired with <see cref="_playbackEpochSamples"/>: a fresh
    /// <see cref="PlaybackEpoch.Next"/> id at every re-anchor of this clock (<see cref="Start"/>,
    /// <see cref="Flush"/>, device loss). Written under <see cref="_deviceLifecycleGate"/> with the sample
    /// epoch, so <see cref="Read"/> cannot pair an id from one segment with elapsed from another.
    /// </summary>
    private long _playbackEpochId = PlaybackEpoch.Next();
    private long _lastSubmitDropLogTicks;
    private int _deviceStoppedAfterFlush;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;
    /// <summary>1 once the device stopped without us asking (device lost/removed). Latched until the next <see cref="Start"/>.</summary>
    private int _deviceLost;
    /// <summary>1 while a deliberate stop (Stop/Flush/Dispose) is in flight or pending restart, so the native stop notification is not mistaken for device loss.</summary>
    private int _intentionalStopPending;
    /// <summary>Stopwatch timestamp taken at the end of the last data callback; 0 = no callback yet this segment.</summary>
    private long _lastCallbackTimestamp;
    /// <summary>Largest frameCount observed in a data callback - the device's effective period size.</summary>
    private int _maxCallbackFrames;
    /// <summary>Monotonic guard for <see cref="ElapsedSinceStart"/>; reset with the playback epoch on Start/Flush.</summary>
    private long _elapsedHighWaterTicks;
    private nint _device;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public MiniAudioOutput(
        AudioFormat format,
        string? deviceId = null,
        int framesPerBuffer = 0,
        int ringCapacityFrames = 16384)
    {
        format.Validate(nameof(format));
        if (framesPerBuffer < 0) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer));
        if (ringCapacityFrames < 64) throw new ArgumentOutOfRangeException(nameof(ringCapacityFrames), "must be >= 64");

        _format = format;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _periodSizeFrames = (uint)framesPerBuffer;

        _ring = new FrameAlignedFloatRing(format.Channels, (long)ringCapacityFrames * format.Channels);
        TargetQueueSamples = _ring.CapacityFrames / 2;
    }

    public AudioFormat Format => _format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        AudioOutputChannelCapabilities.Fixed(_format.Channels);

    public bool IsRunning => Volatile.Read(ref _isRunning);

    public int DeviceState
    {
        get
        {
            // Native calls must not race Stop()'s DeviceDestroy + FreeHGlobal (use-after-free);
            // the gate is reentrant and only contended during rare lifecycle transitions.
            lock (_deviceLifecycleGate)
                return _device != nint.Zero ? MiniAudioNative.DeviceGetState(_device) : 0;
        }
    }

    public long PlayedSamples => Volatile.Read(ref _playedSamples);

    public int QueuedSamples => _ring.BufferedFrames;

    public int CapacitySamples => _ring.CapacityFrames;

    public long DroppedSamples => Volatile.Read(ref _droppedSamples);

    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);

    public long CallbackCount => Volatile.Read(ref _callbackCount);

    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);

    /// <summary>
    /// True once the device stopped without a deliberate Stop/Flush (device unplugged, backend error).
    /// Latched from miniaudio's stop notification until the next <see cref="Start"/>; while latched,
    /// <see cref="Submit"/> throws, <see cref="WaitForCapacity"/> reports no capacity and
    /// <see cref="IsAdvancing"/> is false, so the router surfaces the loss via <c>OutputErrored</c>/<c>Faulted</c>
    /// instead of freezing the mastered clock silently. Best-effort: backends that reroute instead of
    /// stopping (e.g. WASAPI shared-mode default-device moves) may not fire the notification.
    /// </summary>
    public bool DeviceLost => Volatile.Read(ref _deviceLost) != 0;

    public int TargetQueueSamples { get; set; }

    /// <summary>
    /// <see cref="IPlaybackClock.ElapsedSinceStart"/>: monotonic <strong>audible</strong> playback time.
    /// Base is the consumed-sample counter (segment-local, like PortAudio); between data callbacks it is
    /// interpolated with wall time since the last callback (clamped to one device period so it can never
    /// run ahead of the next callback's sample count), and one device period is subtracted as the DAC-side
    /// latency this config knows about. In the healthy steady state the two effects cancel per callback, so
    /// the clock advances smoothly instead of in period-sized stair steps. A CAS high-water keeps the result
    /// monotonic per segment even across underrun-shortened callbacks. Residual accuracy: miniaudio buffers
    /// up to (periods − 1) additional periods internally that this property cannot see, so the reported
    /// position may still lead the speaker by up to that much (typically ≤ 2 periods, ~20 ms at defaults) -
    /// better than the previous raw counter (period stair steps, no latency subtraction) but not
    /// PortAudio's <c>Pa_GetStreamTime</c>-grade accuracy. No native calls on this read path.
    /// </summary>
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            var samples = Volatile.Read(ref _playedSamples) - Volatile.Read(ref _playbackEpochSamples);
            if (samples < 0) samples = 0;

            var periodSeconds = PeriodSeconds();
            double interpolatedSeconds = 0;
            var lastCallback = Volatile.Read(ref _lastCallbackTimestamp);
            if (lastCallback != 0
                && Volatile.Read(ref _isRunning)
                && Volatile.Read(ref _deviceStoppedAfterFlush) == 0
                && Volatile.Read(ref _deviceLost) == 0
                && Volatile.Read(ref _callbackFaulted) == 0)
            {
                interpolatedSeconds = ComputeCallbackInterpolationSeconds(
                    lastCallback, Stopwatch.GetTimestamp(), periodSeconds);
            }

            var audibleSeconds = samples / (double)_format.SampleRate + interpolatedSeconds - periodSeconds;
            var candidateTicks = audibleSeconds > 0 ? (long)(audibleSeconds * TimeSpan.TicksPerSecond) : 0;
            return TimeSpan.FromTicks(AdvanceElapsedHighWater(candidateTicks));
        }
    }

    public bool IsAdvancing
    {
        get
        {
            if (!Volatile.Read(ref _isRunning)
                || Volatile.Read(ref _deviceStoppedAfterFlush) != 0
                || Volatile.Read(ref _deviceLost) != 0)
                return false;
            lock (_deviceLifecycleGate)
                return _device != nint.Zero && MiniAudioNative.DeviceIsStarted(_device) != 0;
        }
    }

    /// <inheritdoc />
    public long EpochId => Volatile.Read(ref _playbackEpochId);

    /// <summary>
    /// <see cref="IPlaybackClock.Read"/>: taken under <see cref="_deviceLifecycleGate"/>, where every
    /// re-anchor writes the epoch pair, so the id and the elapsed always describe the same segment.
    /// <see cref="LatchDeviceLost"/> can bump the id from the native stop notification outside the gate;
    /// that direction is benign - a lost device's elapsed does not rewind, and a consumer's rule for a
    /// same-epoch regression is to hold rather than fold.
    /// </summary>
    public ClockReading Read()
    {
        lock (_deviceLifecycleGate)
            return new ClockReading(Volatile.Read(ref _playbackEpochId), ElapsedSinceStart, IsAdvancing);
    }

    /// <summary>
    /// Wall time to add on top of the consumed-sample count: elapsed Stopwatch time since the last data
    /// callback, clamped to one period (the most the next callback can add), never negative. Pure so the
    /// interpolation contract is unit-testable without a device.
    /// </summary>
    internal static double ComputeCallbackInterpolationSeconds(
        long lastCallbackTimestamp, long nowTimestamp, double periodSeconds)
    {
        if (lastCallbackTimestamp == 0 || periodSeconds <= 0)
            return 0;
        var wallSeconds = (nowTimestamp - lastCallbackTimestamp) / (double)Stopwatch.Frequency;
        if (wallSeconds <= 0)
            return 0;
        return Math.Min(wallSeconds, periodSeconds);
    }

    /// <summary>
    /// <see cref="IAudioOutputLatency.SubmitToOutputLatency"/>: the managed ring backlog plus the WHOLE
    /// device-side buffering behind it - the contract is submit→speaker, so it must not net out anything
    /// <see cref="ElapsedSinceStart"/> already removed. (It cannot: consumers read that clock relative to
    /// an epoch, and its constant one-period subtraction cancels in the difference.) The managed side only
    /// knows the period size, so the device side is estimated at <see cref="EstimatedDevicePeriods"/> ×
    /// period - miniaudio's default <c>periods</c> count, and the figure <see cref="ElapsedSinceStart"/>
    /// accounts for as one subtracted period plus a documented residual lead of up to two more. Estimate,
    /// not a measurement; no native calls on this path.
    /// </summary>
    public TimeSpan SubmitToOutputLatency =>
        TimeSpan.FromSeconds(QueuedSamples / (double)_format.SampleRate
                             + EstimatedDevicePeriods * PeriodSeconds());

    /// <summary>One device period in seconds: observed callback size, else the configured period, else miniaudio's 10 ms default.</summary>
    private double PeriodSeconds()
    {
        var frames = Volatile.Read(ref _maxCallbackFrames);
        if (frames <= 0 && _periodSizeFrames > 0)
            frames = (int)_periodSizeFrames;
        return frames > 0 ? frames / (double)_format.SampleRate : DefaultPeriodSeconds;
    }

    /// <summary>Lock-free CAS max: returns the greater of the candidate and the segment's previous report.</summary>
    private long AdvanceElapsedHighWater(long candidateTicks)
    {
        var prev = Volatile.Read(ref _elapsedHighWaterTicks);
        while (candidateTicks > prev)
        {
            var seen = Interlocked.CompareExchange(ref _elapsedHighWaterTicks, candidateTicks, prev);
            if (seen == prev)
                return candidateTicks;
            prev = seen;
        }

        return prev;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Start", slowWarningMs: 1000);
        lock (_deviceLifecycleGate)
        {
            if (_isRunning)
            {
                timing?.SetOutcome("already-running");
                return;
            }

            Interlocked.Exchange(ref _callbackFaultException, null);
            Volatile.Write(ref _callbackFaulted, 0);
            Volatile.Write(ref _deviceLost, 0);
            Volatile.Write(ref _intentionalStopPending, 0);
            Volatile.Write(ref _lastCallbackTimestamp, 0);
            Volatile.Write(ref _elapsedHighWaterTicks, 0);

            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            var deviceIdBytes = MiniAudioNative.ToUtf8NullTerminated(_deviceId);
            fixed (byte* deviceIdPtr = deviceIdBytes)
            {
                var createResult = MiniAudioNative.DeviceCreate(
                    (int)MiniAudioDeviceType.Playback,
                    deviceIdPtr,
                    (uint)_format.SampleRate,
                    (uint)_format.Channels,
                    _periodSizeFrames,
                    CallbackPtr,
                    GCHandle.ToIntPtr(_selfHandle),
                    out _device,
                    StopCallbackPtr);
                if (createResult != MiniAudioNative.Success)
                {
                    _selfHandle.Free();
                    MiniAudioException.ThrowIfError(createResult, "ma_device_init(playback)");
                }
            }

            var startResult = MiniAudioNative.DeviceStart(_device);
            if (startResult != MiniAudioNative.Success)
            {
                MiniAudioNative.DeviceDestroy(_device);
                _device = nint.Zero;
                _selfHandle.Free();
                MiniAudioException.ThrowIfError(startResult, "ma_device_start(playback)");
            }

            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
            Volatile.Write(ref _deviceStoppedAfterFlush, 0);
            Volatile.Write(ref _isRunning, true);
            Trace.LogDebug(
                "Start: channels={Channels} rate={Rate}Hz period={PeriodFrames} ringCap={RingCapFrames}f targetQueue={TargetFrames}f",
                _format.Channels,
                _format.SampleRate,
                _periodSizeFrames,
                CapacitySamples,
                TargetQueueSamples);
            timing?.SetOutcome($"format={_format} ring={CapacitySamples} target={TargetQueueSamples}");
        }
    }

    public void Stop()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Stop", slowWarningMs: 1000);
        lock (_deviceLifecycleGate)
        {
            if (!Volatile.Read(ref _isRunning))
            {
                timing?.SetOutcome("not-running");
                return;
            }

            // Deliberate stop: the native stop notification this triggers must not latch _deviceLost.
            // Stays set - the device is gone until the next Start, which clears it.
            Volatile.Write(ref _intentionalStopPending, 1);
            var device = _device;
            var stopResult = MiniAudioNative.Success;
            try
            {
                if (device != nint.Zero)
                {
                    stopResult = MiniAudioNative.DeviceStop(device);
                    MiniAudioNative.DeviceDestroy(device);
                    _device = nint.Zero;
                }
            }
            finally
            {
                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
                Volatile.Write(ref _isRunning, false);
                Volatile.Write(ref _deviceStoppedAfterFlush, 0);
                _ring.Clear();
            }

            MiniAudioException.ThrowIfError(stopResult, "ma_device_stop(playback)");
            timing?.SetOutcome(
                $"played={Volatile.Read(ref _playedSamples)} underrun={Volatile.Read(ref _underrunSamples)} dropped={Volatile.Read(ref _droppedSamples)}");
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCallbackFaulted();
        ThrowIfDeviceLost();
        EnsureDeviceRunningAfterFlush();
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
                    Trace.LogWarning(
                        "MiniAudioOutput: ring full; dropped {DroppedFloats} floats (~{Frames} frames this Submit); total DroppedSamples={Total}",
                        dropped,
                        frames,
                        Volatile.Read(ref _droppedSamples));
                }
            }
        }
    }

    /// <summary>
    /// A faulted device callback fills silence and the ring will never drain reliably again.
    /// Surfacing it as a Submit exception routes the failure into the router's
    /// <c>OutputErrored</c> path instead of leaving silence with a frozen clock.
    /// </summary>
    private void ThrowIfCallbackFaulted()
    {
        if (Volatile.Read(ref _callbackFaulted) == 0)
            return;
        throw new InvalidOperationException(
            "miniaudio playback callback faulted; the device is no longer draining",
            Volatile.Read(ref _callbackFaultException));
    }

    /// <summary>
    /// A lost device (unexpected native stop) will never drain the ring again. Surfacing it as a
    /// Submit exception routes the failure into the router's <c>OutputErrored</c> path instead of
    /// leaving silence with a frozen clock.
    /// </summary>
    private void ThrowIfDeviceLost()
    {
        if (Volatile.Read(ref _deviceLost) == 0)
            return;
        throw new InvalidOperationException(
            "miniaudio playback device was lost (stopped unexpectedly); the output is no longer draining");
    }

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0) return !token.IsCancellationRequested;

        // A lost device can never drain the ring - fail pacing immediately (checked before the
        // not-running early-out: a latched output must never report capacity again).
        if (Volatile.Read(ref _deviceLost) != 0)
        {
            Trace.LogWarning("WaitForCapacity: device lost - reporting no capacity");
            return false;
        }

        if (!Volatile.Read(ref _isRunning))
            return !token.IsCancellationRequested;

        // A faulted callback means the ring can never drain - fail pacing immediately instead of
        // burning the full 5s timeout below on every chunk.
        if (Volatile.Read(ref _callbackFaulted) != 0)
        {
            Trace.LogWarning("WaitForCapacity: device callback faulted - reporting no capacity");
            return false;
        }

        EnsureDeviceRunningAfterFlush();

        var target = TargetQueueSamples;
        var deadlineTicks = Environment.TickCount64 + (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
        while (!token.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= deadlineTicks)
                return false;

            if (QueuedSamples + chunkSamples <= target)
                return true;

            var excessSamples = QueuedSamples + chunkSamples - target;
            var waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excessSamples / _format.SampleRate));
            if (token.WaitHandle.WaitOne(waitMs))
                return false;
        }

        return false;
    }

    public void Flush()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Flush", slowWarningMs: 250);
        if (_disposed || !Volatile.Read(ref _isRunning) || _device == nint.Zero) return;
        lock (_deviceLifecycleGate)
        {
            if (_disposed || !Volatile.Read(ref _isRunning) || _device == nint.Zero) return;
            // Deliberate stop: the native stop notification this triggers must not latch _deviceLost.
            // Cleared by EnsureDeviceRunningAfterFlush once the device is deliberately restarted.
            Volatile.Write(ref _intentionalStopPending, 1);
            // A failing stop must not abort the flush half-way (ring/epoch/flag inconsistent) -
            // finish the bookkeeping regardless and only log, matching PortAudioOutput.Flush's
            // treatment of a failed Pa_AbortStream.
            var stopResult = MiniAudioNative.DeviceStop(_device);
            if (stopResult != MiniAudioNative.Success)
                Trace.LogWarning("Flush: ma_device_stop failed with {Result}; continuing flush bookkeeping", stopResult);
            _ring.Clear();
            Interlocked.Exchange(ref _underrunSamples, 0);
            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
            // Re-anchor the clock's interpolation state with the epoch (segment-local, like the epoch itself).
            Volatile.Write(ref _lastCallbackTimestamp, 0);
            Volatile.Write(ref _elapsedHighWaterTicks, 0);
            Volatile.Write(ref _deviceStoppedAfterFlush, 1);
            timing?.SetOutcome($"queued={QueuedSamples}");
        }
    }

    internal int TryDrainForTest(Span<float> destination) => _ring.Read(destination);

    private void EnsureDeviceRunningAfterFlush()
    {
        if (Volatile.Read(ref _deviceStoppedAfterFlush) == 0 || _device == nint.Zero)
            return;

        lock (_deviceLifecycleGate)
        {
            if (Volatile.Read(ref _deviceStoppedAfterFlush) == 0 || _device == nint.Zero)
                return;

            MiniAudioException.ThrowIfError(MiniAudioNative.DeviceStart(_device), "ma_device_start(playback after flush)");
            Volatile.Write(ref _deviceStoppedAfterFlush, 0);
            // The deliberate Flush stop is over; from here an unexpected stop is device loss again.
            Volatile.Write(ref _intentionalStopPending, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint userData, float* outputBuffer, float* inputBuffer, uint frameCount)
    {
        _ = inputBuffer;
        MiniAudioOutput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not MiniAudioOutput s)
                return;
            self = s;

            Interlocked.Increment(ref self._callbackCount);
            var totalFloats = checked((int)frameCount * self._format.Channels);
            if (outputBuffer is null)
            {
                Interlocked.CompareExchange(
                    ref self._callbackFaultException,
                    new InvalidOperationException("miniaudio playback callback received a null output buffer."),
                    null);
                Volatile.Write(ref self._callbackFaulted, 1);
                return;
            }

            var output = new Span<float>(outputBuffer, totalFloats);
            var toRead = self._ring.Read(output);

            if (toRead < totalFloats)
            {
                output[toRead..].Clear();
                var underrunFrames = (totalFloats - toRead) / self._format.Channels;
                if (underrunFrames > 0)
                    Interlocked.Add(ref self._underrunSamples, underrunFrames);
            }

            // Count only CONSUMED frames, matching PortAudioOutput: _playedSamples drives
            // ElapsedSinceStart (the A/V master clock when this output paces playback), and
            // counting underrun silence as played skipped the clock forward past the submitted
            // content on every dropout - permanent lip-sync drift on the miniaudio backend.
            if (toRead > 0)
                Interlocked.Add(ref self._playedSamples, toRead / self._format.Channels);

            // Clock interpolation state (ElapsedSinceStart): remember the device's effective period
            // and when this callback ran, so the read path can advance smoothly between callbacks.
            var callbackFrames = (int)frameCount;
            if (callbackFrames > Volatile.Read(ref self._maxCallbackFrames))
                Volatile.Write(ref self._maxCallbackFrames, callbackFrames);
            Volatile.Write(ref self._lastCallbackTimestamp, Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            if (self is not null)
            {
                Interlocked.CompareExchange(ref self._callbackFaultException, ex, null);
                Volatile.Write(ref self._callbackFaulted, 1);
            }

            if (outputBuffer is not null && self is not null)
                new Span<float>(outputBuffer, checked((int)frameCount * self._format.Channels)).Clear();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StopNotification(nint userData)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is MiniAudioOutput self)
                self.OnDeviceStoppedNotification();
        }
        catch
        {
            // A managed callback must never let an exception cross the native boundary.
        }
    }

    /// <summary>
    /// Runs on miniaudio's device worker thread whenever the device stops. Must never take
    /// <see cref="_deviceLifecycleGate"/> (some backends fire it synchronously from inside
    /// ma_device_stop/ma_device_uninit, which our lifecycle methods call while holding the gate) -
    /// it only reads latches and sets the device-lost latch.
    /// </summary>
    private void OnDeviceStoppedNotification()
    {
        if (!ShouldLatchDeviceLost(
                Volatile.Read(ref _isRunning),
                Volatile.Read(ref _intentionalStopPending) != 0,
                Volatile.Read(ref _deviceStoppedAfterFlush) != 0))
            return;
        LatchDeviceLost();
    }

    /// <summary>
    /// The device-loss decision, pure for unit testing: an unexpected native stop counts as device loss
    /// only while we believe the device is running and no deliberate Stop/Flush is in flight.
    /// </summary>
    internal static bool ShouldLatchDeviceLost(bool isRunning, bool intentionalStopPending, bool stoppedAfterFlush) =>
        isRunning && !intentionalStopPending && !stoppedAfterFlush;

    private void LatchDeviceLost()
    {
        if (Interlocked.Exchange(ref _deviceLost, 1) != 0)
            return;
        // The segment this clock was reporting is over; whatever a restart produces belongs to a new one.
        Volatile.Write(ref _playbackEpochId, PlaybackEpoch.Next());
        Trace.LogError(
            "miniaudio playback device stopped unexpectedly (lost/removed); failing Submit/WaitForCapacity so the router surfaces OutputErrored");
    }

    internal void ForceDeviceLostForTest() => LatchDeviceLost();

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome("already-disposed");
            return;
        }

        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "MiniAudioOutput.Dispose: Stop");
        timing?.SetOutcome(
            $"played={Volatile.Read(ref _playedSamples)} dropped={Volatile.Read(ref _droppedSamples)} underrun={Volatile.Read(ref _underrunSamples)}");
    }
}
