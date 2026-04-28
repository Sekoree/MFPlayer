using System.Threading.Channels;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Per-subscriber bounded queue used by <see cref="FFmpegVideoChannel"/> to fan frames out
/// to multiple consumers (pull endpoint + N push endpoints) without ring contention.
/// Each subscription owns a private <see cref="Channel{T}"/> with single-writer
/// (the decoder) / single-reader (the consumer) semantics.
/// </summary>
internal sealed class FFmpegVideoSubscription : IVideoSubscription
{
    private readonly FFmpegVideoChannel _parent;
    private readonly VideoSubscriptionOptions _options;
    private readonly ChannelReader<VideoFrame> _reader;
    private readonly ChannelWriter<VideoFrame> _writer;
    private long _queued;
    private long _dequeued;
    private long _droppedOldest;
    private volatile bool _disposed;

    /// <summary>
    /// §heavy-media-fixes phase 4 — frames evicted by overflow handling.
    /// Surfaced through <see cref="IVideoSubscription.DroppedFrames"/>.
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedOldest);

    public FFmpegVideoSubscription(FFmpegVideoChannel parent, VideoSubscriptionOptions options)
    {
        _parent  = parent;
        _options = options;

        var ch = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(Math.Max(1, options.Capacity))
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _reader = ch.Reader;
        _writer = ch.Writer;
    }

    public int  Capacity    => _options.Capacity;
    public int  Count       => (int)Math.Max(0, Interlocked.Read(ref _queued));
    public bool IsCompleted => _disposed && Count == 0;

    public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Called by the decoder when it has a new frame. Returns <see langword="false"/> if
    /// the subscription is disposed so the publisher can release its retained ref.
    /// </summary>
    internal bool TryPublish(VideoFrame frame, CancellationToken token)
    {
        if (_disposed) return false;

        switch (_options.OverflowPolicy)
        {
            case VideoOverflowPolicy.Wait:
            {
                var write = _writer.WriteAsync(frame, token);
                if (!write.IsCompletedSuccessfully)
                {
                    try { write.AsTask().GetAwaiter().GetResult(); }
                    catch (OperationCanceledException)  { return false; }
                    catch (ChannelClosedException)      { return false; }
                }
                Interlocked.Increment(ref _queued);
                return true;
            }

            case VideoOverflowPolicy.DropNewest:
            {
                if (_writer.TryWrite(frame))
                {
                    Interlocked.Increment(ref _queued);
                    return true;
                }
                // Full — caller releases the new frame's ref.
                return false;
            }

            case VideoOverflowPolicy.DropOldestUnderStall:
            {
                // §heavy-media-fixes phase 4 — wait briefly for the consumer
                // to drain (handles per-vsync jitter without dropping) then
                // fall back to plain DropOldest semantics if the queue stays
                // full for too long. The bounded wait is what unblocks demux
                // / decode / EOF detection on heavy media without losing the
                // "lossless under normal conditions" guarantee that the Wait
                // policy provided.
                if (_writer.TryWrite(frame))
                {
                    Interlocked.Increment(ref _queued);
                    return true;
                }

                int stallMs = Math.Max(0, _options.StallTimeoutMs);
                if (stallMs > 0)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                    linked.CancelAfter(stallMs);
                    try
                    {
                        var write = _writer.WriteAsync(frame, linked.Token);
                        if (!write.IsCompletedSuccessfully)
                            write.AsTask().GetAwaiter().GetResult();
                        Interlocked.Increment(ref _queued);
                        return true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        // Stall timer fired — fall through to eviction.
                    }
                    catch (ChannelClosedException)
                    {
                        return false;
                    }
                }

                // Sustained backpressure: evict and retry exactly like
                // DropOldest. Same ref-count invariants (§3.10).
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (_writer.TryWrite(frame))
                    {
                        Interlocked.Increment(ref _queued);
                        return true;
                    }
                    if (_reader.TryRead(out var evicted))
                    {
                        long after = Interlocked.Decrement(ref _queued);
                        System.Diagnostics.Debug.Assert(after >= 0,
                            "DropOldestUnderStall evicted a frame while _queued was already 0 — ref-count torn.");
                        evicted.MemoryOwner?.Dispose();
                        Interlocked.Increment(ref _droppedOldest);
                    }
                    if (_disposed) return false;
                }
                return false;
            }

            case VideoOverflowPolicy.DropOldest:
            default:
            {
                // §3.10 — DropOldest ref-count invariant:
                // `_queued` is incremented exactly once per successful
                // `_writer.TryWrite` and decremented exactly once per
                // `_reader.TryRead`. Each evicted frame's `MemoryOwner` is
                // disposed at most once here (caller owns the retry frame's
                // ref until the final loss path at line 99). A Debug.Assert
                // pins the non-negative invariant for the evict step.
                // Try direct write; on full, evict the oldest and retry.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (_writer.TryWrite(frame))
                    {
                        Interlocked.Increment(ref _queued);
                        return true;
                    }
                    if (_reader.TryRead(out var evicted))
                    {
                        long after = Interlocked.Decrement(ref _queued);
                        System.Diagnostics.Debug.Assert(after >= 0,
                            "DropOldest evicted a frame while _queued was already 0 — ref-count torn.");
                        evicted.MemoryOwner?.Dispose();
                        Interlocked.Increment(ref _droppedOldest);
                    }
                    if (_disposed) return false;
                }
                // Live-lock guard — caller releases.
                return false;
            }
        }
    }

    public int FillBuffer(Span<VideoFrame> dest, int frameCount)
    {
        int filled = 0;
        for (int i = 0; i < frameCount; i++)
        {
            if (!_reader.TryRead(out var vf)) break;
            dest[i] = vf;
            filled++;
        }
        if (filled > 0)
        {
            Interlocked.Add(ref _queued, -filled);
            Interlocked.Add(ref _dequeued, filled);
            _parent.NotifyFrameDelivered(dest[filled - 1].Pts.Ticks);
        }
        if (filled == 0 && Interlocked.Read(ref _dequeued) > 0)
            RaiseUnderrun();
        return filled;
    }

    public bool TryRead(out VideoFrame frame)
    {
        if (_reader.TryRead(out frame))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dequeued);
            _parent.NotifyFrameDelivered(frame.Pts.Ticks);
            return true;
        }
        frame = default;
        if (Interlocked.Read(ref _dequeued) > 0)
            RaiseUnderrun();
        return false;
    }

    /// <summary>Drain and release all queued frames (called on seek / flush).</summary>
    internal void Flush()
    {
        while (_reader.TryRead(out var vf))
        {
            Interlocked.Decrement(ref _queued);
            vf.MemoryOwner?.Dispose();
        }
    }

    internal void CompleteWriter() => _writer.TryComplete();

    private void RaiseUnderrun()
    {
        var handler = BufferUnderrun;
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(static s =>
        {
            var (h, sender) = ((EventHandler<BufferUnderrunEventArgs>, object))s!;
            h(sender, new BufferUnderrunEventArgs(TimeSpan.Zero, 0));
        }, (handler, (object)this));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.TryComplete();
        Flush();
        _parent.RemoveSubscription(this);
    }
}

