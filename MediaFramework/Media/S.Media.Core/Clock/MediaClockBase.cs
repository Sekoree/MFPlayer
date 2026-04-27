namespace S.Media.Core.Clock;

/// <summary>
/// Abstract base for <see cref="IMediaClock"/> implementations.
/// Manages the <see cref="Tick"/> event and drives it from an internal
/// <see cref="Timer"/> at a configurable interval so concrete subclasses only
/// need to supply the current position value.
/// </summary>
/// <remarks>
/// Uses <see cref="System.Threading.Timer"/> instead of <c>System.Timers.Timer</c>
/// to avoid allocating an <c>ElapsedEventArgs</c> on every tick (~50–62 Hz).
/// </remarks>
public abstract class MediaClockBase : IMediaClock, IDisposable
{
    private readonly Lock    _tickLock = new();
    private event Action<TimeSpan>? _tick;
    private readonly Timer   _tickTimer;
    private TimeSpan         _tickInterval;
    private bool             _disposed;
    // Doc/Clock-And-AV-Drift-Analysis.md §6.7 / item O — lazy timer activation.
    // The timer only runs when the clock has been Start()'d AND there is at least
    // one Tick subscriber. This avoids pinning a thread-pool thread every
    // _tickInterval just to invoke a no-op handler chain. Tracked separately from
    // _tickInterval so SetTickInterval can rearm without re-checking subscribers.
    private bool _timerArmed;
    private bool _started;

    protected MediaClockBase(TimeSpan tickInterval)
    {
        _tickInterval = tickInterval;
        // Infinite dueTime = timer starts stopped; ArmTimer/DisarmTimer toggles it.
        _tickTimer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // ── IMediaClock ────────────────────────────────────────────────────────

    public abstract TimeSpan Position   { get; }
    public          TimeSpan TickCadence => _tickInterval;
    public abstract bool     IsRunning  { get; }

    /// <summary>
    /// §2.8 — raised on the <see cref="System.Threading.Timer"/> callback thread (a
    /// <see cref="ThreadPool"/> thread). Handlers must be non-blocking; heavy work should
    /// be offloaded to <see cref="Task.Run(Action)"/> or a dedicated thread.
    /// The argument is the current <see cref="Position"/> snapshot at tick time.
    /// </summary>
    public event Action<TimeSpan>? Tick
    {
        add
        {
            lock (_tickLock)
            {
                _tick += value;
                ReconcileTimerLocked();
            }
        }
        remove
        {
            lock (_tickLock)
            {
                _tick -= value;
                ReconcileTimerLocked();
            }
        }
    }

    public virtual void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_tickLock)
        {
            _started = true;
            ReconcileTimerLocked();
        }
    }

    public virtual void Stop()
    {
        lock (_tickLock)
        {
            _started = false;
            ReconcileTimerLocked();
        }
    }

    public abstract void Reset();

    // ── Internal helpers ───────────────────────────────────────────────────

    private void OnTimerTick(object? state)
    {
        // §3.30 / C1: timer callbacks may fire after Dispose() because
        // System.Threading.Timer.Dispose() does not wait for in-flight callbacks.
        // Observing _disposed here keeps subscribers from seeing a post-dispose
        // position read (which some concrete Position implementations would
        // compute off a stopped stopwatch and return stale values for).
        if (_disposed || !IsRunning) return;
        Action<TimeSpan>? handler;
        lock (_tickLock) handler = _tick;
        handler?.Invoke(Position);
    }

    /// <summary>
    /// Activates / deactivates the underlying <see cref="Timer"/> based on whether
    /// <see cref="Start"/> has been called and at least one <see cref="Tick"/>
    /// subscriber is registered. Callers MUST hold <see cref="_tickLock"/>.
    /// </summary>
    private void ReconcileTimerLocked()
    {
        bool wantArmed = _started && _tick is not null && !_disposed;
        if (wantArmed == _timerArmed) return;
        if (wantArmed)
            _tickTimer.Change(_tickInterval, _tickInterval);
        else
            _tickTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timerArmed = wantArmed;
    }

    /// <summary>
    /// Allows subclasses to adjust the tick interval at runtime
    /// (e.g. after the hardware buffer size is known).
    /// </summary>
    protected void SetTickInterval(TimeSpan interval)
    {
        lock (_tickLock)
        {
            _tickInterval = interval;
            if (_timerArmed)
                _tickTimer.Change(interval, interval);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            lock (_tickLock)
            {
                _timerArmed = false;
                _started = false;
            }
            _tickTimer.Dispose();
        }
    }
}

