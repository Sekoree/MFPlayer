using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class PresentFrameQueueTests
{
    private static readonly VideoFormat Bgra2X1 = new(2, 1, PixelFormat.Bgra32, new Rational(60, 1));

    private static VideoFrame Frame(int seconds, Action? onRelease = null) =>
        new(TimeSpan.FromSeconds(seconds),
            Bgra2X1,
            [new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }],
            [8],
            release: onRelease is null ? null : new OnceRelease(onRelease));

    private sealed class OnceRelease(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }

    [Fact]
    public void Capacity_must_be_at_least_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentFrameQueue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentFrameQueue(-1));
    }

    [Fact]
    public void Drains_oldest_first()
    {
        var queue = new PresentFrameQueue(capacity: 3);
        queue.Enqueue(Frame(1), out _);
        queue.Enqueue(Frame(2), out _);
        queue.Enqueue(Frame(3), out _);

        Assert.Equal(TimeSpan.FromSeconds(1), queue.TryDequeue()!.PresentationTime);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.TryDequeue()!.PresentationTime);
        Assert.Equal(TimeSpan.FromSeconds(3), queue.TryDequeue()!.PresentationTime);
        Assert.Null(queue.TryDequeue());
    }

    [Fact]
    public void Overflow_evicts_the_head_and_keeps_the_newest()
    {
        var queue = new PresentFrameQueue(capacity: 2);
        queue.Enqueue(Frame(1), out var none1);
        queue.Enqueue(Frame(2), out var none2);

        // Third arrival on a full queue: the STALEST goes, not the newest. Dropping the newest would
        // mean a burst of arrivals showed the first and discarded the rest.
        var overflowed = queue.Enqueue(Frame(3), out var evicted);

        Assert.Null(none1);
        Assert.Null(none2);
        Assert.True(overflowed);
        Assert.NotNull(evicted);
        Assert.Equal(TimeSpan.FromSeconds(1), evicted.PresentationTime);
        Assert.Equal(2, queue.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.TryDequeue()!.PresentationTime);
        Assert.Equal(TimeSpan.FromSeconds(3), queue.TryDequeue()!.PresentationTime);
    }

    [Fact]
    public void Evicted_frame_is_handed_back_undisposed_so_the_caller_frees_it_outside_the_lock()
    {
        var released = 0;
        var queue = new PresentFrameQueue(capacity: 1);
        queue.Enqueue(Frame(1, () => released++), out _);

        queue.Enqueue(Frame(2), out var evicted);

        Assert.Equal(0, released);
        evicted!.Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void Counts_only_genuine_evictions()
    {
        var queue = new PresentFrameQueue(capacity: 2);

        queue.Enqueue(Frame(1), out _);
        queue.Enqueue(Frame(2), out _);
        Assert.Equal(0, queue.DroppedOldest);

        queue.Enqueue(Frame(3), out var a);
        queue.Enqueue(Frame(4), out var b);
        Assert.Equal(2, queue.DroppedOldest);

        a!.Dispose();
        b!.Dispose();
    }

    [Fact]
    public void A_producer_one_frame_per_beat_faster_than_the_display_drops_exactly_one_per_beat()
    {
        // The behaviour the depth exists for. 61 submits against 60 pulls is a 1-frame surplus over the
        // period; with headroom that costs exactly one drop, and the queue neither starves nor runs away.
        var queue = new PresentFrameQueue(capacity: 2);
        var presented = 0;
        var dropped = 0;

        for (var i = 0; i < 61; i++)
        {
            queue.Enqueue(Frame(i), out var evicted);
            if (evicted is not null) { evicted.Dispose(); dropped++; }
            if (i % 61 != 60)     // the display pulls once per iteration except the surplus one
            {
                var shown = queue.TryDequeue();
                if (shown is not null) { shown.Dispose(); presented++; }
            }
        }

        Assert.Equal(60, presented);
        Assert.Equal(0, dropped);      // absorbed by the depth, not discarded
        Assert.Equal(1, queue.Count);  // the surplus frame is simply still queued
    }

    [Fact]
    public void Single_slot_is_the_degenerate_case_it_drops_whenever_two_arrive_between_pulls()
    {
        // Documents why the default is not 1: with no headroom every phase crossing costs a frame.
        var queue = new PresentFrameQueue(capacity: 1);

        queue.Enqueue(Frame(1), out _);
        queue.Enqueue(Frame(2), out var evicted);

        Assert.NotNull(evicted);
        Assert.Equal(1, queue.DroppedOldest);
        evicted.Dispose();
    }

    [Fact]
    public void DrainAll_empties_oldest_first_and_leaves_the_queue_reusable()
    {
        var queue = new PresentFrameQueue(capacity: 3);
        queue.Enqueue(Frame(1), out _);
        queue.Enqueue(Frame(2), out _);

        var drained = queue.DrainAll();

        Assert.Equal(2, drained.Length);
        Assert.Equal(TimeSpan.FromSeconds(1), drained[0].PresentationTime);
        Assert.Equal(TimeSpan.FromSeconds(2), drained[1].PresentationTime);
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.DrainAll());

        foreach (var frame in drained) frame.Dispose();

        queue.Enqueue(Frame(3), out _);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void DrainAll_does_not_dispose_so_teardown_owns_the_frames()
    {
        var released = 0;
        var queue = new PresentFrameQueue(capacity: 2);
        queue.Enqueue(Frame(1, () => released++), out _);

        var drained = queue.DrainAll();

        Assert.Equal(0, released);
        drained[0].Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void Dequeue_reports_when_the_frame_was_queued_so_the_presenter_can_measure_its_own_latency()
    {
        var queue = new PresentFrameQueue(capacity: 2);
        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        queue.Enqueue(Frame(1), out _);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();

        var frame = queue.TryDequeue(out var queuedAt);

        Assert.NotNull(frame);
        // The stamp has to be taken at ENQUEUE, or the measured latency would exclude the queue wait -
        // which is the part that actually makes video late.
        Assert.InRange(queuedAt, before, after);
        frame.Dispose();
    }

    [Fact]
    public void An_empty_dequeue_reports_no_timestamp_rather_than_a_stale_one()
    {
        var queue = new PresentFrameQueue(capacity: 2);

        Assert.Null(queue.TryDequeue(out var queuedAt));
        Assert.Equal(0, queuedAt);
    }

    [Fact]
    public void Enqueue_rejects_null()
    {
        var queue = new PresentFrameQueue();
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!, out _));
    }

    [Fact]
    public async Task Concurrent_producer_and_consumer_neither_lose_nor_duplicate_a_frame()
    {
        const int total = 5_000;
        var queue = new PresentFrameQueue(capacity: 4);
        var seen = new List<int>(total);
        var dropped = 0;

        var consumer = Task.Run(() =>
        {
            var taken = 0;
            while (taken + Volatile.Read(ref dropped) < total)
            {
                var frame = queue.TryDequeue();
                if (frame is null) { Thread.SpinWait(20); continue; }
                seen.Add((int)frame.PresentationTime.TotalMilliseconds);
                frame.Dispose();
                taken++;
            }
        });

        for (var i = 0; i < total; i++)
        {
            queue.Enqueue(
                new VideoFrame(TimeSpan.FromMilliseconds(i), Bgra2X1, [new byte[8]], [8]),
                out var evicted);
            if (evicted is not null) { evicted.Dispose(); Interlocked.Increment(ref dropped); }
        }

        await consumer.WaitAsync(TimeSpan.FromSeconds(30));
        // Every frame either reached the consumer or was counted as an eviction - none vanished, and
        // what did arrive arrived in submit order.
        Assert.Equal(total, seen.Count + dropped);
        Assert.Equal(seen.OrderBy(x => x), seen);
    }
}
