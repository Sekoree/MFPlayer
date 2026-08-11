using System.Collections.Concurrent;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Threading;

/// <summary>
/// Serial command loop for the public session API (D5 / OQ8). Callers <see cref="Post"/>
/// (fire-and-forget) or <see cref="InvokeAsync(Action)"/> (awaitable) onto one owner context; queries
/// elsewhere read immutable snapshots. There is deliberately <strong>no</strong> blocking
/// <c>Invoke</c> - that is the deadlock OQ8 warns about - so a UI/plugin callback can only re-enter via
/// <c>Post</c>/<c>InvokeAsync</c>, never by blocking the loop on itself. Lives in Core so every tier
/// shares one dispatcher contract; <c>S.Media.Session</c>'s <c>ShowSession</c> owns one (Phase 4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The loop owns a dedicated thread, not a thread-pool one.</strong> It used to be
/// <c>Task.Run(RunLoopAsync)</c> with <c>await work()</c> inside the consuming <c>foreach</c>, which meant
/// that after every awaited command the loop's own continuation - the code that dequeues and STARTS the
/// next command - had to wait for a pool thread. Every show-critical commit (cue fire, start edge, level
/// write, completion poll) therefore queued behind whatever else the app had given the pool, and under the
/// starvation a GO produces the pool's hill-climbing injection delay (~500 ms per thread) landed directly
/// on the cue start path. A dedicated thread cannot be starved by unrelated work.
/// </para>
/// <para>
/// The thread also installs a single-threaded <see cref="SynchronizationContext"/>, so an <c>await</c>
/// inside a command that does NOT say <c>ConfigureAwait(false)</c> resumes on the dispatcher thread
/// instead of the pool. Commands that do say it still resume on the pool - unchanged, and still fully
/// serialized, because the loop does not take the next command until the current task completes.
/// </para>
/// <para>
/// <see cref="IsOnDispatcherThread"/> stays <see cref="AsyncLocal{T}"/>-based rather than comparing thread
/// identity. It is a RE-ENTRANCY guard: a command that has awaited with <c>ConfigureAwait(false)</c> is
/// running on a pool thread while the loop sits blocked on that very command, so a nested
/// <c>InvokeAsync</c> from there must still run inline. Answering by thread identity would make it post
/// instead - onto a loop that cannot reach it - and deadlock.
/// </para>
/// </remarks>
public sealed class SessionDispatcher : IDisposable, IAsyncDisposable
{
    public const int DefaultCapacity = 4096;

    private static readonly AsyncLocal<SessionDispatcher?> Current = new();

    private readonly BlockingCollection<Func<Task>> _queue;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _continuations = new();
    private readonly ManualResetEventSlim _pumpWake = new(false);
    private readonly object _metricsGate = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _pumpExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeState;
    private int _queueDisposed;
    private int _pumpStopped;
    private int _queuedWorkCount;
    private int _highWaterMark;
    private long _rejectedWorkCount;

    public SessionDispatcher(string? name = null, int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be >= 1");

        Name = string.IsNullOrWhiteSpace(name) ? nameof(SessionDispatcher) : name;
        Capacity = capacity;
        _queue = new BlockingCollection<Func<Task>>(new ConcurrentQueue<Func<Task>>(), capacity);
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = Name,
            // Honoured on Windows; a no-op for a normal-priority process on Linux, where real priority
            // needs SCHED_FIFO + RLIMIT_RTPRIO. Harmless either way, and correct where it applies.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public string Name { get; }

    public int Capacity { get; }

    /// <summary>Work waiting in the bounded queue; excludes the command currently executing.</summary>
    public int QueuedWorkCount => Math.Max(0, Volatile.Read(ref _queuedWorkCount));

    public int HighWaterMark => Volatile.Read(ref _highWaterMark);

    public long RejectedWorkCount => Interlocked.Read(ref _rejectedWorkCount);

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>Atomic-enough point-in-time queue health for host telemetry. Individual fields may advance
    /// while this value is assembled; all counters are monotonic except current depth.</summary>
    public SessionDispatcherDiagnostics Diagnostics => new(
        Name,
        Capacity,
        QueuedWorkCount,
        HighWaterMark,
        RejectedWorkCount,
        IsDisposed);

    /// <summary>True when the calling code is running on this dispatcher's logical owner context.</summary>
    public bool IsOnDispatcherThread => ReferenceEquals(Current.Value, this);

    /// <summary>
    /// Queues <paramref name="action"/> to run on the loop. Returns <c>true</c> if enqueued; <c>false</c>
    /// if the bounded queue is full or the dispatcher is disposed/disposing, so the work won't run. Callers
    /// that await completion must handle that (see <see cref="InvokeAsync(Action)"/>, which faults instead of hanging).
    /// </summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryPostWork(() =>
        {
            action();
            return Task.CompletedTask;
        }) == EnqueueResult.Accepted;
    }

    /// <summary>Queues <paramref name="action"/> and completes the returned task when it has run (or faulted).</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task with its result (or fault).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeAsync(() => Task.FromResult(func()));
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task when the async work has run (or faulted).</summary>
    public Task InvokeAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return InvokeAsync(async () =>
        {
            await func().ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Queues <paramref name="func"/> and completes the returned task with its async result (or fault).</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsOnDispatcherThread)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueue = TryPostWork(async () =>
            {
                try
                {
                    tcs.TrySetResult(await func().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        if (enqueue != EnqueueResult.Accepted)
        {
            // Enqueue failed: fault so the awaiter never hangs and distinguish shutdown from overload.
            tcs.TrySetException(enqueue == EnqueueResult.Full
                ? new SessionDispatcherOverloadedException(Name, Capacity)
                : new ObjectDisposedException(nameof(SessionDispatcher)));
        }

        return tcs.Task;
    }

    private EnqueueResult TryPostWork(Func<Task> work)
    {
        if (IsDisposed)
            return EnqueueResult.Disposed;
        try
        {
            lock (_metricsGate)
            {
                if (!_queue.TryAdd(work))
                {
                    if (IsDisposed || _queue.IsAddingCompleted)
                        return EnqueueResult.Disposed;
                    Interlocked.Increment(ref _rejectedWorkCount);
                    return EnqueueResult.Full;
                }

                _queuedWorkCount++;
                if (_queuedWorkCount > _highWaterMark)
                    _highWaterMark = _queuedWorkCount;
            }

            // The pump's idle wait is the SAME event the in-command wait uses; every producer -
            // work, continuation, shutdown - sets it after publishing.
            _pumpWake.Set();
            return EnqueueResult.Accepted;
        }
        catch (ObjectDisposedException)
        {
            return EnqueueResult.Disposed;
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with Post during shutdown.
            return EnqueueResult.Disposed;
        }
    }

    private void RunLoop()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(this));
        try
        {
            // NOT GetConsumingEnumerable: that blocks inside the collection where only new COMMANDS can
            // wake it, so a continuation posted to this context while the pump was idle (the tail of a
            // fire-and-forget command that awaited without ConfigureAwait(false)) would sit unexecuted -
            // a fade completion stalling until unrelated work happened to arrive. The idle wait below
            // watches the one event every producer sets.
            while (true)
            {
                DrainContinuations();

                if (_queue.TryTake(out var work))
                {
                    lock (_metricsGate)
                        _queuedWorkCount--;
                    var previous = Current.Value;
                    Current.Value = this;
                    try
                    {
                        RunToCompletion(work);
                    }
                    finally
                    {
                        Current.Value = previous;
                    }

                    // Anything the command left posted to this context after it completed (a
                    // fire-and-forget continuation) runs here rather than waiting for the next command.
                    DrainContinuations();
                    continue;
                }

                if (_queue.IsCompleted)
                    break;

                // Reset AFTER the wait, never before the checks above: every producer sets the event
                // after publishing, so anything that arrived in the gap is found by the next pass and
                // no wake-up is lost.
                _pumpWake.Wait();
                _pumpWake.Reset();
            }

            DrainContinuations();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);

            // Nothing drains on this thread again. Hand stragglers - and anything posted from now on -
            // to the pool so their awaiters complete instead of hanging on a dead pump.
            Volatile.Write(ref _pumpStopped, 1);
            DrainContinuationsToPool();

            DisposeQueueOnce();
            _pumpExited.TrySetResult();
        }
    }

    /// <summary>
    /// Runs one command to completion, pumping this context's continuations while it is in flight. Not
    /// taking the next command until the task completes is what keeps the loop SERIAL across awaits - the
    /// property every "dispatcher-confined" comment in the session layer depends on.
    /// </summary>
    private void RunToCompletion(Func<Task> work)
    {
        Task task;
        try
        {
            task = work() ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning("SessionDispatcher '{0}': queued work threw: {1}", Name, ex.Message);
            return;
        }

        if (!task.IsCompleted)
        {
            // A command that awaited with ConfigureAwait(false) finishes on a pool thread and posts
            // nothing back here, so completion itself has to wake the pump or it would block forever on
            // an empty continuation queue.
            task.ContinueWith(
                static (_, state) => ((SessionDispatcher)state!)._pumpWake.Set(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            while (!task.IsCompleted)
            {
                if (_continuations.TryDequeue(out var pending))
                {
                    InvokeContinuation(pending);
                    continue;
                }

                // Reset AFTER the wait, never before the dequeue: an item enqueued in the gap is still
                // found by the TryDequeue at the top of the next iteration, so no wake-up is lost.
                _pumpWake.Wait();
                _pumpWake.Reset();
            }
        }

        if (task.IsFaulted && task.Exception is { } faults)
        {
            MediaDiagnostics.LogWarning(
                "SessionDispatcher '{0}': queued work threw: {1}", Name, faults.GetBaseException().Message);
        }
    }

    private void DrainContinuations()
    {
        while (_continuations.TryDequeue(out var pending))
            InvokeContinuation(pending);
    }

    /// <summary>Shutdown fallback: the dispatcher thread is gone, so posted continuations run on the
    /// pool - the pre-dedicated-thread behavior - rather than being dropped with live awaiters.</summary>
    private void DrainContinuationsToPool()
    {
        while (_continuations.TryDequeue(out var pending))
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Self.InvokeContinuation(state.Pending),
                (Self: this, Pending: pending),
                preferLocal: false);
        }
    }

    private void InvokeContinuation((SendOrPostCallback Callback, object? State) pending)
    {
        try
        {
            pending.Callback(pending.State);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "SessionDispatcher '{0}': posted continuation threw: {1}", Name, ex.Message);
        }
    }

    private void PostContinuation(SendOrPostCallback callback, object? state)
    {
        _continuations.Enqueue((callback, state));
        _pumpWake.Set();

        // Racing the pump's exit: if it has already done its last drain, nothing will ever run this
        // continuation - drain to the pool ourselves. Both sides may drain; each item runs once.
        if (Volatile.Read(ref _pumpStopped) != 0)
            DrainContinuationsToPool();
    }

    /// <summary>
    /// Makes an <c>await</c> inside a command that did not opt out with <c>ConfigureAwait(false)</c>
    /// resume on the dispatcher thread instead of the pool. <c>Send</c> from the dispatcher thread runs
    /// inline; from anywhere else it would have to block on a loop that may be waiting on the caller, so
    /// it is refused rather than allowed to deadlock.
    /// </summary>
    private sealed class DispatcherSynchronizationContext(SessionDispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.PostContinuation(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (ReferenceEquals(Thread.CurrentThread, owner._thread))
            {
                d(state);
                return;
            }

            throw new InvalidOperationException(
                $"Session dispatcher '{owner.Name}' does not support blocking Send from another thread " +
                "(see the OQ8 no-blocking-Invoke rule); use Post or InvokeAsync.");
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    public void Dispose()
    {
        CompleteAddingOnce();
        if (IsOnDispatcherThread)
            return;

        _thread.Join();
    }

    public async ValueTask DisposeAsync()
    {
        CompleteAddingOnce();
        if (IsOnDispatcherThread)
            return;

        await _pumpExited.Task.ConfigureAwait(false);
    }

    private void CompleteAddingOnce()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _queue.CompleteAdding();
        _pumpWake.Set(); // an idle pump is waiting on the event, not inside the collection
    }

    private void DisposeQueueOnce()
    {
        if (Interlocked.Exchange(ref _queueDisposed, 1) == 0)
        {
            _queue.Dispose();
        }
    }

    private enum EnqueueResult
    {
        Accepted,
        Full,
        Disposed,
    }
}

public readonly record struct SessionDispatcherDiagnostics(
    string Name,
    int Capacity,
    int QueuedWorkCount,
    int HighWaterMark,
    long RejectedWorkCount,
    bool IsDisposed);

/// <summary>Raised by awaited dispatcher calls when the configured pending-work capacity is exhausted.</summary>
public sealed class SessionDispatcherOverloadedException : InvalidOperationException
{
    public SessionDispatcherOverloadedException(string dispatcherName, int capacity)
        : base($"Session dispatcher '{dispatcherName}' is full (capacity {capacity}).")
    {
        DispatcherName = dispatcherName;
        Capacity = capacity;
    }

    public string DispatcherName { get; }

    public int Capacity { get; }
}
