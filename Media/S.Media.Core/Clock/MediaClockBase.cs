using System.Timers;
using Timer = System.Timers.Timer;

namespace S.Media.Core.Clock;

/// <summary>
/// Abstract base for <see cref="IMediaClock"/> implementations.
/// Manages the <see cref="Tick"/> event and drives it from an internal
/// <see cref="Timer"/> at a configurable interval so concrete subclasses only
/// need to supply the current position value.
/// </summary>
public abstract class MediaClockBase : IMediaClock, IDisposable
{
    private readonly object  _tickLock = new();
    private event Action<TimeSpan>? _tick;
    private readonly Timer   _tickTimer;
    private bool             _disposed;

    protected MediaClockBase(TimeSpan tickInterval)
    {
        _tickTimer          = new Timer(tickInterval.TotalMilliseconds);
        _tickTimer.Elapsed += OnTimerElapsed;
        _tickTimer.AutoReset = true;
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
        _tickTimer.Start();
    }

    public virtual void Stop()
    {
        _tickTimer.Stop();
    }

    public abstract void Reset();

    // ── Internal helpers ───────────────────────────────────────────────────

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
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
        bool wasRunning = _tickTimer.Enabled;
        _tickTimer.Stop();
        _tickTimer.Interval = interval.TotalMilliseconds;
        if (wasRunning) _tickTimer.Start();
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

