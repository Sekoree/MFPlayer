using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// <see cref="IAudioChannel"/> that pulls audio from an NDI source via
/// <see cref="NDIFrameSync.CaptureAudio"/>.
/// Runs a background capture thread that writes interleaved Float32 samples into a
/// bounded ring buffer; the mixer reads via <see cref="FillBuffer"/>.
/// <para>
/// A pre-allocated pool of <c>float[]</c> arrays avoids per-frame heap allocations.
/// Fully consumed buffers are returned to the pool immediately after the RT pull.
/// </para>
/// </summary>
public sealed class NDIAudioChannel : IAudioChannel
{
    private readonly NDIFrameSync        _frameSync;
    private readonly NDIClock            _clock;
    private readonly int                 _requestedSampleRate;
    private readonly int                 _requestedChannels;
    // Capture interval in Stopwatch ticks: sub-microsecond precision avoids the
    // 0.333 ms/cycle surplus that integer milliseconds cause (1024*1000/48000 = 21 ms
    // vs true 21.333 ms), which filled the ring every ~1.3 s and triggered DropOldest.
    private readonly long                _captureIntervalTicks;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    // DropOldest mode: when the ring is full the channel atomically removes the oldest
    // item itself — no separate TryRead from the capture thread, which previously raced
    // with the RT thread's TryRead and caused audible 1024-frame skips (cracks).
    // SingleReader = true: only the RT thread calls TryRead → faster lock-free path.
    private readonly Channel<float[]>       _ring;
    private readonly ChannelReader<float[]> _ringReader;
    private readonly ChannelWriter<float[]> _ringWriter;

    // Pre-allocated buffer pool: bufferDepth + 4 arrays, each sized for one NDI capture block.
    private readonly ConcurrentQueue<float[]> _pool = new();
    private const int FramesPerCapture = 1024;

    private float[]? _currentChunk;
    private int      _currentOffset;
    private long     _framesConsumed;
    private long     _framesInRing;   // accurate frame count (not chunk count)
    private bool     _disposed;

    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => false;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }
    public TimeSpan    Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);
    public int         BufferAvailable => (int)Math.Max(0, Interlocked.Read(ref _framesInRing));

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <param name="frameSync">NDIFrameSync created from the NDIReceiver for this source.</param>
    /// <param name="clock">NDIClock to update with each incoming frame's timestamp.</param>
    /// <param name="sampleRate">Desired output sample rate (passed to NDIFrameSync).</param>
    /// <param name="channels">Desired channel count.</param>
    /// <param name="bufferDepth">Ring buffer depth in chunks.</param>
    public NDIAudioChannel(
        NDIFrameSync frameSync,
        NDIClock     clock,
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

        // Tick-accurate interval: Stopwatch.Frequency × 1024 / 48000.
        // The integer truncation error is ~0.3 ns/cycle vs ~0.3 ms/cycle with ms arithmetic —
        // one million times smaller. DropOldest now triggers at most once every ~2 hours.
        _captureIntervalTicks = (long)((double)Stopwatch.Frequency * FramesPerCapture / sampleRate);

        _ring = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(bufferDepth)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,   // only RT thread reads
                SingleWriter = true
            });
        _ringReader = _ring.Reader;
        _ringWriter = _ring.Writer;

        // Pre-allocate pool: bufferDepth + 4 to cover ring capacity + in-flight captures.
        int poolBufSize = FramesPerCapture * channels;
        for (int i = 0; i < bufferDepth + 4; i++)
            _pool.Enqueue(new float[poolBufSize]);
    }

    // ── Capture thread ────────────────────────────────────────────────

    public void StartCapture()
    {
        _captureThread = new Thread(CaptureLoop)
        {
            Name         = "NDIAudioChannel.Capture",
            IsBackground = true,
            Priority     = ThreadPriority.Highest
        };
        _captureThread.Start();
    }

    /// <summary>
    /// Waits asynchronously until the ring contains at least <paramref name="minChunks"/> captured
    /// audio chunks. Call this after <see cref="StartCapture"/> and before starting playback so the
    /// RT callback never fires on an empty ring.
    /// </summary>
    public async Task WaitForBufferAsync(int minChunks, CancellationToken ct = default)
    {
        // Use _framesInRing (not _ringReader.Count) because SingleReader = true means only
        // the RT thread may call Count/TryRead; reading from another thread is undefined.
        long minFrames = (long)Math.Clamp(minChunks, 1, BufferDepth) * FramesPerCapture;
        while (Interlocked.Read(ref _framesInRing) < minFrames && !ct.IsCancellationRequested)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }

    private void CaptureLoop()
    {
        // Tick-accurate absolute scheduler: fires at t=0, t+interval, t+2×interval …
        // Thread.Sleep wakeup jitter is compensated each cycle — no cumulative drift.
        var  sw             = Stopwatch.StartNew();
        long expectedTicks  = 0L;
        var  token          = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            // ── Wait for next scheduled slot ──────────────────────────────────
            long nowTicks = sw.ElapsedTicks;
            if (nowTicks < expectedTicks)
            {
                // Convert remaining ticks to ms; sleep most of the wait, then poll.
                long remMs = (expectedTicks - nowTicks) * 1000L / Stopwatch.Frequency;
                if (remMs > 2)
                    Thread.Sleep((int)(remMs - 2));
                else
                    Thread.Sleep(1);
                continue;
            }
            // Advance to next absolute target — compensates if we woke up late.
            expectedTicks += _captureIntervalTicks;

            // ── Capture ───────────────────────────────────────────────────────
            try
            {
                _frameSync.CaptureAudio(out var frame,
                    _requestedSampleRate, _requestedChannels, FramesPerCapture);

                // Guard: no samples or null data pointer → framesync returning silence placeholder.
                if (frame.NoSamples <= 0 || frame.PData == nint.Zero) continue;

                _clock.UpdateFromFrame(frame.Timestamp);

                // Borrow from pool (fallback to new alloc on pool exhaustion or size mismatch).
                int totalSamples = frame.NoSamples * frame.NoChannels;
                if (!_pool.TryDequeue(out var buf))
                {
                    buf = new float[totalSamples];
                }
                else if (buf.Length < totalSamples)
                {
                    _pool.Enqueue(buf);
                    buf = new float[totalSamples];
                }

                PlanarToInterleaved(frame, buf);
                _frameSync.FreeAudio(frame); // release NDI buffer as soon as data is copied

                // With DropOldest mode the channel handles overflow atomically — no
                // separate TryRead loop here.  TryWrite returns false only after Dispose.
                if (_ringWriter.TryWrite(buf))
                    Interlocked.Add(ref _framesInRing, frame.NoSamples);
                else
                    _pool.Enqueue(buf);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static unsafe void PlanarToInterleaved(NdiAudioFrameV3 frame, float[] dest)
    {
        int channels = frame.NoChannels;
        int samples  = frame.NoSamples;
        int stride   = frame.ChannelStrideInBytes / sizeof(float);
        float* pBase = (float*)frame.PData;

        for (int ch = 0; ch < channels; ch++)
        {
            float* pCh = pBase + ch * stride;
            for (int s = 0; s < samples; s++)
                dest[s * channels + ch] = pCh[s];
        }
    }

    // ── IAudioChannel pull (RT thread) ────────────────────────────────────

    public int FillBuffer(Span<float> dest, int frameCount)
    {
        int channels     = SourceFormat.Channels;
        int totalSamples = frameCount * channels;
        int filled       = 0;

        while (filled < totalSamples)
        {
            if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
            {
                // Return fully consumed chunk to pool before fetching next.
                if (_currentChunk != null)
                {
                    _pool.Enqueue(_currentChunk);
                    _currentChunk = null;
                }
                if (!_ringReader.TryRead(out _currentChunk))
                {
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
                        ThreadPool.QueueUserWorkItem(_ =>
                            BufferUnderrun?.Invoke(this,
                                new BufferUnderrunEventArgs(Position, dropped)));
                    return consumed;
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
        Interlocked.Add(ref _framesInRing, -frameCount);
        return frameCount;
    }

    // ── Push (not supported — NDI receive is pull-only) ───────────────────────

    /// <summary>Not supported. <see cref="NDIAudioChannel"/> is a receive-only channel fed by the NDI
    /// framesync capture thread; external writers would race with the capture thread and violate the
    /// <c>SingleWriter = true</c> contract on the internal ring.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default)
        => throw new NotSupportedException(
            "NDIAudioChannel is receive-only. Data is produced by the internal NDI capture thread.");

    /// <inheritdoc cref="WriteAsync"/>
    public bool TryWrite(ReadOnlySpan<float> frames)
        => throw new NotSupportedException(
            "NDIAudioChannel is receive-only. Data is produced by the internal NDI capture thread.");

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

