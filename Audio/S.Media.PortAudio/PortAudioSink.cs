using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private readonly nint              _stream;
    private readonly AudioFormat       _targetFormat;
    private readonly int               _framesPerBuffer;
    private readonly IAudioResampler?  _resampler;

    // Lock-free pool: RT thread takes a buffer, write thread returns it.
    private readonly ConcurrentQueue<float[]> _pool    = new();
    private readonly ConcurrentQueue<float[]> _pending = new();

    private Thread?                   _writeThread;
    private CancellationTokenSource?  _cts;
    private volatile bool             _running;
    private bool                      _disposed;

    public string Name      { get; }
    public bool   IsRunning => _running;

    /// <param name="device">Target output device.</param>
    /// <param name="targetFormat">Hardware format this sink will write at.</param>
    /// <param name="framesPerBuffer">PA write block size (should match the leader's buffer size).</param>
    /// <param name="name">Optional display name for diagnostics.</param>
    /// <param name="resampler">
    /// Optional rate converter. When <see langword="null"/> and source rate differs from
    /// <paramref name="targetFormat"/>.SampleRate, a <see cref="LinearResampler"/> is used.
    /// </param>
    public unsafe PortAudioSink(
        AudioDeviceInfo  device,
        AudioFormat      targetFormat,
        int              framesPerBuffer = 512,
        string?          name           = null,
        IAudioResampler? resampler      = null)
    {
        _targetFormat    = targetFormat;
        _framesPerBuffer = framesPerBuffer;
        _resampler       = resampler;
        Name             = name ?? $"PortAudioSink({device.Name})";

        var outParams = new PaStreamParameters
        {
            device                    = device.Index,
            channelCount              = targetFormat.Channels,
            sampleFormat              = PaSampleFormat.paFloat32,
            suggestedLatency          = device.DefaultLowOutputLatency,
            hostApiSpecificStreamInfo = nint.Zero
        };

        // null streamCallback = blocking write mode
        var err = Native.Pa_OpenStream(
            out _stream,
            inputParameters:  null,
            outputParameters: outParams,
            sampleRate:       targetFormat.SampleRate,
            framesPerBuffer:  (nuint)framesPerBuffer,
            streamFlags:      PaStreamFlags.paNoFlag,
            streamCallback:   null,
            userData:         nint.Zero);

        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"PortAudioSink Pa_OpenStream failed: {Native.Pa_GetErrorText(err)} ({err})");

        // Pre-allocate 8 buffers (enough for ~170 ms at 48 kHz / 512 frames).
        int bufSize = framesPerBuffer * targetFormat.Channels;
        for (int i = 0; i < 8; i++)
            _pool.Enqueue(new float[bufSize]);
    }

    // ── IAudioSink lifecycle ──────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"PortAudioSink Pa_StartStream failed: {Native.Pa_GetErrorText(err)}");

        _cts         = new CancellationTokenSource();
        _running     = true;
        _writeThread = new Thread(WriteLoop)
        {
            Name       = $"{Name}.WriteThread",
            IsBackground = true,
            Priority   = ThreadPriority.AboveNormal
        };
        _writeThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(3));

        Native.Pa_StopStream(_stream);
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
        int writeFrames = sourceFormat.SampleRate == _targetFormat.SampleRate
            ? frameCount
            : (int)Math.Round((double)frameCount * _targetFormat.SampleRate / sourceFormat.SampleRate);
        int writeSamples = writeFrames * outCh;

        // Borrow a pool buffer. If the pooled buffer is too small (rate-mismatch first call),
        // allocate a new one — subsequent calls reuse the correctly-sized buffer from the pool.
        if (!_pool.TryDequeue(out var dest) || dest.Length < writeSamples)
            dest = new float[writeSamples];

        if (_resampler != null && sourceFormat.SampleRate != _targetFormat.SampleRate)
        {
            _resampler.Resample(buffer, dest.AsSpan(0, writeSamples), sourceFormat, _targetFormat.SampleRate);
        }
        else
        {
            int copy = Math.Min(buffer.Length, writeSamples);
            buffer[..copy].CopyTo(dest.AsSpan(0, copy));
        }

        _pending.Enqueue(dest);
    }

    // ── Write thread — calls Pa_WriteStream (blocking) ────────────────────

    private unsafe void WriteLoop()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            if (_pending.TryDequeue(out var buf))
            {
                // Use actual buffer length as the frame count so rate-adjusted writes are
                // correct even when writeFrames ≠ _framesPerBuffer.
                int framesToWrite = buf.Length / _targetFormat.Channels;
                fixed (float* ptr = buf)
                    Native.Pa_WriteStream(_stream, (nint)ptr, (nuint)framesToWrite);

                _pool.Enqueue(buf); // return buffer to pool
            }
            else
            {
                Thread.Yield(); // nothing pending — yield rather than spin-burn CPU
            }
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running  = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(2));
        Native.Pa_AbortStream(_stream);
        Native.Pa_CloseStream(_stream);
        _resampler?.Dispose();
    }
}

