using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IAudioSink"/> that forwards the mixed buffer to an NDI sender
/// via <see cref="NDISender.SendAudio"/>. Uses a pre-allocated pool so
/// <see cref="ReceiveBuffer"/> is allocation-free on the RT thread.
/// Data is converted from interleaved Float32 to NDI planar float (FLTP) on the write thread.
/// </summary>
public sealed class NdiAudioSink : IAudioSink
{
    private readonly NDISender    _sender;
    private readonly AudioFormat  _targetFormat;
    private readonly int          _framesPerBuffer;
    private readonly IAudioResampler? _resampler;
    private readonly bool         _ownsResampler;

    private readonly ConcurrentQueue<float[]> _pool    = new();
    private readonly ConcurrentQueue<float[]> _pending = new();

    private Thread?                  _writeThread;
    private CancellationTokenSource? _cts;
    private volatile bool            _running;
    private bool                     _disposed;

    public string Name      { get; }
    public bool   IsRunning => _running;

    public NdiAudioSink(
        NDISender        sender,
        AudioFormat      targetFormat,
        int              framesPerBuffer = 512,
        string?          name            = null,
        IAudioResampler? resampler       = null)
    {
        _sender          = sender;
        _targetFormat    = targetFormat;
        _framesPerBuffer = framesPerBuffer;
        Name             = name ?? "NdiAudioSink";

        // Auto-create a LinearResampler if the caller didn't supply one and we may receive
        // audio at a different rate (the actual check happens per buffer in ReceiveBuffer).
        if (resampler == null)
        {
            _resampler     = new S.Media.Core.Audio.LinearResampler();
            _ownsResampler = true;
        }
        else
        {
            _resampler     = resampler;
            _ownsResampler = false;
        }

        // Pre-allocate 8 interleaved buffers.
        for (int i = 0; i < 8; i++)
            _pool.Enqueue(new float[framesPerBuffer * targetFormat.Channels]);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cts     = new CancellationTokenSource();
        _running = true;
        _writeThread = new Thread(WriteLoop)
        {
            Name         = $"{Name}.WriteThread",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal
        };
        _writeThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    // ── ReceiveBuffer — RT thread, must not block or allocate ─────────────

    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat)
    {
        if (!_running) return;
        if (!_pool.TryDequeue(out var dest)) return; // pool exhausted — drop

        bool needsResample = sourceFormat.SampleRate != _targetFormat.SampleRate;
        if (needsResample && _resampler != null)
        {
            _resampler.Resample(buffer, dest.AsSpan(), sourceFormat, _targetFormat.SampleRate);
        }
        else
        {
            int copy = Math.Min(buffer.Length, dest.Length);
            buffer[..copy].CopyTo(dest.AsSpan());
        }
        _pending.Enqueue(dest);
    }

    // ── Write thread — interleaved → planar, then SendAudio ───────────────

    private unsafe void WriteLoop()
    {
        var token    = _cts!.Token;
        int channels = _targetFormat.Channels;
        int samples  = _framesPerBuffer;
        // Planar buffer: one plane per channel
        var planar   = new float[channels * samples];

        while (!token.IsCancellationRequested)
        {
            if (!_pending.TryDequeue(out var interleaved))
            { Thread.SpinWait(100); continue; }

            // Deinterleave: interleaved[s*ch+c] → planar[c*samples+s]
            for (int c = 0; c < channels; c++)
                for (int s = 0; s < samples; s++)
                    planar[c * samples + s] = interleaved[s * channels + c];

            _pool.Enqueue(interleaved); // return to pool

            fixed (float* pData = planar)
            {
                var frame = new NdiAudioFrameV3
                {
                    SampleRate           = _targetFormat.SampleRate,
                    NoChannels           = channels,
                    NoSamples            = samples,
                    FourCC               = NdiFourCCAudioType.Fltp,
                    PData                = (nint)pData,
                    ChannelStrideInBytes = samples * sizeof(float),
                    Timecode             = NdiConstants.TimecodeSynthesize
                };
                _sender.SendAudio(frame);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running  = false;
        _cts?.Cancel();
        _writeThread?.Join(TimeSpan.FromSeconds(2));
        if (_ownsResampler) _resampler?.Dispose();
    }
}

