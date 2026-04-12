using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.PortAudio;

/// <summary>
/// <see cref="IAudioSink"/> backed by a PortAudio blocking-write stream.
/// Use as a secondary destination in <see cref="S.Media.Core.Audio.AggregateOutput"/>.
/// A pre-allocated buffer pool keeps <see cref="ReceiveBuffer"/> allocation-free on the RT thread.
/// </summary>
public sealed class PortAudioSink : IAudioSink
{
    private static readonly ILogger Log = PortAudioLogging.GetLogger(nameof(PortAudioSink));

    private readonly struct PendingWrite
    {
        public readonly float[] Buffer;
        public readonly int Samples;

        public PendingWrite(float[] buffer, int samples)
        {
            Buffer = buffer;
            Samples = samples;
        }
    }

    private readonly nint              _stream;
    private readonly AudioFormat       _targetFormat;
    private IAudioResampler?           _resampler;
    private bool                       _ownsResampler;

    // Lock-free pool: RT thread takes a buffer, write thread returns it.
    private readonly ConcurrentQueue<float[]> _pool    = new();
    private readonly ConcurrentQueue<PendingWrite> _pending = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);

    private Thread?                   _writeThread;
    private CancellationTokenSource?  _cts;
    private volatile bool             _running;
    private bool                      _disposed;
    private long                      _poolMissDrops;
    private long                      _capacityMissDrops;
    private long                      _resamplerMissDrops;
    private readonly DriftCorrector?  _driftCorrector;

    public string Name      { get; }
    public bool   IsRunning => _running;
    public long PoolMissDrops => Interlocked.Read(ref _poolMissDrops);
    public long CapacityMissDrops => Interlocked.Read(ref _capacityMissDrops);
    public long ResamplerMissDrops => Interlocked.Read(ref _resamplerMissDrops);

    /// <summary>
    /// The drift corrector instance, or <see langword="null"/> if drift correction is disabled.
    /// Use <see cref="DriftCorrector.CorrectionRatio"/> and <see cref="DriftCorrector.TotalCalls"/>
    /// to monitor correction behaviour at runtime.
    /// </summary>
    public DriftCorrector? DriftCorrection => _driftCorrector;

    /// <param name="device">Target output device.</param>
    /// <param name="targetFormat">Hardware format this sink will write at.</param>
    /// <param name="framesPerBuffer">PA write block size (should match the leader's buffer size).</param>
    /// <param name="name">Optional display name for diagnostics.</param>
    /// <param name="resampler">
    /// Optional rate converter used when source rate differs from
    /// <paramref name="targetFormat"/>.SampleRate. To keep <see cref="ReceiveBuffer"/> allocation-free,
    /// mismatched-rate buffers are dropped when this is <see langword="null"/>.
    /// </param>
    /// <param name="enableDriftCorrection">
    /// When <see langword="true"/>, a <see cref="DriftCorrector"/> monitors the pending-write
    /// queue depth and adjusts the per-buffer output frame count by ±1 frame to compensate for
    /// hardware clock drift between this sink and the leader output. Recommended for long-running
    /// sessions with multiple outputs on independent hardware clocks.
    /// </param>
    public unsafe PortAudioSink(
        AudioDeviceInfo  device,
        AudioFormat      targetFormat,
        int              framesPerBuffer = 512,
        string?          name           = null,
        IAudioResampler? resampler      = null,
        bool             enableDriftCorrection = false)
    {
        _resampler       = resampler;
        _ownsResampler   = false;
        Name             = name ?? $"PortAudioSink({device.Name})";

        Log.LogInformation("Creating PortAudioSink '{Name}': device={DeviceName} (idx={DeviceIndex}), format={SampleRate}Hz/{Channels}ch, fpb={FramesPerBuffer}",
            Name, device.Name, device.Index, targetFormat.SampleRate, targetFormat.Channels, framesPerBuffer);

        var err = TryOpenSinkStream(device, targetFormat, framesPerBuffer, out _stream);

        // If the requested sample rate isn't supported, fall back to the device's
        // default rate.  The AudioMixer in AggregateOutput will resample automatically.
        if (err == PaError.paInvalidSampleRate)
        {
            int deviceRate = device.DefaultSampleRate > 0
                ? (int)Math.Round(device.DefaultSampleRate)
                : 0;
            if (deviceRate > 0 && deviceRate != targetFormat.SampleRate)
            {
                Log.LogWarning("Requested sample rate {RequestedRate}Hz not supported by '{DeviceName}'; " +
                               "falling back to device default {DeviceRate}Hz (AudioMixer will resample)",
                    targetFormat.SampleRate, device.Name, deviceRate);
                targetFormat = targetFormat with { SampleRate = deviceRate };
                err = TryOpenSinkStream(device, targetFormat, framesPerBuffer, out _stream);
            }
        }

        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"PortAudioSink Pa_OpenStream failed: {Native.Pa_GetErrorText(err)} ({err})");

        _targetFormat = targetFormat;

        // Keep enough headroom for common rate-conversion ratios without resizing on the RT path.
        int bufSize = framesPerBuffer * targetFormat.Channels;
        int poolBufferSamples = Math.Max(1, bufSize * 2);
        for (int i = 0; i < 8; i++)
            _pool.Enqueue(new float[poolBufferSamples]);

        if (enableDriftCorrection)
            _driftCorrector = new DriftCorrector(targetDepth: 3, ownerName: Name);

        Log.LogDebug("PortAudioSink '{Name}' opened: poolSize={PoolSize}, bufSamples={BufSamples}, driftCorrection={Drift}",
            Name, 8, poolBufferSamples, enableDriftCorrection);
    }

    // ── IAudioSink lifecycle ──────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.LogInformation("Starting PortAudioSink '{Name}'", Name);
        var err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"PortAudioSink Pa_StartStream failed: {Native.Pa_GetErrorText(err)}");

        _cts         = new CancellationTokenSource();
        _running     = true;
        _driftCorrector?.Reset();
        _writeThread = new Thread(WriteLoop)
        {
            Name       = $"{Name}.WriteThread",
            IsBackground = true,
            Priority   = ThreadPriority.AboveNormal
        };
        _writeThread.Start();
        Log.LogDebug("PortAudioSink '{Name}' started", Name);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Log.LogInformation("Stopping PortAudioSink '{Name}'", Name);
        _running = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(3));

        Native.Pa_StopStream(_stream);
        Log.LogDebug("PortAudioSink '{Name}' stopped", Name);
        return Task.CompletedTask;
    }

    // ── ReceiveBuffer — called on RT thread, MUST NOT block or allocate ───

    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
    {
        if (!_running) return;

        int outCh = _targetFormat.Channels;

        // Compute rate-adjusted output frame count.
        // When the sink's hardware rate differs from the leader's, writing the wrong number of
        // frames to Pa_WriteStream causes the secondary stream to drift (or stall):
        //   leader 48 kHz / 1024 frames → 21.33 ms; sink at 44.1 kHz needs 941 frames for the same window.
        int nominalWriteFrames = sourceFormat.SampleRate == _targetFormat.SampleRate
            ? frameCount
            : (int)Math.Round((double)frameCount * _targetFormat.SampleRate / sourceFormat.SampleRate);

        // Apply drift correction: adjusts the frame count by ±1 occasionally to keep
        // the pending-write queue stable, compensating for hardware clock drift.
        int writeFrames = _driftCorrector != null
            ? _driftCorrector.CorrectFrameCount(nominalWriteFrames, _pending.Count)
            : nominalWriteFrames;
        int writeSamples = writeFrames * outCh;

        // Borrow a pool buffer. RT path never allocates: drop when no capacity is available.
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

        if (sourceFormat.SampleRate != _targetFormat.SampleRate)
        {
            // Cross-rate: resampler output sized for the drift-corrected frame count.
            // The resampler's stateful phase accumulator handles ±1 frame naturally.
            var rs = _resampler;
            if (rs == null)
            {
                _pool.Enqueue(dest);
                Interlocked.Increment(ref _resamplerMissDrops);
                return;
            }

            rs.Resample(buffer, dest.AsSpan(0, writeSamples), sourceFormat, _targetFormat.SampleRate);
        }
        else
        {
            // Same rate: direct copy. When drift correction adjusts the frame count,
            // we hold the last frame for extra output frames or copy fewer frames.
            int copyFrames  = Math.Min(frameCount, writeFrames);
            int copySamples = copyFrames * outCh;
            buffer[..copySamples].CopyTo(dest.AsSpan(0, copySamples));

            if (writeFrames > frameCount && frameCount > 0)
            {
                // Hold last frame for drift-correction extra frames.
                var lastFrame = buffer.Slice((frameCount - 1) * outCh, outCh);
                for (int f = copyFrames; f < writeFrames; f++)
                    lastFrame.CopyTo(dest.AsSpan(f * outCh, outCh));
            }
        }

        _pending.Enqueue(new PendingWrite(dest, writeSamples));
        _pendingSignal.Release();
    }

    // ── Write thread — calls Pa_WriteStream (blocking) ────────────────────

    private unsafe void WriteLoop()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _pendingSignal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_pending.TryDequeue(out var pending))
            {
                // Use actual buffer length as the frame count so rate-adjusted writes are
                // correct even when writeFrames != _framesPerBuffer.
                int framesToWrite = pending.Samples / _targetFormat.Channels;
                fixed (float* ptr = pending.Buffer)
                    Native.Pa_WriteStream(_stream, (nint)ptr, (nuint)framesToWrite);

                _pool.Enqueue(pending.Buffer); // return buffer to pool
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to open a PA blocking-write stream. Returns the PA error code
    /// so the caller can retry with a different rate on <c>paInvalidSampleRate</c>.
    /// </summary>
    private static unsafe PaError TryOpenSinkStream(
        AudioDeviceInfo device, AudioFormat format, int framesPerBuffer, out nint stream)
    {
        double suggestedLatency = framesPerBuffer > 0 && format.SampleRate > 0
            ? framesPerBuffer / (double)format.SampleRate
            : device.DefaultLowOutputLatency;

        var outParams = new PaStreamParameters
        {
            device                    = device.Index,
            channelCount              = format.Channels,
            sampleFormat              = PaSampleFormat.paFloat32,
            suggestedLatency          = suggestedLatency,
            hostApiSpecificStreamInfo = nint.Zero
        };

        // null streamCallback = blocking write mode
        return Native.Pa_OpenStream(
            out stream,
            inputParameters:  null,
            outputParameters: outParams,
            sampleRate:       format.SampleRate,
            framesPerBuffer:  (nuint)framesPerBuffer,
            streamFlags:      PaStreamFlags.paNoFlag,
            streamCallback:   null,
            userData:         nint.Zero);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing PortAudioSink '{Name}': poolMissDrops={PoolMissDrops}, capacityMissDrops={CapacityMissDrops}, resamplerMissDrops={ResamplerMissDrops}, driftRatio={DriftRatio}",
            Name, Interlocked.Read(ref _poolMissDrops), Interlocked.Read(ref _capacityMissDrops), Interlocked.Read(ref _resamplerMissDrops),
            _driftCorrector?.CorrectionRatio ?? 1.0);
        _running  = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(2));

        // Drain pending writes so pooled buffers are not leaked.
        while (_pending.TryDequeue(out var p))
            _pool.Enqueue(p.Buffer);

        Native.Pa_AbortStream(_stream);
        Native.Pa_CloseStream(_stream);
        _pendingSignal.Dispose();
        if (_ownsResampler)
            _resampler?.Dispose();
        Log.LogDebug("PortAudioSink '{Name}' disposed", Name);
    }
}

