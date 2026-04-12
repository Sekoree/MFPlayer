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

    public AudioFormat HardwareFormat => _hardwareFormat;
    public IMediaClock Clock          => _clock  ?? throw new InvalidOperationException("Call Open() first.");
    public bool        IsRunning      => _isRunning;

    /// <inheritdoc/>
    public void OverrideRtMixer(IAudioMixer mixer) => _activeMixer = mixer;

    // ── Open ──────────────────────────────────────────────────────────────

    public unsafe void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream != nint.Zero)
            throw new InvalidOperationException("Output is already open. Close it before re-opening.");

        double suggestedLatency = framesPerBuffer > 0 && requestedFormat.SampleRate > 0
            ? framesPerBuffer / (double)requestedFormat.SampleRate
            : device.DefaultLowOutputLatency;

        var outParams = new PaStreamParameters
        {
            device                    = device.Index,
            channelCount              = requestedFormat.Channels,
            sampleFormat              = PaSampleFormat.paFloat32,
            suggestedLatency          = suggestedLatency,
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
        double actualRate   = info?.sampleRate ?? requestedFormat.SampleRate;
        int    actualFrames = framesPerBuffer > 0 ? framesPerBuffer : 512; // sensible fallback
        _framesPerBuffer = actualFrames;
        _hardwareFormat = requestedFormat with { SampleRate = (int)actualRate };

        // Build clock and mixer, then pre-allocate buffers NOW so the RT callback
        // never has to allocate managed memory from a native thread (which can fast-fail).
        _clock = PortAudioClock.Create(actualRate);
        _clock.SetStreamHandle(_stream, actualFrames);

        _mixer = new AudioMixer(_hardwareFormat);
        _mixer.PrepareBuffers(actualFrames);
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

        // Buffers were pre-allocated during Open() so the RT callback never has to
        // allocate managed memory from a native thread.  Calling PrepareBuffers here
        // is only needed if channels were added after Open(); do it for safety but it
        // is a no-op when buffers are already the right size.
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
        // Wrap in try/catch: any managed exception escaping an [UnmanagedCallersOnly]
        // method causes a runtime fast-fail, killing the process silently.
        Span<float> dest = default;
        try
        {
            var self = (PortAudioOutput?)GCHandle.FromIntPtr(userData).Target;
            if (self is null) return (int)PaStreamCallbackResult.paAbort;

            var mixer = self._activeMixer ?? self._mixer;
            if (mixer is null) return (int)PaStreamCallbackResult.paAbort;

            int channels = self._hardwareFormat.Channels;
            int totalFrames = (int)frameCount;
            int totalSamples = totalFrames * channels;
            dest = new Span<float>((void*)output, totalSamples);

            // Some backends can request larger callback blocks than the stream was
            // opened/prepared for. Mix in bounded chunks to stay allocation-free.
            int maxChunkFrames = self._framesPerBuffer > 0 ? self._framesPerBuffer : 512;
            int offsetFrames = 0;
            while (offsetFrames < totalFrames)
            {
                int chunkFrames = Math.Min(maxChunkFrames, totalFrames - offsetFrames);
                int chunkOffsetSamples = offsetFrames * channels;
                int chunkSamples = chunkFrames * channels;
                mixer.FillOutputBuffer(
                    dest.Slice(chunkOffsetSamples, chunkSamples),
                    chunkFrames,
                    self._hardwareFormat);
                offsetFrames += chunkFrames;
            }
            return (int)PaStreamCallbackResult.paContinue;
        }
        catch
        {
            // Output silence and keep going rather than aborting the stream.
            if (!dest.IsEmpty)
                dest.Clear();
            return (int)PaStreamCallbackResult.paContinue;
        }
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

