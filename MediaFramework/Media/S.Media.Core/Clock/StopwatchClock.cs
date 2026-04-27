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
/// <para>
/// Implements <see cref="ISuppressesAutoAvDriftCorrection"/>: a stopwatch is wall
/// time, not a decoder-PTS reference, so
/// <see cref="S.Media.Core.Routing.IAVRouter.GetAvDrift"/> against it conflates
/// pipeline-depth offsets with real A/V drift. The playback layer therefore uses
/// <see cref="S.Media.Core.Routing.IAVRouter.GetAvStreamHeadDrift"/> for auto
/// correction, which baselines the first sample and tracks only relative change.
/// </para>
/// </summary>
public sealed class StopwatchClock : MediaClockBase, ISuppressesAutoAvDriftCorrection
{
    // Doc/Clock-And-AV-Drift-Analysis.md §6.1 / item J — lockless Position.
    // The previous implementation acquired a Lock on every Position read, which on
    // hot router paths (push tick @ 250 Hz × N routes) burned an interlocked
    // operation per call.  This version uses a seqlock-style version field
    // matching the NDIClock pattern: writers (Start/Stop/Reset) bump it odd-then-
    // even around their multi-field updates, readers retry on torn snapshots.
    // The remaining writer-side _swLock guards the small block where _sw and
    // _offset are mutated together — Start/Stop/Reset are uncontended in steady
    // state, so the lock acquisition there has no measurable cost.
    private readonly Stopwatch _sw = new();
    private readonly Lock      _swLock = new();
    private long               _offsetTicks;        // accumulated TimeSpan ticks from previous Start/Stop cycles
    private int                _snapshotVersion;    // seqlock; even = stable, odd = writer in progress
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
            // Seqlock read: retry while a writer is in progress (odd version) or the
            // version changed between sampling _offsetTicks and _sw.Elapsed. The retry
            // count is bounded in practice — writers (Start/Stop/Reset) are rare and
            // each write-pair is ~tens of nanoseconds.
            while (true)
            {
                int v1 = Volatile.Read(ref _snapshotVersion);
                if ((v1 & 1) != 0) continue;

                long offset = Interlocked.Read(ref _offsetTicks);
                long elapsedTicks = _sw.Elapsed.Ticks;

                int v2 = Volatile.Read(ref _snapshotVersion);
                if (v1 != v2) continue;

                return TimeSpan.FromTicks(offset + elapsedTicks);
            }
        }
    }

    public override void Start()
    {
        lock (_swLock)
        {
            Interlocked.Increment(ref _snapshotVersion); // odd
            _sw.Start();
            Interlocked.Increment(ref _snapshotVersion); // even
        }
        _running = true;
        AcquireHighResTimer();
        base.Start();
    }

    public override void Stop()
    {
        _running = false;
        lock (_swLock)
        {
            Interlocked.Increment(ref _snapshotVersion); // odd
            _sw.Stop();
            // Fold the just-elapsed delta into the persistent offset BEFORE resetting
            // _sw, so a reader catching the seqlock between the two operations cannot
            // observe `offset + 0`. The seqlock retry handles the read-side race.
            Interlocked.Add(ref _offsetTicks, _sw.Elapsed.Ticks);
            _sw.Reset();
            Interlocked.Increment(ref _snapshotVersion); // even
        }
        ReleaseHighResTimer();
        base.Stop();
    }

    public override void Reset()
    {
        lock (_swLock)
        {
            Interlocked.Increment(ref _snapshotVersion); // odd
            _sw.Reset();
            Interlocked.Exchange(ref _offsetTicks, 0);
            Interlocked.Increment(ref _snapshotVersion); // even
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

