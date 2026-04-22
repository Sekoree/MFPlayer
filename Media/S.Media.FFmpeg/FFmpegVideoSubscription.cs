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
    private volatile bool _disposed;

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

            case VideoOverflowPolicy.DropOldest:
            default:
            {
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
                        Interlocked.Decrement(ref _queued);
                        evicted.MemoryOwner?.Dispose();
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

