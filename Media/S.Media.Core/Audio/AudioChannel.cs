using System.Threading.Channels;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// General-purpose push-mode audio channel.
/// Callers write interleaved float frames via <see cref="WriteAsync"/> or <see cref="TryWrite"/>;
/// the mixer reads them via <see cref="FillBuffer"/> (non-blocking; fills silence on underrun).
/// </summary>
public sealed class AudioChannel : IAudioChannel
{
    private readonly Channel<float[]>       _channel;
    private readonly ChannelReader<float[]> _reader;
    private readonly ChannelWriter<float[]> _writer;

    // Partial-read state (current chunk + offset within it)
    private float[]? _currentChunk;
    private int      _currentOffset;

    private long     _framesConsumed;
    private long     _framesInRing;   // tracks frames currently in the ring buffer
    private volatile bool _disposed;

    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => false;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }

    public TimeSpan Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);

    public int BufferAvailable => (int)Math.Max(0, Interlocked.Read(ref _framesInRing));

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <param name="sourceFormat">Native PCM format of data written by the producer.</param>
    /// <param name="bufferDepth">Number of chunks the ring buffer can hold before back-pressuring.</param>
    public AudioChannel(AudioFormat sourceFormat, int bufferDepth = 8)
    {
        SourceFormat = sourceFormat;
        BufferDepth  = bufferDepth;

        var options = new BoundedChannelOptions(bufferDepth)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<float[]>(options);
        _reader  = _channel.Reader;
        _writer  = _channel.Writer;
    }

    // ── Push ──────────────────────────────────────────────────────────────

    public async ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writer.WriteAsync(frames.ToArray(), ct).ConfigureAwait(false);
        Interlocked.Add(ref _framesInRing, frames.Length / SourceFormat.Channels);
    }

    public bool TryWrite(ReadOnlySpan<float> frames)
    {
        if (_disposed) return false;
        if (!_writer.TryWrite(frames.ToArray())) return false;
        Interlocked.Add(ref _framesInRing, frames.Length / SourceFormat.Channels);
        return true;
    }

    // ── Pull (called from RT thread — no allocation, no blocking) ─────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        int channels     = SourceFormat.Channels;
        int totalSamples = frameCount * channels;
        int filled       = 0;

        while (filled < totalSamples)
        {
            // Refill current chunk if exhausted
            if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                if (_currentChunk != null)
                {
                    // Fully consumed — account for any remaining frames already subtracted sample-by-sample
                    _currentChunk = null;
                }
                if (!_reader.TryRead(out _currentChunk))
                {
                    // Underrun — fill remainder with silence
                    dest[filled..].Clear();
                    int dropped = (totalSamples - filled) / channels;
                    if (dropped > 0)
                        RaiseUnderrun(dropped);
                    return filled / channels;
                }
                _currentOffset = 0;
            }

            int available = _currentChunk.Length - _currentOffset;
            int needed    = totalSamples - filled;
            int toCopy    = Math.Min(available, needed);

            _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(dest[filled..]);
            filled         += toCopy;
            _currentOffset += toCopy;
        }

        Interlocked.Add(ref _framesConsumed, frameCount);
        Interlocked.Add(ref _framesInRing, -frameCount);
        return frameCount;
    }

    public void Seek(TimeSpan position)
    {
        // Flush buffer
        _currentChunk  = null;
        _currentOffset = 0;
        while (_reader.TryRead(out _)) { }
        Interlocked.Exchange(ref _framesInRing, 0);
        Interlocked.Exchange(ref _framesConsumed,
            (long)(position.TotalSeconds * SourceFormat.SampleRate));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void RaiseUnderrun(int framesDropped)
    {
        // Raise on a thread-pool thread so we never block the RT path.
        var args = new BufferUnderrunEventArgs(Position, framesDropped);
        ThreadPool.QueueUserWorkItem(_ => BufferUnderrun?.Invoke(this, args));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.TryComplete();
    }
}

