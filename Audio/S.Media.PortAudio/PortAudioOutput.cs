using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PALib;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.PortAudio;

/// <summary>
/// PortAudio-backed <see cref="IAudioOutput"/>.
/// Opens a hardware stream in callback mode; the PA RT thread calls
/// <see cref="AudioMixer.FillOutputBuffer"/> directly (zero allocation in hot path).
/// </summary>
public sealed class PortAudioOutput : IAudioOutput
{
    private nint            _stream;
    private GCHandle        _gcHandle;
    private PortAudioClock? _clock;
    private AudioMixer?     _mixer;
    // Normally null (falls back to _mixer); set by AggregateOutput.OverrideRtMixer
    // to redirect the RT callback through the aggregate fan-out path.
    private volatile IAudioMixer? _activeMixer;
    private AudioFormat     _hardwareFormat;
    private int             _framesPerBuffer;
    private bool            _isRunning;
    private bool            _disposed;

    // ── IAudioOutput / IMediaOutput ───────────────────────────────────────

    public AudioFormat  HardwareFormat => _hardwareFormat;
    public IAudioMixer  Mixer          => _mixer  ?? throw new InvalidOperationException("Call Open() first.");
    public IMediaClock  Clock          => _clock  ?? throw new InvalidOperationException("Call Open() first.");
    public bool         IsRunning      => _isRunning;

    /// <inheritdoc/>
    public void OverrideRtMixer(IAudioMixer mixer) => _activeMixer = mixer;

    // ── Open ──────────────────────────────────────────────────────────────

    public unsafe void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream != nint.Zero)
            throw new InvalidOperationException("Output is already open. Close it before re-opening.");

        var outParams = new PaStreamParameters
        {
            device                    = device.Index,
            channelCount              = requestedFormat.Channels,
            sampleFormat              = PaSampleFormat.paFloat32,
            suggestedLatency          = device.DefaultLowOutputLatency,
            hostApiSpecificStreamInfo = nint.Zero
        };

        // Pin 'this' for the callback lifetime.
        _gcHandle = GCHandle.Alloc(this);

        var err = Native.Pa_OpenStream(
            out _stream,
            inputParameters:  null,
            outputParameters: outParams,
            sampleRate:       requestedFormat.SampleRate,
            framesPerBuffer:  framesPerBuffer > 0 ? (nuint)framesPerBuffer : 0,
            streamFlags:      PaStreamFlags.paNoFlag,
            streamCallback:   &StreamCallback,
            userData:         GCHandle.ToIntPtr(_gcHandle));

        if (err != PaError.paNoError)
        {
            _gcHandle.Free();
            throw new InvalidOperationException(
                $"Pa_OpenStream failed: {Native.Pa_GetErrorText(err)} ({err})");
        }

        // Read back the negotiated format.
        var info = Native.Pa_GetStreamInfo(_stream);
        double actualRate  = info?.sampleRate ?? requestedFormat.SampleRate;
        int    actualFrames = framesPerBuffer > 0 ? framesPerBuffer : 512; // sensible fallback
        _framesPerBuffer = actualFrames;
        _hardwareFormat = requestedFormat with { SampleRate = (int)actualRate };

        // Build clock and mixer.
        _clock = PortAudioClock.Create(actualRate);
        _clock.SetStreamHandle(_stream, actualFrames);

        _mixer = new AudioMixer(_hardwareFormat);
    }

    // ── Start / Stop ──────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureOpen();

        var err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"Pa_StartStream failed: {Native.Pa_GetErrorText(err)} ({err})");

        // Pre-allocate mixer scratch buffers before the RT callback can fire.
        _mixer!.PrepareBuffers(_framesPerBuffer > 0 ? _framesPerBuffer : 512);
        _clock!.Start();
        _isRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;

        // Pa_StopStream drains callbacks — run on a thread-pool thread to avoid blocking UI.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _clock?.Stop();
            _isRunning = false;

            if (_stream != nint.Zero)
                Native.Pa_StopStream(_stream);
        }, ct);
    }

    // ── RT callback — MUST NOT allocate, lock, or block ───────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int StreamCallback(
        nint                  input,
        nint                  output,
        nuint                 frameCount,
        nint                  timeInfo,
        PaStreamCallbackFlags flags,
        nint                  userData)
    {
        var self = (PortAudioOutput?)GCHandle.FromIntPtr(userData).Target;
        if (self is null) return (int)PaStreamCallbackResult.paAbort;

        var mixer = self._activeMixer ?? self._mixer;
        if (mixer is null) return (int)PaStreamCallbackResult.paAbort;

        int totalSamples = (int)frameCount * self._hardwareFormat.Channels;
        var dest = new Span<float>((void*)output, totalSamples);

        mixer.FillOutputBuffer(dest, (int)frameCount, self._hardwareFormat);
        return (int)PaStreamCallbackResult.paContinue;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void EnsureOpen()
    {
        if (_stream == nint.Zero)
            throw new InvalidOperationException("Call Open() before Start/Stop.");
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isRunning)
        {
            _clock?.Stop();
            _isRunning = false;
            if (_stream != nint.Zero)
                Native.Pa_AbortStream(_stream);
        }

        if (_stream != nint.Zero)
        {
            Native.Pa_CloseStream(_stream);
            _stream = nint.Zero;
        }

        if (_gcHandle.IsAllocated)
            _gcHandle.Free();

        _mixer?.Dispose();
        _clock?.Dispose();
    }
}

