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
    private CancellationTokenSource? _cts;
    private int                      _captureStartedFlag;  // §3.46 — 0/1 CAS guard

    // DropOldest mode: when the ring is full the channel atomically removes the oldest
    // item itself — no separate TryRead from the capture thread, which previously raced
    // with the RT thread's TryRead and caused audible 1024-frame skips (cracks).
    // §3.47b / N9 — switched from `BoundedChannelFullMode.DropOldest` to manual
    // drop-oldest on an unbounded channel because the bounded mode silently
    // discards overflow without our pool ever seeing the evicted array, leaking
    // `float[]` allocations into the GC heap over time. The manual drain path
    // (TryRead → return to pool → TryWrite new) also keeps `_framesProduced`
    // strictly monotonic: we only increment it for chunks we actually enqueued,
    // and the evicted chunks decrement the implied in-flight count via a
    // matching ring-counter Decrement.
    private readonly Channel<float[]>       _ring;
    private readonly ChannelReader<float[]> _ringReader;
    private readonly ChannelWriter<float[]> _ringWriter;
    // §3.47b — explicit in-flight counter so we don't rely on Channel internals
    // to tell us when the ring is at capacity.
    private long _framesInRing;

    // Pre-allocated buffer pool: ringCapacity + 4 arrays, each sized for one NDI capture block.
    private readonly ConcurrentQueue<float[]> _pool = new();
    private readonly int _framesPerCapture;
    private readonly int _ringCapacity;           // actual ring slot count (≥ bufferDepth)
    // §4.20 / N10 — log-once set for unsupported audio FourCCs encountered on capture.
    private readonly HashSet<NDIFourCCAudioType> _unsupportedAudioFourCcLogged = [];

    /// <summary>
    /// §4.17 / N7 — raised once per distinct unsupported audio FourCC.
    /// <para>§2.8 — dispatched on the NDI audio capture thread. Handlers must be fast;
    /// do not block or call back into the capture path.</para>
    /// </summary>
    public event EventHandler<NDIUnsupportedFourCcEventArgs>? UnsupportedFourCc;

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
            // §3.47b — read the explicit in-flight counter (set by the manual
            // drop-oldest path). Multiply by frames-per-chunk to match the
            // IAudioChannel contract (frames, not chunks).
            long chunks = Interlocked.Read(ref _framesInRing);
            return (int)Math.Max(0, Math.Min(chunks, _ringCapacity)) * _framesPerCapture;
        }
    }

    /// <summary>
    /// §2.8 — raised on the NDI audio capture thread when the ring drains
    /// below threshold. Handlers must be non-blocking.
    /// </summary>
    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// §2.8 — NDI live sources have no defined end-of-stream; this event exists
    /// for interface compatibility and is never raised in practice.
    /// </summary>
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
    // §4.16 / N4 — clock-write policy resolved at construction. Null means
    // "legacy: always write". For FirstWriter the channel always calls
    // TryUpdateFromFrame and relies on the NDIClock CAS.
    private readonly NDIClockPolicy _clockPolicy;

    public NDIAudioChannel(
        NDIFrameSync frameSync,
        NDIClock     clock,
        Lock?        frameSyncGate = null,
        int          sampleRate  = 48000,
        int          channels    = 2,
        int          bufferDepth = 16,
        bool         preferLowLatency = false,
        int          framesPerCapture = 1024,
        NDIClockPolicy clockPolicy = NDIClockPolicy.Both)
    {
        _frameSync            = frameSync;
        _frameSyncGate        = frameSyncGate ?? new Lock();
        _clock                = clock;
        _clockPolicy          = clockPolicy;
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

        _ring = Channel.CreateUnbounded<float[]>(
            new UnboundedChannelOptions
            {
                // SingleReader MUST stay false: the capture thread TryReads (to
                // evict the oldest chunk on full) while the RT thread TryReads
                // during FillBuffer. Two distinct readers = lost wakeups under
                // SingleReader=true.
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
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
        // §3.41 — refuse to start a disposed channel; previously the latched _cts.Token
        // would silently capture cancellation already requested by Dispose, leaving the
        // capture thread to spin once and exit with no diagnostic.
        ObjectDisposedException.ThrowIf(_disposed, this);

        // §3.46 — atomic "already started" guard. The legacy code allowed two concurrent
        // capture threads if StartCapture raced itself, both racing _frameSync.
        if (Interlocked.CompareExchange(ref _captureStartedFlag, 1, 0) != 0)
        {
            Log.LogDebug("NDIAudioChannel.StartCapture called twice; ignoring second invocation");
            return;
        }

        // §3.41 — fresh CTS per Start so a previous Dispose / stop cannot pre-cancel
        // the new capture loop.
        _cts = new CancellationTokenSource();

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
        // Snapshot the per-start CTS so a Dispose-after-Start that nulls _cts cannot
        // throw NRE inside the loop.
        var  cts            = _cts;
        if (cts is null) return;
        var  token          = cts.Token;

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
            NDIAudioFrameV3 frame = default;
            bool haveFrame = false;
            try
            {
                lock (_frameSyncGate)
                {
                    _frameSync.CaptureAudio(out frame,
                        _requestedSampleRate, _requestedChannels, _framesPerCapture);
                }
                haveFrame = true;

                // Guard: no samples or null data pointer → framesync returning silence placeholder.
                if (frame.NoSamples <= 0 || frame.PData == nint.Zero) continue;

                // §4.20 / N10 — PlanarToInterleaved assumes the canonical Fltp FourCC
                // layout (32-bit float planar, channel stride in bytes).
                if (frame.FourCC != NDIFourCCAudioType.Fltp)
                {
                    // §4.17 / N7 — first-sighting of a new FourCC logs + fires
                    // the public event. Subsequent frames with the same
                    // FourCC are silently dropped (the original log-once
                    // behaviour is preserved).
                    if (_unsupportedAudioFourCcLogged.Add(frame.FourCC))
                    {
                        Log.LogWarning(
                            "NDIAudioChannel: unsupported audio FourCC={FourCC}; expected Fltp. Frame dropped.",
                            frame.FourCC);
                        try { UnsupportedFourCc?.Invoke(this, new NDIUnsupportedFourCcEventArgs((uint)frame.FourCC, isAudio: true)); }
                        catch (Exception ex) { Log.LogWarning(ex, "UnsupportedFourCc handler threw"); }
                    }
                    continue;
                }

                // §4.16 / N4 — clock-policy-aware write. AudioPreferred and
                // Both always write; VideoPreferred skips here so the video
                // channel is the sole leader; FirstWriter uses the NDIClock
                // CAS so whichever channel publishes first wins.
                switch (_clockPolicy)
                {
                    case NDIClockPolicy.Both:
                    case NDIClockPolicy.AudioPreferred:
                        _clock.UpdateFromFrame(frame.Timestamp);
                        break;
                    case NDIClockPolicy.FirstWriter:
                        _clock.TryUpdateFromFrame(frame.Timestamp, NDIClock.WriterClaimAudio);
                        break;
                    // VideoPreferred: skip — video channel is the sole writer.
                }

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

                // §3.47b — manual drop-oldest on an unbounded channel. We
                // explicitly evict the oldest chunk (returning it to the pool)
                // before enqueueing the new one, so the pool size stays stable
                // and `_framesProduced` only counts chunks that were actually
                // enqueued without subsequent eviction.
                if (Interlocked.Read(ref _framesInRing) >= _ringCapacity)
                {
                    if (_ringReader.TryRead(out var evicted))
                    {
                        long afterEvict = Interlocked.Decrement(ref _framesInRing);
                        Debug.Assert(afterEvict >= 0,
                            "NDIAudioChannel._framesInRing went negative on manual drop-oldest");
                        _pool.Enqueue(evicted);
                    }
                }

                if (_ringWriter.TryWrite(buf))
                {
                    Interlocked.Increment(ref _framesInRing);
                    Interlocked.Add(ref _framesProduced, frame.NoSamples);
                }
                else
                {
                    // TryWrite on an unbounded channel fails only after Complete.
                    _pool.Enqueue(buf);
                }
            }
            catch (OperationCanceledException) { /* cooperative */ }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // §3.44 / N6 — narrow log + type tag; the `finally` block still
                // frees the NDI buffer we captured above.
                Log.LogWarning(ex, "NDIAudioChannel capture-loop error [{ExceptionType}], retrying",
                    ex.GetType().Name);
                Thread.Sleep(10);
            }
            finally
            {
                // §3.44 / N6 — FreeAudio unconditionally; previously any `continue`
                // branch that skipped the free leaked the NDI internal buffer.
                if (haveFrame && frame.PData != nint.Zero)
                {
                    try { lock (_frameSyncGate) _frameSync.FreeAudio(frame); }
                    catch (Exception ex) { Log.LogWarning(ex, "FreeAudio threw"); }
                }
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
                // §3.47b — pair every TryRead with a matching _framesInRing decrement.
                long afterRead = Interlocked.Decrement(ref _framesInRing);
                Debug.Assert(afterRead >= 0,
                    "NDIAudioChannel._framesInRing went negative on FillBuffer");
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
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        // §3.42 / N19 — loop-join: retry until the capture thread actually exits
        // rather than giving up after a hard timeout. The thread's CTS is cancelled
        // above so it will exit promptly once any in-flight FreeAudio / framesync
        // call returns; the loop guards against a slow SDK call that takes > 2 s.
        LoopJoin(_captureThread, "audio-capture");
        cts?.Dispose();
        _ringWriter.TryComplete();

        // §3.47h (cosmetic) — return any leftover ring chunks to the pool so a
        // diagnostic dump after Dispose shows zero pool churn rather than the
        // rented-but-never-returned tail.
        while (_ringReader.TryRead(out var chunk))
        {
            Interlocked.Decrement(ref _framesInRing);
            _pool.Enqueue(chunk);
        }
    }

    // §3.42 / N19 — retry join in a loop so a slow NDI SDK call cannot leave the
    // caller holding a stale (post-Dispose) reference to the capture thread's resources.
    private static void LoopJoin(Thread? thread, string name)
    {
        if (thread is null) return;
        int timeoutMs = 500;
        while (!thread.Join(timeoutMs))
        {
            Log.LogWarning(
                "NDI {ThreadName} thread still alive after {Timeout} ms — retrying join",
                name, timeoutMs);
            timeoutMs = Math.Min(timeoutMs * 2, 5_000);
        }
    }
}

