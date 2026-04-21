using System.Diagnostics;

namespace S.Media.Core;

/// <summary>
/// Pure software clock backed by a <see cref="Stopwatch"/>.
/// Use when no hardware time source is available (offline render, unit tests, network sources).
/// </summary>
public sealed class StopwatchClock : MediaClockBase
{
    private readonly Stopwatch _sw = new();
    private readonly Lock      _swLock = new();
    private TimeSpan           _offset; // accumulated time from previous Start/Stop cycles
    private volatile bool      _running;

    /// <param name="tickInterval">How often Tick fires. Defaults to 20 ms.</param>
    public StopwatchClock(TimeSpan? tickInterval = null)
        : base(tickInterval ?? TimeSpan.FromMilliseconds(20))
    {
    }


    // ── IMediaClock ────────────────────────────────────────────────────────

    public override bool     IsRunning  => _running;

    public override TimeSpan Position
    {
        get
        {
            // Lock prevents reading between _offset += _sw.Elapsed and _sw.Reset()
            // inside Stop(), which would transiently double-count the elapsed time.
            lock (_swLock) return _offset + _sw.Elapsed;
        }
    }

    public override void Start()
    {
        lock (_swLock) _sw.Start();
        _running = true;
        base.Start();
    }

    public override void Stop()
    {
        _running = false;
        lock (_swLock)
        {
            _sw.Stop();
            _offset += _sw.Elapsed;
            _sw.Reset();
        }
        base.Stop();
    }

    public override void Reset()
    {
        lock (_swLock)
        {
            _sw.Reset();
            _offset = TimeSpan.Zero;
        }
        _running = false;
    }
}

