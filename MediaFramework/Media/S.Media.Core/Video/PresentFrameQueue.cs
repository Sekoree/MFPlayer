namespace S.Media.Core.Video;

/// <summary>
/// The small bounded hand-off between a producer running on a media/wall cadence and a display output
/// presenting on its panel's vblank. Oldest-first, drop-oldest when full.
/// </summary>
/// <remarks>
/// <para>
/// A producer's rate and a panel's refresh are never exactly equal - 60.000 authored against a 59.94 Hz
/// panel, say - so over any long run one side must lose or repeat a frame. That part is unavoidable.
/// What a queue changes is <em>when</em>: with a single slot the arrival phase decides whether a frame
/// survives, and since that phase drifts through the beat period the losses arrive in clusters. With a
/// couple of slots of headroom the phase is absorbed and only the genuine rate difference is left, which
/// surfaces as one evenly-spaced drop (producer faster) or repeat (producer slower) per beat period.
/// </para>
/// <para>
/// Drop-oldest rather than drop-newest: the queue is drained oldest-first, so a full queue means the head
/// is the staleest thing in it and the newest arrival is the one worth keeping.
/// </para>
/// <para>
/// Thread-safe for one producer and one consumer (the usual submit-thread / render-thread pair); the
/// caller disposes every frame handed back, so no frame is ever disposed under the lock.
/// </para>
/// </remarks>
public sealed class PresentFrameQueue
{
    /// <summary>
    /// Two slots absorb the producer/panel phase drift while adding at most one refresh period of
    /// latency (~17 ms at 60 Hz). One slot is the degenerate case: no headroom, so drops cluster
    /// wherever the two cadences cross.
    /// </summary>
    public const int DefaultCapacity = 2;

    private readonly Queue<VideoFrame> _queue;
    private readonly object _gate = new();
    private long _droppedOldest;

    public PresentFrameQueue(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _queue = new Queue<VideoFrame>(capacity);
    }

    /// <summary>Maximum frames held before an enqueue evicts the head.</summary>
    public int Capacity { get; }

    /// <summary>Frames currently waiting for a vblank.</summary>
    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>
    /// Frames evicted because the queue was full - i.e. the producer is outrunning the display. Expect a
    /// slow, steady count; a fast or bursty one means something upstream is overproducing.
    /// </summary>
    public long DroppedOldest => Interlocked.Read(ref _droppedOldest);

    /// <summary>
    /// Queues <paramref name="frame"/>, evicting the head if the queue was already full.
    /// </summary>
    /// <param name="frame">The frame to queue. Ownership passes to the queue.</param>
    /// <param name="evicted">The displaced head, which the CALLER must dispose, or null.</param>
    /// <returns><c>true</c> when a frame was evicted to make room.</returns>
    public bool Enqueue(VideoFrame frame, out VideoFrame? evicted)
    {
        ArgumentNullException.ThrowIfNull(frame);
        evicted = null;
        lock (_gate)
        {
            if (_queue.Count >= Capacity)
                evicted = _queue.Dequeue();
            _queue.Enqueue(frame);
        }

        if (evicted is null)
            return false;

        Interlocked.Increment(ref _droppedOldest);
        return true;
    }

    /// <summary>Takes the oldest queued frame, or null when nothing is due. The caller disposes it.</summary>
    public VideoFrame? TryDequeue()
    {
        lock (_gate)
            return _queue.Count > 0 ? _queue.Dequeue() : null;
    }

    /// <summary>
    /// Empties the queue and returns its contents oldest-first for the caller to dispose. Used on
    /// reconfigure, flush and teardown, where every held frame is stale by definition.
    /// </summary>
    public VideoFrame[] DrainAll()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
                return [];
            var drained = _queue.ToArray();
            _queue.Clear();
            return drained;
        }
    }
}
