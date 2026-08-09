using System.Diagnostics;

namespace S.Media.Core.Video;

/// <summary>
/// The small bounded hand-off between a producer running on a media/wall cadence and a display output
/// presenting on its panel's vblank. It supports FIFO reads for compatibility and newest-frame reads
/// for minimum-latency presenters; capacity pressure always evicts the oldest frame.
/// </summary>
/// <remarks>
/// <para>
/// A producer's rate and a panel's refresh are never exactly equal - 60.000 authored against a 59.94 Hz
/// panel, say - so over any long run one side must lose or repeat a frame. That part is unavoidable.
/// The low-latency presentation path takes the newest queued frame at each device opportunity. This
/// prevents a short stall or cadence crossing from becoming a FIFO backlog; any superseded older frame
/// is reported as a cadence drop at this output only.
/// </para>
/// <para>
/// Drop-oldest rather than drop-newest: a full queue means the head is the stalest thing in it and the
/// newest arrival is the one worth keeping. The newest-frame dequeue makes the same choice explicitly.
/// </para>
/// <para>
/// Thread-safe for one producer and one consumer (the usual submit-thread / render-thread pair); the
/// caller disposes every frame handed back, so no frame is ever disposed under the lock.
/// </para>
/// </remarks>
public sealed class PresentFrameQueue
{
    /// <summary>
    /// Two slots provide a small hand-off margin across producer/presenter scheduling without allowing
    /// a deep display backlog. The low-latency consumer still takes the newest frame.
    /// </summary>
    public const int DefaultCapacity = 2;

    private readonly Queue<(VideoFrame Frame, long EnqueuedTimestamp)> _queue;
    private readonly object _gate = new();
    private long _droppedOldest;

    public PresentFrameQueue(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _queue = new Queue<(VideoFrame, long)>(capacity);
    }

    /// <summary>Maximum frames held before an enqueue evicts the head.</summary>
    public int Capacity { get; }

    /// <summary>Frames currently waiting for a vblank.</summary>
    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>
    /// Frames evicted because the queue was full or superseded by a newer frame at a presentation
    /// opportunity. A slow, steady count is normal cadence conversion; a burst indicates a stall.
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
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_queue.Count >= Capacity)
                evicted = _queue.Dequeue().Frame;
            _queue.Enqueue((frame, now));
        }

        if (evicted is null)
            return false;

        Interlocked.Increment(ref _droppedOldest);
        return true;
    }

    /// <summary>Takes the oldest queued frame, or null when nothing is due. The caller disposes it.</summary>
    public VideoFrame? TryDequeue() => TryDequeue(out _);

    /// <summary>
    /// Takes the oldest queued frame along with the <see cref="Stopwatch.GetTimestamp"/> reading from when
    /// it was queued, so the presenter can measure how long the frame actually took to reach the device.
    /// </summary>
    /// <param name="enqueuedTimestamp">When the returned frame was queued; 0 when none was due.</param>
    public VideoFrame? TryDequeue(out long enqueuedTimestamp)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                enqueuedTimestamp = 0;
                return null;
            }

            var (frame, queuedAt) = _queue.Dequeue();
            enqueuedTimestamp = queuedAt;
            return frame;
        }
    }

    /// <summary>
    /// Takes the newest queued frame and returns every older queued frame for disposal. A presenter can
    /// display only one frame at a device opportunity; showing an older FIFO head first would turn a normal
    /// cadence crossing or short stall into avoidable queue latency.
    /// </summary>
    /// <param name="enqueuedTimestamp">Enqueue timestamp of the returned newest frame.</param>
    /// <param name="superseded">Older frames that can no longer be useful, oldest first.</param>
    public VideoFrame? TryDequeueLatest(out long enqueuedTimestamp, out VideoFrame[] superseded)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                enqueuedTimestamp = 0;
                superseded = [];
                return null;
            }

            var staleCount = _queue.Count - 1;
            superseded = staleCount == 0 ? [] : new VideoFrame[staleCount];
            for (var i = 0; i < staleCount; i++)
                superseded[i] = _queue.Dequeue().Frame;

            var (frame, queuedAt) = _queue.Dequeue();
            enqueuedTimestamp = queuedAt;
            if (staleCount > 0)
                Interlocked.Add(ref _droppedOldest, staleCount);
            return frame;
        }
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
            var drained = new VideoFrame[_queue.Count];
            var i = 0;
            foreach (var (frame, _) in _queue)
                drained[i++] = frame;
            _queue.Clear();
            return drained;
        }
    }
}
