using System.Collections.Concurrent;

namespace S.Media.Core;

/// <summary>
/// Producer/consumer queue paired with a <see cref="SemaphoreSlim"/> signal and
/// an <see cref="Interlocked"/>-tracked depth counter.  Used by write-loop-style
/// endpoints (<c>NDIAVEndpoint.VideoWriteLoop</c> / <c>AudioWriteLoop</c>,
/// <c>PortAudioEndpoint</c> in blocking-write mode) to avoid re-implementing the same
/// enqueue-signal / wait-dequeue triplet in every endpoint (§5.4 of
/// Code-Review-Findings).
///
/// <para>
/// <b>Dispose contract:</b> <see cref="Dispose"/> only releases the signal semaphore —
/// it does <b>not</b> drain the queue, so any items still in flight at Dispose time are
/// silently lost. Callers must ensure producer quiescence before Dispose (typically by
/// cancelling the producer CTS, joining the writer thread, and calling
/// <see cref="Drain(Action{T})"/> to return rented buffers to their pool).
/// </para>
///
/// <para>
/// Typical usage from a writer thread:
/// </para>
/// <code>
/// _work.Enqueue(item);  // signals the consumer and bumps <see cref="Count"/>
/// </code>
///
/// <para>
/// Typical usage from a single consumer thread:
/// </para>
/// <code>
/// while (!token.IsCancellationRequested)
/// {
///     if (!_work.WaitForItem(token)) break;   // cancellation
///     while (_work.TryDequeue(out var item)) { /* process */ }
/// }
/// </code>
///
/// <para>
/// The <see cref="Count"/> value is approximate under concurrent producers (it
/// represents the <i>committed</i> depth after enqueue + signal).  Callers that
/// need a hard cap should reserve a slot with <see cref="TryReserveSlot"/>
/// before enqueuing — this matches the atomic reserve-slot pattern described
/// in §3.9 of Code-Review-Findings.
/// </para>
/// </summary>
public sealed class PooledWorkQueue<T> : IDisposable
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private int _count;
    private volatile bool _completed;

    /// <summary>Approximate current queue depth.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// §4.10 / PQ1: returns <see langword="true"/> after <see cref="Complete"/> has
    /// been called. Once set, no further items will be accepted by
    /// <see cref="Enqueue"/>, <see cref="EnqueueReserved"/> or
    /// <see cref="TryEnqueueWithCap"/>, and <see cref="WaitForItem"/> returns
    /// <see langword="false"/> as soon as the queue drains.
    /// </summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// §4.10 / PQ1: signals producers are finished. Consumers draining via
    /// <see cref="WaitForItem"/> + <see cref="TryDequeue"/> will observe
    /// <see cref="WaitForItem"/> returning <see langword="false"/> once the queue
    /// is empty, allowing a clean loop exit without cancellation tokens.
    /// Idempotent; the completion signal also unblocks a single waiting consumer.
    /// </summary>
    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        // Release once so a consumer parked in WaitForItem wakes and observes
        // IsCompleted. Subsequent WaitForItem calls short-circuit on the flag.
        try { _signal.Release(); } catch (ObjectDisposedException) { /* disposed */ }
    }

    /// <summary>
    /// Atomically reserves a slot (incrementing <see cref="Count"/>) iff the
    /// current depth is below <paramref name="cap"/>.  On success the caller
    /// MUST subsequently call <see cref="EnqueueReserved"/> or
    /// <see cref="ReleaseReservation"/> exactly once — a reserved-but-not-filled
    /// slot leaks the capacity until the next drain.
    /// </summary>
    public bool TryReserveSlot(int cap)
    {
        while (true)
        {
            int current = Volatile.Read(ref _count);
            if (current >= cap) return false;
            if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
                return true;
        }
    }

    /// <summary>Cancels a reservation made by <see cref="TryReserveSlot"/>.</summary>
    public void ReleaseReservation() => Interlocked.Decrement(ref _count);

    /// <summary>Enqueues an item into a previously reserved slot and signals the consumer.</summary>
    public void EnqueueReserved(T item)
    {
        _queue.Enqueue(item);
        _signal.Release();
    }

    /// <summary>
    /// Enqueues <paramref name="item"/> and signals the consumer.  Uncapped —
    /// use <see cref="TryReserveSlot"/> first if a depth cap must be honoured
    /// against racing producers.
    /// </summary>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        Interlocked.Increment(ref _count);
        _signal.Release();
    }

    /// <summary>
    /// §4.10 / PQ4: atomic reserve + enqueue with a hard cap. Returns
    /// <see langword="false"/> when the queue is already at <paramref name="cap"/>
    /// depth or has been <see cref="Complete">completed</see>. Unlike the two-step
    /// <see cref="TryReserveSlot"/> + <see cref="EnqueueReserved"/> pattern, this
    /// method cannot leak a reservation if the caller throws between the two
    /// steps — the slot is either filled or never taken.
    /// </summary>
    public bool TryEnqueueWithCap(T item, int cap)
    {
        if (_completed) return false;
        if (!TryReserveSlot(cap)) return false;
        try
        {
            _queue.Enqueue(item);
            _signal.Release();
            return true;
        }
        catch
        {
            // Unreachable in practice (ConcurrentQueue.Enqueue does not throw)
            // but keep the invariant: release the reservation on failure.
            ReleaseReservation();
            throw;
        }
    }

    /// <summary>
    /// Blocks the consumer thread until an item is available, <paramref name="ct"/>
    /// is cancelled, or the queue has been <see cref="Complete">completed</see>
    /// and drained. Returns <see langword="false"/> on cancellation or completed+empty
    /// so the outer loop can break without an exception.
    /// </summary>
    public bool WaitForItem(CancellationToken ct)
    {
        // §4.10 / PQ1: completed + empty → exit immediately so the consumer loop
        // terminates after the producer calls Complete() and the queue drains.
        if (_completed && _queue.IsEmpty) return false;
        try
        {
            _signal.Wait(ct);
            // The signal may have been released by Complete() rather than by a
            // real enqueue; re-check empty + completed after waking.
            if (_completed && _queue.IsEmpty) return false;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Attempts to dequeue a single item; decrements <see cref="Count"/> on success.</summary>
    public bool TryDequeue(out T item)
    {
        if (_queue.TryDequeue(out item!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drains all remaining items into <paramref name="sink"/> without blocking.
    /// Use from the owner's <c>Dispose</c> to return rented buffers to their pool
    /// after the consumer thread has exited.
    /// </summary>
    public void Drain(Action<T> sink)
    {
        while (_queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            sink(item);
        }
    }

    public void Dispose() => _signal.Dispose();
}

