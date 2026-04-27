using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib;
using PALib.Types.Core;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;

namespace S.Media.PortAudio;

/// <summary>
/// How a <see cref="PortAudioEndpoint"/> is driven by PortAudio.
/// </summary>
public enum PortAudioDrivingMode
{
    /// <summary>
    /// PortAudio's RT callback pulls audio from the endpoint
    /// (zero-alloc hot path). The endpoint also implements
    /// <see cref="IPullAudioEndpoint"/>. Best for the primary hardware output
    /// driving the clock.
    /// </summary>
    Callback,

    /// <summary>
    /// A dedicated worker thread calls <c>Pa_WriteStream</c> with buffers
    /// pushed via <see cref="IAudioEndpoint.ReceiveBuffer"/>. The endpoint does
    /// <b>not</b> implement <see cref="IPullAudioEndpoint"/>. Best for secondary
    /// audio destinations where the primary endpoint already drives the clock.
    /// </summary>
    BlockingWrite,
}

/// <summary>
/// PortAudio-backed audio endpoint. <b>Single unified implementation</b> replacing the
/// legacy <c>PortAudioOutput</c> (callback/pull) and <c>PortAudioSink</c>
/// (blocking-write/push) split.
///
/// <para>
/// Both driving modes wrap a <c>Pa_OpenStream</c> handle and both expose the same
/// <see cref="PortAudioClock"/> (<c>Pa_GetStreamTime</c>) via
/// <see cref="IClockCapableEndpoint"/>, so <see cref="Clock"/> is valid from the moment
/// <see cref="Create"/> returns — users who want a push endpoint clocked by real
/// hardware get that for free.
/// </para>
///
/// <para>Mode selection is via <see cref="PortAudioDrivingMode"/>:</para>
/// <list type="bullet">
///   <item><see cref="PortAudioDrivingMode.Callback"/> — runtime type also implements
///     <see cref="IPullAudioEndpoint"/>; the PA RT callback pulls audio via
///     <see cref="IAudioFillCallback.Fill"/>. Zero-allocation hot path.</item>
///   <item><see cref="PortAudioDrivingMode.BlockingWrite"/> — pushes
///     <see cref="IAudioEndpoint.ReceiveBuffer"/> into a pre-allocated pool and a
///     dedicated worker thread issues blocking <c>Pa_WriteStream</c> calls.</item>
/// </list>
///
/// <para>
/// Use <see cref="Create"/> to construct a ready-to-register instance — the factory
/// opens the PA stream and creates the clock up front, so the endpoint can be passed
/// directly to <c>AVRouter.RegisterEndpoint(...)</c>.
/// </para>
/// </summary>
public abstract class PortAudioEndpoint : IAudioEndpoint, IClockCapableEndpoint, IDisposable
{
    private protected static readonly ILogger Log =
        PortAudioLogging.GetLogger(nameof(PortAudioEndpoint));

    private protected readonly PortAudioDrivingMode _mode;
    private protected readonly string               _deviceName;
    private protected readonly AudioFormat          _hardwareFormat;
    private protected readonly int                  _framesPerBuffer;
    private protected readonly PortAudioClock       _clock;

    private protected nint          _stream;
    private protected volatile bool _isRunning;
    private protected volatile bool _disposed;

    // ── IMediaEndpoint ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The PortAudio-negotiated hardware stream format.</summary>
    public AudioFormat HardwareFormat => _hardwareFormat;

    /// <summary>The driving mode this endpoint was created with.</summary>
    public PortAudioDrivingMode Mode => _mode;

    /// <inheritdoc cref="IClockCapableEndpoint.Clock"/>
    public IMediaClock Clock => _clock;

    /// <summary>Whether the endpoint is currently running.</summary>
    public bool IsRunning => _isRunning;

    // ── IAudioEndpoint ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public AudioFormat? NegotiatedFormat => _hardwareFormat;

    /// <inheritdoc/>
    public abstract void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, TimeSpan sourcePts);

    // ── Factory ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a PortAudio stream and returns a ready-to-register
    /// <see cref="PortAudioEndpoint"/>. The returned instance's <see cref="Clock"/>
    /// is valid immediately.
    /// </summary>
    /// <param name="device">Target audio device.</param>
    /// <param name="requestedFormat">Desired sample rate / channel count.</param>
    /// <param name="mode">Driving mode. Defaults to
    /// <see cref="PortAudioDrivingMode.Callback"/> (primary hardware output).</param>
    /// <param name="framesPerBuffer">Frames per callback/write block. 0 = driver-chosen in
    /// <see cref="PortAudioDrivingMode.Callback"/>; defaults to 512 in
    /// <see cref="PortAudioDrivingMode.BlockingWrite"/>.</param>
    /// <param name="suggestedLatency">
    /// Suggested output latency in seconds. When &gt; 0, this value is passed directly to
    /// <c>PaStreamParameters.suggestedLatency</c>. When ≤ 0 (default), the latency is
    /// derived from <paramref name="framesPerBuffer"/> or the device's default low-output latency.
    /// </param>
    /// <param name="name">Optional display name for diagnostics.</param>
    /// <param name="resampler">
    /// (BlockingWrite mode only) Optional rate converter used when an incoming buffer's
    /// rate differs from the negotiated hardware rate. When <see langword="null"/> a
    /// <see cref="LinearResampler"/> is created automatically and owned by the endpoint.
    /// </param>
    /// <param name="enableDriftCorrection">
    /// (BlockingWrite mode only) When <see langword="true"/>, a <see cref="DriftCorrector"/>
    /// monitors pending-write queue depth to compensate for hardware clock drift against
    /// the session leader clock.
    /// </param>
    public static PortAudioEndpoint Create(
        AudioDeviceInfo        device,
        AudioFormat            requestedFormat,
        PortAudioDrivingMode   mode                  = PortAudioDrivingMode.Callback,
        int                    framesPerBuffer       = 0,
        double                 suggestedLatency      = 0,
        string?                name                  = null,
        IAudioResampler?       resampler             = null,
        bool                   enableDriftCorrection = false)
    {
        if (mode == PortAudioDrivingMode.BlockingWrite && framesPerBuffer <= 0)
            framesPerBuffer = 512;

        return mode switch
        {
            PortAudioDrivingMode.Callback =>
                new CallbackEndpoint(device, requestedFormat, framesPerBuffer, suggestedLatency, name),
            PortAudioDrivingMode.BlockingWrite =>
                new BlockingWriteEndpoint(device, requestedFormat, framesPerBuffer, suggestedLatency,
                                          name, resampler, enableDriftCorrection),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    // ── Construction (shared) ─────────────────────────────────────────────

    private protected PortAudioEndpoint(
        AudioDeviceInfo      device,
        AudioFormat          requestedFormat,
        PortAudioDrivingMode mode,
        int                  framesPerBuffer,
        double               suggestedLatency,
        string?              name)
    {
        _mode       = mode;
        _deviceName = device.Name ?? device.Index.ToString();
        Name        = name ?? $"PortAudioEndpoint[{mode}]({_deviceName})";

        Log.LogInformation(
            "Opening PortAudioEndpoint: mode={Mode} device={DeviceName} (idx={DeviceIndex}) " +
            "format={SampleRate}Hz/{Channels}ch fpb={FramesPerBuffer} suggestedLatency={SuggestedLatency}s",
            mode, device.Name, device.Index, requestedFormat.SampleRate, requestedFormat.Channels,
            framesPerBuffer, suggestedLatency);

        var err = TryOpenStreamAttempt(device, requestedFormat, framesPerBuffer, suggestedLatency);

        // If the requested sample rate isn't supported, fall back to the device default.
        if (err == PaError.paInvalidSampleRate)
        {
            int deviceRate = device.DefaultSampleRate > 0
                ? (int)Math.Round(device.DefaultSampleRate)
                : 0;
            if (deviceRate > 0 && deviceRate != requestedFormat.SampleRate)
            {
                Log.LogWarning(
                    "Requested sample rate {RequestedRate}Hz not supported by '{DeviceName}'; " +
                    "falling back to device default {DeviceRate}Hz (router/resampler will convert)",
                    requestedFormat.SampleRate, device.Name, deviceRate);
                requestedFormat = requestedFormat with { SampleRate = deviceRate };
                err = TryOpenStreamAttempt(device, requestedFormat, framesPerBuffer, suggestedLatency);
            }
        }

        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"Pa_OpenStream failed: {Native.Pa_GetErrorText(err)} ({err})");

        var info = Native.Pa_GetStreamInfo(_stream);
        double actualRate   = info?.sampleRate ?? requestedFormat.SampleRate;
        int    actualFrames = framesPerBuffer > 0 ? framesPerBuffer : 512;
        _hardwareFormat  = requestedFormat with { SampleRate = (int)actualRate };
        _framesPerBuffer = actualFrames;

        // P1/CH8: clock is valid from the moment the ctor returns.
        _clock = PortAudioClock.Create(actualRate);
        _clock.SetStreamHandle(_stream, actualFrames);

        Log.LogInformation(
            "PortAudioEndpoint opened: mode={Mode} actualRate={ActualRate}Hz fpb={FramesPerBuffer} latency={Latency}s",
            mode, actualRate, actualFrames, info?.outputLatency ?? 0);
    }

    /// <summary>
    /// Performs the actual <c>Pa_OpenStream</c> call. Implemented by the subclass to
    /// supply the right callback / user-data pair and driving flags.
    /// </summary>
    private protected abstract PaError TryOpenStreamAttempt(
        AudioDeviceInfo device, AudioFormat format, int framesPerBuffer, double explicitLatency);

    private protected static double ComputeSuggestedLatency(
        AudioDeviceInfo device, AudioFormat format, int framesPerBuffer, double explicitLatency)
    {
        if (explicitLatency > 0) return explicitLatency;
        if (framesPerBuffer > 0 && format.SampleRate > 0)
            return framesPerBuffer / (double)format.SampleRate;
        return device.DefaultLowOutputLatency;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts the underlying PortAudio stream and transitions the endpoint
    /// into the running state.
    /// <para>
    /// <b>§3.28b / P4 — blocking behaviour:</b> <c>Pa_StartStream</c> can block
    /// for 100–300 ms on the WASAPI exclusive-mode backend while the audio
    /// engine negotiates buffer alignment with the driver. For most use cases
    /// (shared-mode devices, JACK, ALSA) it returns in well under 10 ms. If
    /// you are starting many endpoints in parallel and have identified this
    /// as a wall-clock hotspot, wrap the call in <see cref="Task.Run(Action)"/>
    /// at the call site so the starts can proceed concurrently.
    /// </para>
    /// </summary>
    public virtual Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning)
            return Task.CompletedTask;
        if (_stream == nint.Zero)
            throw new InvalidOperationException("PortAudioEndpoint stream is not open.");

        Log.LogInformation("Starting PortAudioEndpoint '{Name}'", Name);
        var err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"Pa_StartStream failed: {Native.Pa_GetErrorText(err)} ({err})");

        _clock.Start();
        _isRunning = true;
        Log.LogDebug("PortAudioEndpoint '{Name}' started", Name);
        return Task.CompletedTask;
    }

    public virtual Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;
        Log.LogInformation("Stopping PortAudioEndpoint '{Name}'", Name);
        return Task.Run(() =>
        {
            _isRunning = false;
            _clock.Stop();
            if (_stream != nint.Zero)
                Native.Pa_StopStream(_stream);
            Log.LogDebug("PortAudioEndpoint '{Name}' stopped", Name);
        }, ct);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing PortAudioEndpoint '{Name}' (mode={Mode})", Name, _mode);

        if (_isRunning)
        {
            _isRunning = false;
            if (_stream != nint.Zero)
                Native.Pa_AbortStream(_stream);
        }

        if (_stream != nint.Zero)
        {
            Native.Pa_CloseStream(_stream);
            _stream = nint.Zero;
        }

        _clock.Dispose();
    }

    // ── Subclass: Callback mode ───────────────────────────────────────────

    private sealed class CallbackEndpoint : PortAudioEndpoint, IPullAudioEndpoint
    {
        private GCHandle _gcHandle;
        private volatile IAudioFillCallback? _fillCallback;
        // Non-zero while the static PA callback is inside managed code. P5.
        private int _callbackInFlight;

        public CallbackEndpoint(
            AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer,
            double suggestedLatency, string? name)
            : base(device, requestedFormat, PortAudioDrivingMode.Callback,
                   framesPerBuffer, suggestedLatency, name)
        {
        }

        public IAudioFillCallback? FillCallback
        {
            get => _fillCallback;
            set
            {
                // §3.51 / CH5 — volatile swap + brief spin for in-flight fills.
                // Without the spin, the router's "Unregister" path
                //     pull.FillCallback = null;
                //     // ... tear down EndpointEntry ...
                // can race a PA callback that is still mid-Fill with the old
                // reference, producing use-after-free on the captured
                // EndpointEntry. `_fillCallback` is already volatile, so the
                // write alone is visible; the spin ensures the callback has
                // fully exited managed code before the caller proceeds.
                _fillCallback = value;
                if (value is null)
                {
                    var spin = new SpinWait();
                    int iterations = 0;
                    while (Volatile.Read(ref _callbackInFlight) != 0 && iterations++ < 10_000)
                        spin.SpinOnce();
                }
            }
        }

        public AudioFormat EndpointFormat => _hardwareFormat;
        public int FramesPerBuffer        => _framesPerBuffer;

        public override void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, TimeSpan sourcePts)
        {
            // Pull endpoints do not use push delivery.
        }

        private protected override unsafe PaError TryOpenStreamAttempt(
            AudioDeviceInfo device, AudioFormat format, int framesPerBuffer, double explicitLatency)
        {
            // P6: allocate GCHandle lazily per attempt so a failed first attempt
            // followed by a rate-fallback retry doesn't leak the handle.
            if (!_gcHandle.IsAllocated)
                _gcHandle = GCHandle.Alloc(this);

            double suggestedLatency = ComputeSuggestedLatency(device, format, framesPerBuffer, explicitLatency);
            var outParams = new PaStreamParameters
            {
                device                    = device.Index,
                channelCount              = format.Channels,
                sampleFormat              = PaSampleFormat.paFloat32,
                suggestedLatency          = suggestedLatency,
                hostApiSpecificStreamInfo = nint.Zero,
            };
            return Native.Pa_OpenStream(
                out _stream,
                inputParameters:  null,
                outputParameters: outParams,
                sampleRate:       format.SampleRate,
                framesPerBuffer:  framesPerBuffer > 0 ? (nuint)framesPerBuffer : 0,
                streamFlags:      PaStreamFlags.paNoFlag,
                streamCallback:   &StreamCallback,
                userData:         GCHandle.ToIntPtr(_gcHandle));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe int StreamCallback(
            nint input, nint output, nuint frameCount, nint timeInfo,
            PaStreamCallbackFlags flags, nint userData)
        {
            Span<float> dest = default;
            try
            {
                var handle = GCHandle.FromIntPtr(userData);
                if (!handle.IsAllocated) return (int)PaStreamCallbackResult.paAbort;
                var self = (CallbackEndpoint?)handle.Target;
                if (self is null || self._disposed) return (int)PaStreamCallbackResult.paAbort;

                Interlocked.Increment(ref self._callbackInFlight);
                try
                {
                    int channels     = self._hardwareFormat.Channels;
                    int totalFrames  = (int)frameCount;
                    int totalSamples = totalFrames * channels;
                    dest = new Span<float>((void*)output, totalSamples);

                    var fillCb = self._fillCallback;
                    if (fillCb is not null)
                        fillCb.Fill(dest, totalFrames, self._hardwareFormat);
                    else
                        dest.Clear();

                    return (int)PaStreamCallbackResult.paContinue;
                }
                finally
                {
                    Interlocked.Decrement(ref self._callbackInFlight);
                }
            }
            catch
            {
                if (!dest.IsEmpty) dest.Clear();
                return (int)PaStreamCallbackResult.paContinue;
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;

            // Null the fill callback before freeing the GCHandle so a late callback
            // reads null (and outputs silence) rather than a stale reference.
            _fillCallback = null;

            base.Dispose();

            // Spin until any in-flight callback has left the managed region before
            // freeing the GCHandle.
            var spin = new SpinWait();
            int iterations = 0;
            while (Volatile.Read(ref _callbackInFlight) != 0 && iterations++ < 10_000)
                spin.SpinOnce();

            if (_gcHandle.IsAllocated)
                _gcHandle.Free();
        }
    }

    // ── Subclass: BlockingWrite mode ──────────────────────────────────────

    private sealed class BlockingWriteEndpoint : PortAudioEndpoint
    {
        private readonly struct PendingWrite
        {
            public readonly float[] Buffer;
            public readonly int     Samples;
            public PendingWrite(float[] buffer, int samples) { Buffer = buffer; Samples = samples; }
        }

        private readonly IAudioResampler                _resampler;
        private readonly bool                           _ownsResampler;
        private readonly ConcurrentQueue<float[]>       _pool = new();
        private readonly PooledWorkQueue<PendingWrite>  _work = new();
        private readonly DriftCorrector?                _driftCorrector;

        private Thread?                  _writeThread;
        private CancellationTokenSource? _cts;
        private long _poolMissDrops;
        private long _capacityMissDrops;
        private long _resamplerMissDrops;
        private long _writeErrorCount;
        private PaError _lastWriteError;

        public long PoolMissDrops      => Interlocked.Read(ref _poolMissDrops);
        public long CapacityMissDrops  => Interlocked.Read(ref _capacityMissDrops);
        public long ResamplerMissDrops => Interlocked.Read(ref _resamplerMissDrops);
        public long WriteErrorCount    => Interlocked.Read(ref _writeErrorCount);
        public PaError LastWriteError  => _lastWriteError;
        public DriftCorrector? DriftCorrection => _driftCorrector;

        public BlockingWriteEndpoint(
            AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer,
            double suggestedLatency, string? name,
            IAudioResampler? resampler, bool enableDriftCorrection)
            : base(device, requestedFormat, PortAudioDrivingMode.BlockingWrite,
                   framesPerBuffer, suggestedLatency, name)
        {
            _resampler     = resampler ?? new LinearResampler();
            _ownsResampler = resampler is null;

            int bufSize           = _framesPerBuffer * _hardwareFormat.Channels;
            int poolBufferSamples = Math.Max(1, bufSize * 2);
            for (int i = 0; i < 8; i++)
                _pool.Enqueue(new float[poolBufferSamples]);

            if (enableDriftCorrection)
                _driftCorrector = new DriftCorrector(targetDepth: 3, ownerName: Name);

            if (_ownsResampler)
                Log.LogDebug("PortAudioEndpoint '{Name}': auto-created LinearResampler", Name);
        }

        private protected override unsafe PaError TryOpenStreamAttempt(
            AudioDeviceInfo device, AudioFormat format, int framesPerBuffer, double explicitLatency)
        {
            double suggestedLatency = ComputeSuggestedLatency(device, format, framesPerBuffer, explicitLatency);
            var outParams = new PaStreamParameters
            {
                device                    = device.Index,
                channelCount              = format.Channels,
                sampleFormat              = PaSampleFormat.paFloat32,
                suggestedLatency          = suggestedLatency,
                hostApiSpecificStreamInfo = nint.Zero,
            };
            return Native.Pa_OpenStream(
                out _stream,
                inputParameters:  null,
                outputParameters: outParams,
                sampleRate:       format.SampleRate,
                framesPerBuffer:  (nuint)framesPerBuffer,
                streamFlags:      PaStreamFlags.paNoFlag,
                streamCallback:   null,
                userData:         nint.Zero);
        }

        public override void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat, TimeSpan sourcePts)
        {
            if (!_isRunning) return;

            int outCh = _hardwareFormat.Channels;

            int writeFrames = SinkBufferHelper.ComputeWriteFrames(
                frameCount, sourceFormat.SampleRate, _hardwareFormat.SampleRate,
                _driftCorrector, _work.Count);
            int writeSamples = writeFrames * outCh;

            if (!_pool.TryDequeue(out var dest))
            {
                Interlocked.Increment(ref _poolMissDrops);
                return;
            }

            if (dest.Length < writeSamples)
            {
                _pool.Enqueue(dest);
                Interlocked.Increment(ref _capacityMissDrops);
                return;
            }

            if (sourceFormat.SampleRate != _hardwareFormat.SampleRate)
            {
                var rs = _resampler;
                if (rs == null)
                {
                    _pool.Enqueue(dest);
                    Interlocked.Increment(ref _resamplerMissDrops);
                    return;
                }

                int actualSamples = SinkBufferHelper.ResampleWithDrift(
                    rs, buffer, dest.AsSpan(0, writeSamples),
                    sourceFormat, _hardwareFormat.SampleRate, outCh, writeFrames);
                writeSamples = actualSamples;
                writeFrames  = outCh > 0 ? actualSamples / outCh : 0;
            }
            else
            {
                SinkBufferHelper.CopySameRate(buffer, dest.AsSpan(0, writeSamples),
                    frameCount, writeFrames, outCh);
            }

            _work.Enqueue(new PendingWrite(dest, writeSamples));
        }

        public override Task StartAsync(CancellationToken ct = default)
        {
            if (_isRunning)
                return Task.CompletedTask;

            var t = base.StartAsync(ct);

            _cts = new CancellationTokenSource();
            _driftCorrector?.Reset();
            Interlocked.Exchange(ref _writeErrorCount, 0);
            _lastWriteError = PaError.paNoError;

            _writeThread = new Thread(WriteLoop)
            {
                Name         = $"{Name}.WriteThread",
                IsBackground = true,
                Priority     = ThreadPriority.AboveNormal,
            };
            _writeThread.Start();
            return t;
        }

        public override Task StopAsync(CancellationToken ct = default)
        {
            if (!_isRunning) return Task.CompletedTask;
            Log.LogInformation("Stopping PortAudioEndpoint '{Name}' (BlockingWrite)", Name);
            return Task.Run(() =>
            {
                _isRunning = false;
                _cts?.Cancel();

                var thread = _writeThread;
                if (thread is not null)
                {
                    // B18: abort-on-join-timeout.
                    if (!thread.Join(TimeSpan.FromSeconds(3)))
                    {
                        Log.LogWarning(
                            "PortAudioEndpoint '{Name}': write thread did not exit in 3s; aborting stream",
                            Name);
                        if (_stream != nint.Zero)
                            Native.Pa_AbortStream(_stream);
                        thread.Join(TimeSpan.FromSeconds(1));
                    }
                }

                _writeThread = null;
                _cts?.Dispose();
                _cts = null;

                _work.Drain(p => _pool.Enqueue(p.Buffer));

                _clock.Stop();
                if (_stream != nint.Zero)
                    Native.Pa_StopStream(_stream);

                Log.LogDebug("PortAudioEndpoint '{Name}' stopped", Name);
            }, ct);
        }

        private unsafe void WriteLoop()
        {
            var token = _cts!.Token;
            while (!token.IsCancellationRequested)
            {
                if (!_work.WaitForItem(token)) break;

                while (_work.TryDequeue(out var pending))
                {
                    int framesToWrite = pending.Samples / _hardwareFormat.Channels;
                    PaError err;
                    fixed (float* ptr = pending.Buffer)
                        err = Native.Pa_WriteStream(_stream, (nint)ptr, (nuint)framesToWrite);

                    // B17: surface non-success returns (paOutputUnderflowed /
                    // paStreamIsStopped / transport errors). Log on transition + count.
                    if (err != PaError.paNoError)
                    {
                        long occurrences = Interlocked.Increment(ref _writeErrorCount);
                        if (_lastWriteError != err)
                        {
                            _lastWriteError = err;
                            Log.LogWarning(
                                "PortAudioEndpoint '{Name}': Pa_WriteStream returned {Err} ({ErrName}); occurrences={Count}",
                                Name, (int)err, Native.Pa_GetErrorText(err), occurrences);
                        }
                    }

                    _pool.Enqueue(pending.Buffer);
                }
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            Log.LogInformation(
                "Disposing PortAudioEndpoint '{Name}' (BlockingWrite): poolMissDrops={Pool}, " +
                "capacityMissDrops={Cap}, resamplerMissDrops={Rs}, writeErrors={We}, lastErr={Le}, driftRatio={Dr}",
                Name,
                Interlocked.Read(ref _poolMissDrops),
                Interlocked.Read(ref _capacityMissDrops),
                Interlocked.Read(ref _resamplerMissDrops),
                Interlocked.Read(ref _writeErrorCount),
                _lastWriteError,
                _driftCorrector?.CorrectionRatio ?? 1.0);

            _isRunning = false;
            _cts?.Cancel();

            var thread = _writeThread;
            if (thread is not null && !thread.Join(TimeSpan.FromSeconds(2)))
            {
                if (_stream != nint.Zero)
                    Native.Pa_AbortStream(_stream);
                thread.Join(TimeSpan.FromSeconds(1));
            }

            _work.Drain(p => _pool.Enqueue(p.Buffer));
            _work.Dispose();

            base.Dispose();

            if (_ownsResampler)
                _resampler.Dispose();
        }
    }
}
