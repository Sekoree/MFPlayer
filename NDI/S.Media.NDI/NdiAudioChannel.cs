using System.Runtime.InteropServices;
using System.Threading.Channels;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IAudioChannel"/> that pulls audio from an NDI source via
/// <see cref="NDIFrameSync.CaptureAudio"/>.
/// Runs a background capture thread that writes interleaved Float32 samples
/// into a bounded ring buffer; the mixer reads via <see cref="FillBuffer"/>.
/// </summary>
public sealed class NdiAudioChannel : IAudioChannel
{
    private readonly NDIFrameSync        _frameSync;
    private readonly NdiClock            _clock;
    private readonly int                 _requestedSampleRate;
    private readonly int                 _requestedChannels;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    private readonly Channel<float[]>       _ring;
    private readonly ChannelReader<float[]> _ringReader;
    private readonly ChannelWriter<float[]> _ringWriter;

    private float[]? _currentChunk;
    private int      _currentOffset;
    private long     _framesConsumed;
    private bool     _disposed;

    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => false;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }
    public TimeSpan    Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);
    public int         BufferAvailable => _ringReader.Count;

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <param name="frameSync">NDIFrameSync created from the NDIReceiver for this source.</param>
    /// <param name="clock">NdiClock to update with each incoming frame's timestamp.</param>
    /// <param name="sampleRate">Desired output sample rate (passed to NDIFrameSync).</param>
    /// <param name="channels">Desired channel count.</param>
    /// <param name="bufferDepth">Ring buffer depth in chunks.</param>
    public NdiAudioChannel(
        NDIFrameSync frameSync,
        NdiClock     clock,
        int          sampleRate  = 48000,
        int          channels    = 2,
        int          bufferDepth = 16)
    {
        _frameSync            = frameSync;
        _clock                = clock;
        _requestedSampleRate  = sampleRate;
        _requestedChannels    = channels;
        BufferDepth           = bufferDepth;
        SourceFormat          = new AudioFormat(sampleRate, channels);

        _ring = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(bufferDepth)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // NDI is live — drop oldest on overflow
                SingleReader = true,
                SingleWriter = true
            });
        _ringReader = _ring.Reader;
        _ringWriter = _ring.Writer;
    }

    // ── Capture thread ────────────────────────────────────────────────────

    public void StartCapture()
    {
        _captureThread = new Thread(CaptureLoop)
        {
            Name         = "NdiAudioChannel.Capture",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        const int framesPerCapture = 1024;
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            _frameSync.CaptureAudio(out var frame,
                _requestedSampleRate, _requestedChannels, framesPerCapture);

            if (frame.NoSamples <= 0) { Thread.Sleep(1); continue; }

            _clock.UpdateFromFrame(frame.Timestamp);

            // NDI audio is FLTP (planar float). Convert to interleaved Float32.
            var interleaved = PlanarToInterleaved(frame);
            _ringWriter.TryWrite(interleaved);
        }
    }

    private static unsafe float[] PlanarToInterleaved(NdiAudioFrameV3 frame)
    {
        int channels   = frame.NoChannels;
        int samples    = frame.NoSamples;
        int stride     = frame.ChannelStrideInBytes / sizeof(float);
        var result     = new float[samples * channels];
        float* pBase   = (float*)frame.PData;

        for (int ch = 0; ch < channels; ch++)
        {
            float* pCh = pBase + ch * stride;
            for (int s = 0; s < samples; s++)
                result[s * channels + ch] = pCh[s];
        }
        return result;
    }

    // ── IAudioChannel pull ────────────────────────────────────────────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        int channels     = SourceFormat.Channels;
        int totalSamples = frameCount * channels;
        int filled       = 0;

        while (filled < totalSamples)
        {
            if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                if (!_ringReader.TryRead(out _currentChunk))
                {
                    dest[filled..].Clear();
                    int dropped = (totalSamples - filled) / channels;
                    if (dropped > 0)
                        ThreadPool.QueueUserWorkItem(_ =>
                            BufferUnderrun?.Invoke(this,
                                new BufferUnderrunEventArgs(Position, dropped)));
                    return filled / channels;
                }
                _currentOffset = 0;
            }
            int available = _currentChunk.Length - _currentOffset;
            int toCopy    = Math.Min(available, totalSamples - filled);
            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(dest[filled..]);
            filled         += toCopy;
            _currentOffset += toCopy;
        }

        Interlocked.Add(ref _framesConsumed, frameCount);
        return frameCount;
    }

    // ── Push (not applicable for NDI receive, provided for interface compat) ──

    public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
        => _ringWriter.WriteAsync(frames.ToArray(), ct);

    public bool TryWrite(ReadOnlySpan<float> frames)
        => _ringWriter.TryWrite(frames.ToArray());

    public void Seek(TimeSpan position) { /* NDI live sources cannot seek */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();
    }
}

