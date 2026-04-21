using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
internal sealed class NDIAudioChannel : IAudioChannel
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIAudioChannel));

    private readonly NDIFrameSync        _frameSync;
    private readonly Lock                _frameSyncGate;
    private readonly NDIClock            _clock;
    private readonly int                 _requestedSampleRate;
    private readonly int                 _requestedChannels;
    // Capture interval in Stopwatch ticks: sub-microsecond precision avoids the
    // 0.333 ms/cycle surplus that integer milliseconds cause (1024*1000/48000 = 21 ms
    // vs true 21.333 ms), which filled the ring every ~1.3 s and triggered DropOldest.
    private readonly long                _captureIntervalTicks;
    private readonly int                 _waitPollMs;

    private Thread?                  _captureThread;
    private CancellationTokenSource  _cts = new();

    // DropOldest mode: when the ring is full the channel atomically removes the oldest
    // item itself — no separate TryRead from the capture thread, which previously raced
    // with the RT thread's TryRead and caused audible 1024-frame skips (cracks).
    // SingleReader = true: only the RT thread calls TryRead → faster lock-free path.
    private readonly Channel<float[]>       _ring;
    private readonly ChannelReader<float[]> _ringReader;
    private readonly ChannelWriter<float[]> _ringWriter;

    // Pre-allocated buffer pool: ringCapacity + 4 arrays, each sized for one NDI capture block.
    private readonly ConcurrentQueue<float[]> _pool = new();
    private readonly int _framesPerCapture;
    private readonly int _ringCapacity;           // actual ring slot count (≥ bufferDepth)

    private float[]? _currentChunk;
    private int      _currentOffset;
    private long     _framesConsumed;
    private long     _framesProduced;             // monotonic: total frames ever enqueued
    private bool     _disposed;

    public Guid        Id           { get; } = Guid.NewGuid();
    public AudioFormat SourceFormat { get; }
    public bool        IsOpen       => !_disposed;
    public bool        CanSeek      => false;
    public float       Volume       { get; set; } = 1.0f;
    public int         BufferDepth  { get; }
    public TimeSpan    Position =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _framesConsumed) / SourceFormat.SampleRate);
    public int         BufferAvailable
    {
        get
        {
            // produced − consumed gives an upper bound; cap at ring capacity because
            // BoundedChannelFullMode.DropOldest silently discards excess chunks.
            long inFlight = Interlocked.Read(ref _framesProduced) - Interlocked.Read(ref _framesConsumed);
            long cap = (long)_ringCapacity * _framesPerCapture;
            return (int)Math.Max(0, Math.Min(inFlight, cap));
        }
    }

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

#pragma warning disable CS0067  // NDI streams have no defined EOF; event may be used in future
    public event EventHandler? EndOfStream;
#pragma warning restore CS0067

    /// <param name="frameSync">NDIFrameSync created from the NDIReceiver for this source.</param>
    /// <param name="clock">NDIClock to update with each incoming frame's timestamp.</param>
    /// <param name="sampleRate">Desired output sample rate (passed to NDIFrameSync).</param>
    /// <param name="channels">Desired channel count.</param>
    /// <param name="bufferDepth">Ring buffer depth in chunks.</param>
    /// <param name="framesPerCapture">Samples per NDI capture call. Smaller values reduce latency
    /// but increase CPU overhead from more frequent calls. Default 1024 (~21 ms @ 48 kHz);
    /// use 256 (~5.3 ms) or 512 (~10.7 ms) for low-latency paths.</param>
    public NDIAudioChannel(
        NDIFrameSync frameSync,
        NDIClock     clock,
        Lock?        frameSyncGate = null,
        int          sampleRate  = 48000,
        int          channels    = 2,
        int          bufferDepth = 16,
        bool         preferLowLatency = false,
        int          framesPerCapture = 1024)
    {
        _frameSync            = frameSync;
        _frameSyncGate        = frameSyncGate ?? new Lock();
        _clock                = clock;
        _requestedSampleRate  = sampleRate;
        _requestedChannels    = channels;
        BufferDepth           = bufferDepth;
        _framesPerCapture     = Math.Clamp(framesPerCapture, 64, 4096);
        SourceFormat          = new AudioFormat(sampleRate, channels);
        _waitPollMs           = preferLowLatency ? 2 : 10;

        // Tick-accurate interval: Stopwatch.Frequency × framesPerCapture / sampleRate.
        // The integer truncation error is ~0.3 ns/cycle vs ~0.3 ms/cycle with ms arithmetic —
        // one million times smaller. DropOldest now triggers at most once every ~2 hours.
        _captureIntervalTicks = (long)((double)Stopwatch.Frequency * _framesPerCapture / sampleRate);

        // Ring capacity: with small framesPerCapture (e.g. 256 @ 48kHz = 5.3 ms/chunk) the
        // user's queue depth may be too few physical slots to absorb scheduling jitter.
        // Scale the internal ring to always hold at least ~100 ms of audio, preventing
        // BoundedChannelFullMode.DropOldest from silently discarding chunks under normal
        // operating conditions. The user-facing BufferDepth stays as requested.
        int minSlotsForHeadroom = (int)Math.Ceiling(sampleRate * 0.1 / _framesPerCapture);
        _ringCapacity = Math.Max(bufferDepth, minSlotsForHeadroom);

        _ring = Channel.CreateBounded<float[]>(
            new BoundedChannelOptions(_ringCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,   // only RT thread reads
                SingleWriter = true
            });
        _ringReader = _ring.Reader;
        _ringWriter = _ring.Writer;

        // Pre-allocate pool: ringCapacity + 4 to cover ring capacity + in-flight captures.
        int poolBufSize = _framesPerCapture * channels;
        for (int i = 0; i < _ringCapacity + 4; i++)
            _pool.Enqueue(new float[poolBufSize]);

        Log.LogInformation("Created NDIAudioChannel: {SampleRate}Hz/{Channels}ch, bufferDepth={BufferDepth}, ringCapacity={RingCapacity}, framesPerCapture={FramesPerCapture}",
            sampleRate, channels, bufferDepth, _ringCapacity, _framesPerCapture);
    }

    // ── Capture thread ────────────────────────────────────────────────

    public void StartCapture()
    {
        Log.LogInformation("Starting NDIAudioChannel capture thread");
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
        long minFrames = (long)Math.Clamp(minChunks, 1, BufferDepth) * _framesPerCapture;
        while ((Interlocked.Read(ref _framesProduced) - Interlocked.Read(ref _framesConsumed)) < minFrames
               && !ct.IsCancellationRequested)
            await Task.Delay(_waitPollMs, ct).ConfigureAwait(false);
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
                // Coarse sleep for anything more than ~5 ms out; spin-wait the tail.
                // On Linux, Thread.Sleep(1) can overshoot by 1–4 ms due to kernel
                // timer granularity, which accumulates and starves the PortAudio ring.
                // SpinWait burns CPU for the last few ms but gives microsecond accuracy.
                long remTicks = expectedTicks - nowTicks;
                if (remTicks > Stopwatch.Frequency / 200) // > ~5 ms
                {
                    int sleepMs = (int)(remTicks * 1000L / Stopwatch.Frequency) - 4;
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }
                else
                {
                    // Spin the final ≤5 ms — avoids accumulating jitter.
                    while (sw.ElapsedTicks < expectedTicks)
                        Thread.SpinWait(50);
                }
                continue;
            }
            // Advance to next absolute target — compensates if we woke up late.
            expectedTicks += _captureIntervalTicks;

            // ── Capture ───────────────────────────────────────────────────────
            try
            {
                NDIAudioFrameV3 frame;
                lock (_frameSyncGate)
                {
                    _frameSync.CaptureAudio(out frame,
                        _requestedSampleRate, _requestedChannels, _framesPerCapture);
                }

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
                lock (_frameSyncGate)
                    _frameSync.FreeAudio(frame); // release NDI buffer as soon as data is copied

                // With DropOldest mode the channel handles overflow atomically — no
                // separate TryRead loop here.  TryWrite returns false only after Dispose.
                if (_ringWriter.TryWrite(buf))
                    Interlocked.Add(ref _framesProduced, frame.NoSamples);
                else
                    _pool.Enqueue(buf);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log.LogWarning(ex, "NDIAudioChannel capture-loop error, retrying");
                Thread.Sleep(10);
            }
        }
    }

    private static unsafe void PlanarToInterleaved(NDIAudioFrameV3 frame, float[] dest)
    {
        // OPT-9: Use NDI SDK's SIMD-optimized conversion when available.
        // Pin the managed buffer and point the interleaved struct's PData at it.
        fixed (float* pDest = dest)
        {
            var interleaved = new NDIAudioInterleaved32f
            {
                SampleRate = frame.SampleRate,
                NoChannels = frame.NoChannels,
                NoSamples  = frame.NoSamples,
                Timecode   = frame.Timecode,
                PData      = (nint)pDest
            };

            if (NDIAudioUtils.ToInterleaved32f(frame, ref interleaved))
                return; // success — NDI filled the buffer with SIMD-optimized interleave
        }

        // Fallback: manual scalar loop (shouldn't normally be reached).
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
                        Interlocked.Add(ref _framesConsumed, consumed);
                    if (dropped > 0)
                    {
                        // Static delegate + value-tuple state avoids allocating a closure on the RT thread.
                        var state = (Self: this, Pos: Position, Dropped: dropped);
                        ThreadPool.QueueUserWorkItem(static s =>
                        {
                            var (self, pos, d) = ((NDIAudioChannel, TimeSpan, int))s!;
                            self.BufferUnderrun?.Invoke(self, new BufferUnderrunEventArgs(pos, d));
                        }, state);
                    }
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
        return frameCount;
    }

    // ── Push (not supported — NDI receive is pull-only) ───────────────────────

    // NDIAudioChannel implements only IAudioChannel, not IWritableAudioChannel —
    // data is produced by the internal NDI capture thread, so there is no external
    // write path exposed to callers.

    public void Seek(TimeSpan position) { /* NDI live sources cannot seek */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing NDIAudioChannel: framesConsumed={FramesConsumed}",
            Interlocked.Read(ref _framesConsumed));
        _cts.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _ringWriter.TryComplete();
    }
}

