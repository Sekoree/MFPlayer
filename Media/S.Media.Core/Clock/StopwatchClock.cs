using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S.Media.Core.Clock;

/// <summary>
/// Pure software clock backed by a <see cref="Stopwatch"/>.
/// Use when no hardware time source is available (offline render, unit tests, network sources).
/// <para>
/// <b>Windows timer granularity (§3.31b / C2):</b> On Windows the default system
/// timer granularity is ~15.6 ms, which coarsens <see cref="Thread.Sleep(int)"/>
/// / <see cref="System.Threading.Timer"/> / <c>WaitOne</c> waits driven off this
/// clock. When <see cref="UseHighResolutionTimer"/> is <see langword="true"/>
/// (default), <see cref="Start"/> calls <c>winmm.timeBeginPeriod(1)</c> and
/// <see cref="Stop"/> / <see cref="Reset"/> emit the matching <c>timeEndPeriod</c>
/// so the 1 ms resolution scope is tight. The call is a no-op on non-Windows
/// platforms. Disable if the host process already manages timer resolution.
/// </para>
/// </summary>
public sealed class StopwatchClock : MediaClockBase
{
    private readonly Stopwatch _sw = new();
    private readonly Lock      _swLock = new();
    private TimeSpan           _offset; // accumulated time from previous Start/Stop cycles
    private volatile bool      _running;
    // §3.31b — track whether we currently hold a timeBeginPeriod reservation so
    // Stop/Reset only release what Start actually acquired (and a double-Stop
    // doesn't underflow the process-wide refcount).
    private bool               _holdsHighResPeriod;

    /// <summary>
    /// When <see langword="true"/> (default) the clock requests 1 ms system-timer
    /// resolution on Windows for the duration of <see cref="IsRunning"/>.
    /// No-op on Linux / macOS.
    /// </summary>
    public bool UseHighResolutionTimer { get; init; } = true;

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
        AcquireHighResTimer();
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
        ReleaseHighResTimer();
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
        ReleaseHighResTimer();
        // §3.31a / C6: stop the base-class tick timer so it doesn't keep firing
        // (and observing _running == false via OnTimerTick) after a Reset that
        // was not preceded by Stop. Cheap idempotent call.
        base.Stop();
    }

    // ── §3.31b / C2 — Windows high-resolution timer scope ────────────────────

    private void AcquireHighResTimer()
    {
        if (!UseHighResolutionTimer || !OperatingSystem.IsWindows()) return;
        if (_holdsHighResPeriod) return;
        try
        {
            if (TimeBeginPeriod(1) == 0) // TIMERR_NOERROR
                _holdsHighResPeriod = true;
        }
        catch (DllNotFoundException) { /* non-Windows / stripped winmm — no-op */ }
    }

    private void ReleaseHighResTimer()
    {
        if (!_holdsHighResPeriod) return;
        _holdsHighResPeriod = false;
        if (!OperatingSystem.IsWindows()) return;
        try { TimeEndPeriod(1); }
        catch (DllNotFoundException) { /* non-Windows / stripped winmm — no-op */ }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriod(uint uPeriod);
}

