using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// General-purpose push-mode audio channel.
/// Callers write interleaved float frames via <see cref="WriteAsync"/> or <see cref="TryWrite"/>;
/// the mixer reads them via <see cref="FillBuffer"/> (non-blocking; fills silence on underrun).
/// </summary>
public sealed class AudioChannel : IAudioChannel
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(AudioChannel));

    private readonly Channel<float[]>       _channel;
    private readonly ChannelReader<float[]> _reader;
    private readonly ChannelWriter<float[]> _writer;

    // Pool of float[] chunks to avoid per-push allocations.
    private readonly ConcurrentQueue<float[]> _chunkPool = new();

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
    public event EventHandler? EndOfStream;

    /// <summary>
    /// Signals that no more audio will be written to this channel.
    /// Completes the internal ring and fires <see cref="EndOfStream"/> on the ThreadPool.
    /// Safe to call from any thread; idempotent after disposal.
    /// </summary>
    public void Complete()
    {
        if (_disposed) return;
        _writer.TryComplete();
        var handler = EndOfStream;
        if (handler == null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, h) = ((AudioChannel, EventHandler))s!;
            h(self, EventArgs.Empty);
        }, (this, handler));
    }

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

        Log.LogInformation("Created AudioChannel: {SampleRate}Hz/{Channels}ch, bufferDepth={BufferDepth}",
            sourceFormat.SampleRate, sourceFormat.Channels, bufferDepth);
    }

    // ── Push ──────────────────────────────────────────────────────────────

    public async ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buf = RentChunkBuffer(frames.Length);
        frames.Span.CopyTo(buf);
        await _writer.WriteAsync(buf, ct).ConfigureAwait(false);
        Interlocked.Add(ref _framesInRing, frames.Length / SourceFormat.Channels);
    }

    public bool TryWrite(ReadOnlySpan<float> frames)
    {
        if (_disposed) return false;
        var buf = RentChunkBuffer(frames.Length);
        frames.CopyTo(buf);
        if (!_writer.TryWrite(buf))
        {
            _chunkPool.Enqueue(buf);
            return false;
        }
        Interlocked.Add(ref _framesInRing, frames.Length / SourceFormat.Channels);
        return true;
    }

    // ── Pull (called from RT thread — no allocation, no blocking) ─────────

    /// <summary>
    /// Fills <paramref name="dest"/> with up to <paramref name="frameCount"/> interleaved
    /// frames from the ring buffer.  Handles partial chunks: each enqueued float[] may
    /// contain fewer samples than requested, so the method loops across chunks, tracking
    /// <c>_currentChunk</c>/<c>_currentOffset</c> as carry state.
    /// <para>On underrun (ring empty before <paramref name="frameCount"/> frames delivered),
    /// the remainder is filled with silence and a <see cref="BufferUnderrun"/> event is
    /// raised asynchronously via the ThreadPool (never blocks the RT thread).</para>
    /// </summary>
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
                    // Fully consumed — return to pool.
                    _chunkPool.Enqueue(_currentChunk);
                    _currentChunk = null;
                }
                if (!_reader.TryRead(out _currentChunk))
                {
                    // Underrun — fill remainder with silence
                    dest[filled..].Clear();
                    int consumed = filled / channels;
                    int dropped  = (totalSamples - filled) / channels;
                    // Update tracking for frames that were consumed before the underrun.
                    if (consumed > 0)
                    {
                        Interlocked.Add(ref _framesConsumed, consumed);
                        Interlocked.Add(ref _framesInRing, -consumed);
                    }
                    if (dropped > 0)
                        RaiseUnderrun(dropped);
                    return consumed;
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
        // Flush buffer — return chunks to pool
        if (_currentChunk != null)
            _chunkPool.Enqueue(_currentChunk);
        _currentChunk  = null;
        _currentOffset = 0;
        while (_reader.TryRead(out var chunk))
            _chunkPool.Enqueue(chunk);
        Interlocked.Exchange(ref _framesInRing, 0);
        Interlocked.Exchange(ref _framesConsumed,
            (long)(position.TotalSeconds * SourceFormat.SampleRate));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private float[] RentChunkBuffer(int minLength)
    {
        while (_chunkPool.TryDequeue(out var candidate))
        {
            if (candidate.Length >= minLength)
                return candidate;
            // Undersized — let GC collect it, keep looking for a suitable buffer.
        }

        return new float[minLength];
    }

    private void RaiseUnderrun(int framesDropped)
    {
        // Raise on a thread-pool thread so we never block the RT path.
        // Capture args in a tuple to avoid allocating a closure.
        var state = (Self: this, Pos: Position, Dropped: framesDropped);
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (self, pos, dropped) = ((AudioChannel, TimeSpan, int))s!;
            self.BufferUnderrun?.Invoke(self, new BufferUnderrunEventArgs(pos, dropped));
        }, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing AudioChannel: framesConsumed={FramesConsumed}",
            Interlocked.Read(ref _framesConsumed));
        _writer.TryComplete();
    }
}

