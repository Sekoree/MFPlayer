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
    private readonly object  _tickLock = new();
    private event Action<TimeSpan>? _tick;
    private readonly Timer   _tickTimer;
    private TimeSpan         _tickInterval;
    private bool             _disposed;

    protected MediaClockBase(TimeSpan tickInterval)
    {
        _tickInterval = tickInterval;
        // Infinite dueTime = timer starts stopped; Change() activates it.
        _tickTimer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // ── IMediaClock ────────────────────────────────────────────────────────

    public abstract TimeSpan Position   { get; }
    public abstract double   SampleRate { get; }
    public abstract bool     IsRunning  { get; }

    public event Action<TimeSpan>? Tick
    {
        add    { lock (_tickLock) _tick += value; }
        remove { lock (_tickLock) _tick -= value; }
    }

    public virtual void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tickTimer.Change(_tickInterval, _tickInterval);
    }

    public virtual void Stop()
    {
        _tickTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public abstract void Reset();

    // ── Internal helpers ───────────────────────────────────────────────────

    private void OnTimerTick(object? state)
    {
        if (!IsRunning) return;
        Action<TimeSpan>? handler;
        lock (_tickLock) handler = _tick;
        handler?.Invoke(Position);
    }

    /// <summary>
    /// Allows subclasses to adjust the tick interval at runtime
    /// (e.g. after the hardware buffer size is known).
    /// </summary>
    protected void SetTickInterval(TimeSpan interval)
    {
        _tickInterval = interval;
        if (IsRunning)
            _tickTimer.Change(interval, interval);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) _tickTimer.Dispose();
        _disposed = true;
    }
}

