using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Time;

/// <summary>
/// Master playback clock. Free-running by default (backed by
/// <see cref="Stopwatch"/>); call <see cref="SetMaster"/> (or
/// <see cref="MediaClockExtensions.SetMasterChain"/> for <see cref="IMediaClock"/>) to slave it to an external
/// <see cref="IPlaybackClock"/> (typically the audio output) so reported position
/// tracks actual played samples instead of wall time.
/// </summary>
/// <remarks>
/// <para>
/// Tick events (<see cref="AudioTick"/>, <see cref="VideoTick"/>,
/// <see cref="PositionChanged"/>) are driven by an internal wall-clock thread
/// regardless of master attachment - they're "render at this cadence" signals,
/// not "media time advanced by X." When a tick handler runs long or the thread
/// wakes late, the driver <strong>bursts</strong> missed deadlines (capped) and
/// then fast-forwards the schedule so a long stall does not freeze the process.
/// </para>
/// <para>
/// <see cref="PositionChanged"/> is usually raised from the driver thread at
/// ~30 Hz. <see cref="Reset"/> and <see cref="Seek"/> raise it synchronously
/// on the caller's thread immediately after updating the stored position -
/// marshal if your UI requires a single context.
/// </para>
/// <para>
/// When a master clock is attached, <see cref="Pause"/> snapshots how far the
/// master had advanced; <see cref="Start"/> adds any additional master
/// elapsed time that accrued while paused (e.g. PortAudio still draining) so
/// the playhead stays aligned with heard audio.
/// </para>
/// <para>
/// Graph-wide coordinated master pitch (PPM), synchronized drop/repeat across multiple outputs, or other
/// timing policy beyond what individual <see cref="IPlaybackClock"/> instances report is <strong>host-owned</strong>
/// (see <see cref="Audio.AudioRouter"/> remarks, <see cref="Audio.PumpPressurePlaybackHintMonitor"/> for queue-drop hints,
/// and the FFmpeg <c>AdaptiveRateAudioOutput</c> adapter for optional per-output resampling).
/// </para>
/// </remarks>
public sealed class MediaClock : IMediaClock, IDisposable
{
    private static readonly TimeSpan DefaultAudioTickInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultVideoTickInterval = TimeSpan.FromTicks(166_667); // ~60 Hz
    private static readonly TimeSpan PositionChangedInterval  = TimeSpan.FromMilliseconds(33); // ~30 Hz

    private readonly Stopwatch _stopwatch = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _audioTickInterval;
    private readonly TimeSpan _videoTickInterval;
    private readonly ILogger? _log;

    private TimeSpan _basePosition;
    private IPlaybackClock? _master;
    private TimeSpan _masterAnchor;
    /// <summary>
    /// <see cref="ClockReading.EpochId"/> that <see cref="_masterAnchor"/> was taken in. The master REPORTS
    /// its own epoch boundaries (plan D), so <see cref="ComputePositionUnlocked"/> compares ids rather than
    /// inferring a boundary from an observed regression - the old inference folded a master that merely
    /// DIPPED and then double-counted its recovery.
    /// </summary>
    private long _masterEpochId;
    /// <summary>
    /// Highest <see cref="IPlaybackClock.ElapsedSinceStart"/> observed in the CURRENT master epoch.
    /// Invariant: <c>_masterAnchor &lt;= _masterElapsedHighWater</c> - both are set to the same value at
    /// every re-anchor and the high-water is only ever raised by position reads. It is no longer an epoch
    /// DETECTOR, only enforcement of the per-epoch monotonic contract: a regression with an unchanged epoch
    /// id is a contract violation (or a torn read of a clock mid-re-anchor) and is HELD, never folded.
    /// </summary>
    private TimeSpan _masterElapsedHighWater;
    /// <summary>Master reading at last <see cref="Pause"/> - used to fold in audio that played during pause.</summary>
    private ClockReading? _masterReadingWhenPaused;
    /// <summary>
    /// Backing store for <see cref="PositionEpoch"/>; mutated only under <see cref="_gate"/>. Seeded from
    /// <see cref="PlaybackEpoch.Next"/>, never left at the default 0: 0 is <see cref="PlaybackEpoch.Single"/>,
    /// which <see cref="IPlaybackClock"/> RESERVES for "this clock has exactly one epoch and never
    /// re-anchors" and never hands out - precisely so ids from two different clocks can never compare equal
    /// by accident. A MediaClock does re-anchor (<see cref="Seek"/>/<see cref="Reset"/>/<see cref="SetMaster"/>),
    /// and wrappers republish this id as their <see cref="IPlaybackClock.EpochId"/>, so leaving it at 0 made
    /// two never-yet-seeked playheads compare EQUAL to each other and to every genuinely single-epoch clock.
    /// </summary>
    private long _positionEpoch = PlaybackEpoch.Next();

    private bool _isRunning;
    private bool _disposed;

    private Thread? _driverThread;
    private CancellationTokenSource? _driverCts;
    // See WithDriverTransition for the roles of these two.
    private readonly Lock _driverTransitionGate = new();
    private Thread? _activeDriverThread;

    private static readonly ILogger TraceLog = MediaDiagnostics.CreateLogger("S.Media.Core.Clock.MediaClock");

    public MediaClock() : this(DefaultAudioTickInterval, DefaultVideoTickInterval, logger: null) { }

    public MediaClock(ILogger? logger)
        : this(DefaultAudioTickInterval, DefaultVideoTickInterval, logger) { }

    public MediaClock(TimeSpan audioTickInterval, TimeSpan videoTickInterval, ILogger? logger = null)
    {
        if (audioTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(audioTickInterval));
        if (videoTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(videoTickInterval));
        _audioTickInterval = audioTickInterval;
        _videoTickInterval = videoTickInterval;
        _log = logger;
    }

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? AudioTick;
    public event EventHandler? VideoTick;

    public TimeSpan CurrentPosition
    {
        get { lock (_gate) return ComputePositionUnlocked(); }
    }

    /// <summary>The currently attached master, or <c>null</c> when the clock is in stopwatch mode.</summary>
    public IPlaybackClock? Master
    {
        get { lock (_gate) return _master; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _isRunning; }
    }

    /// <inheritdoc cref="IPlayhead.PlaybackRate"/>
    public double PlaybackRate => 1.0;

    /// <summary>
    /// <see cref="IPlayhead.PositionEpoch"/>. It takes a fresh <see cref="PlaybackEpoch.Next"/> id exactly
    /// when the reported position is <em>explicitly re-established</em> - <see cref="Seek"/>,
    /// <see cref="Reset"/>, <see cref="SetMaster"/> - i.e. whenever a cached position-derived decision made
    /// under an earlier epoch may be stale. Consumers capture the epoch they started under and treat a
    /// mismatch as "a re-anchor happened since; my cached view no longer applies" (e.g.
    /// <c>MediaPlayer.Position</c>'s natural-EOF Duration clamp is only valid while the epoch still equals
    /// the one recorded at Play). Folding a MASTER's epoch change does <em>not</em> take a new id: position
    /// is continuous across those, so earlier position-derived state remains valid.
    /// </summary>
    public long PositionEpoch
    {
        get { lock (_gate) return _positionEpoch; }
    }

    /// <summary>
    /// <see cref="IPlayhead.ReadPosition"/>: epoch, position and running state from one pass under the gate,
    /// so a consumer can never pair a position with an epoch a racing <see cref="Seek"/> already invalidated.
    /// </summary>
    public ClockReading ReadPosition()
    {
        lock (_gate) return new ClockReading(_positionEpoch, ComputePositionUnlocked(), _isRunning);
    }

    /// <summary>
    /// Serializes driver start against a Pause/Dispose that has already detached the old driver
    /// under <c>_gate</c> but is still joining it outside <c>_gate</c> - without it a racing Start
    /// sees <c>_driverThread == null</c> and spins up a second driver while the first still ticks.
    /// Lock order: transition gate -> <c>_gate</c> (DriverLoop only ever takes <c>_gate</c>).
    /// Calls arriving ON the driver thread (a tick subscriber) bypass the gate: their join is a
    /// self-join no-op anyway, and blocking there would deadlock against an in-flight joiner.
    /// </summary>
    private void WithDriverTransition(Action body)
    {
        if (ReferenceEquals(Thread.CurrentThread, Volatile.Read(ref _activeDriverThread)))
        {
            body();
            return;
        }
        lock (_driverTransitionGate)
            body();
    }

    public void Start() => WithDriverTransition(StartCore);

    private void StartCore()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_isRunning)
            {
                TraceLog.LogTrace("Start: already running (position={Position})", ComputePositionUnlocked());
                return;
            }
            TraceLog.LogDebug("Start: master={Master} position={Position}",
                _master?.GetType().Name ?? "(stopwatch)", _basePosition);
            _isRunning = true;
            if (_master is not null)
            {
                var reading = _master.Read();
                if (_masterReadingWhenPaused is { } paused)
                {
                    var drift = reading.Elapsed - paused.Elapsed;
                    if (reading.EpochId != paused.EpochId)
                    {
                        // The master re-anchored during the pause (an output Flush rewound the device clock).
                        // Audio that drained between Pause and the flush is unknowable, so fold nothing - the
                        // anchor below starts the new epoch cleanly.
                        TraceLog.LogDebug(
                            "Start: master epoch changed during pause (flush/segment reset) {PausedEpoch}->{Epoch} - not folding",
                            paused.EpochId, reading.EpochId);
                    }
                    else if (drift > TimeSpan.Zero)
                    {
                        _basePosition += drift;
                        TraceLog.LogDebug(
                            "Start: folded master drift while paused pausedAt={PausedAt} now={Now} driftMs={DriftMs} position={Position}",
                            paused.Elapsed, reading.Elapsed, drift.TotalMilliseconds, _basePosition);
                    }
                    else if (drift < TimeSpan.Zero)
                    {
                        // Same epoch and yet regressed: the master broke its monotonic contract. Fold
                        // nothing rather than rewinding the playhead.
                        TraceLog.LogDebug(
                            "Start: master elapsed regressed inside epoch {Epoch} pausedAt={PausedAt} now={Now} driftMs={DriftMs} - not folding",
                            reading.EpochId, paused.Elapsed, reading.Elapsed, drift.TotalMilliseconds);
                    }
                    _masterReadingWhenPaused = null;
                }
                AnchorMasterUnlocked(reading);
                TraceLog.LogDebug("Start: master anchor={Anchor} position={Position}",
                    _masterAnchor, ComputePositionUnlocked());
            }
            else
            {
                _stopwatch.Start();
            }
            StartDriver();
        }
    }

    public void Pause(CancellationToken cancellationToken = default) =>
        WithDriverTransition(() => PauseCore(cancellationToken));

    private void PauseCore(CancellationToken cancellationToken)
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_isRunning) return;
            _basePosition = ComputePositionUnlocked();
            TraceLog.LogDebug("Pause: position={Position} master={Master}",
                _basePosition, _master?.GetType().Name ?? "(stopwatch)");
            if (_master is not null)
                _masterReadingWhenPaused = _master.Read();
            else
                _stopwatch.Reset();
            _isRunning = false;
            (toJoin, toDispose) = DetachDriver();
        }
        // Join under the transition gate (but off _gate, which the driver's ticks need to exit)
        // so a concurrent Start cannot spawn a second driver while the old one is still winding down.
        JoinDriver(toJoin, toDispose, cancellationToken);
    }

    /// <summary>Same as <see cref="Pause"/> for now - semantics may diverge later.</summary>
    public void Stop(CancellationToken cancellationToken = default) => Pause(cancellationToken);

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _basePosition = TimeSpan.Zero;
            _masterReadingWhenPaused = null;
            _positionEpoch = PlaybackEpoch.Next();
            if (_master is not null)
                AnchorMasterUnlocked(_master.Read());
            else if (_isRunning) _stopwatch.Restart();
            else _stopwatch.Reset();
        }
        RaisePositionChanged(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            ThrowIfDisposed();
            TraceLog.LogDebug("Seek: from={From} to={To}", _basePosition, position);
            _basePosition = position;
            _masterReadingWhenPaused = null;
            _positionEpoch = PlaybackEpoch.Next();
            if (_master is not null)
            {
                // Anchor on whatever the master reports right now, epoch included. If a racing output Flush
                // lands AFTER this read (rewinding the master's segment clock to zero), the anchor is stale
                // for at most one position read: ComputePositionUnlocked sees the master's NEW epoch id and
                // folds into it, resuming from `position`.
                AnchorMasterUnlocked(_master.Read());
            }
            else if (_isRunning) _stopwatch.Restart();
            else _stopwatch.Reset();
        }
        RaisePositionChanged(position);
    }

    public void SetMaster(IPlaybackClock? master)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = ComputePositionUnlocked();
            TraceLog.LogDebug("SetMaster: prev={Prev} next={Next} positionAtSwap={Position}",
                _master?.GetType().Name ?? "(stopwatch)", master?.GetType().Name ?? "(stopwatch)", current);
            _master = master;
            _basePosition = current;
            _masterReadingWhenPaused = null;
            _positionEpoch = PlaybackEpoch.Next();
            if (master is not null)
            {
                AnchorMasterUnlocked(master.Read());
                _stopwatch.Reset();
            }
            else if (_isRunning)
            {
                _stopwatch.Restart();
            }
        }
    }

    public void Dispose() => WithDriverTransition(DisposeCore);

    private void DisposeCore()
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopwatch.Stop();
            _isRunning = false;
            (toJoin, toDispose) = DetachDriver();
        }
        JoinDriver(toJoin, toDispose, CancellationToken.None);
    }

    // --- driver thread -----------------------------------------------------

    private void StartDriver()
    {
        if (_driverThread is { IsAlive: true }) return;
        _driverCts = new CancellationTokenSource();
        var token = _driverCts.Token;
        _driverThread = new Thread(() => DriverLoop(token))
        {
            IsBackground = true,
            Name = "MediaClock.Driver",
            Priority = ThreadPriority.AboveNormal,
        };
        _driverThread.Start();
    }

    private (Thread? thread, CancellationTokenSource? cts) DetachDriver()
    {
        var t = _driverThread;
        var cts = _driverCts;
        _driverThread = null;
        _driverCts = null;
        cts?.Cancel();
        return (t, cts);
    }

    private static void JoinDriver(Thread? thread, CancellationTokenSource? cts, CancellationToken cancellationToken)
    {
        try
        {
            CooperativePlaybackJoin.JoinThreadWhileCancelable(thread, cancellationToken);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private void DriverLoop(CancellationToken token)
    {
        Volatile.Write(ref _activeDriverThread, Thread.CurrentThread);
        try
        {
            DriverLoopCore(token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeDriverThread, null, Thread.CurrentThread);
        }
    }

    private void DriverLoopCore(CancellationToken token)
    {
        var sessionStart  = Stopwatch.GetTimestamp();
        var nextAudio     = _audioTickInterval;
        var nextVideo     = _videoTickInterval;
        var nextPosition  = PositionChangedInterval;

        var waitHandle = token.WaitHandle;

        while (!token.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(sessionStart);
            var nextDeadline = Min(nextAudio, Min(nextVideo, nextPosition));
            var sleep = nextDeadline - elapsed;

            if (sleep > TimeSpan.Zero)
            {
                // WaitHandle.WaitOne(TimeSpan) TRUNCATES to whole milliseconds, so a sub-millisecond
                // remainder waits zero and returns instantly - and this loop then re-runs, and waits
                // zero again, until the deadline finally passes. That busy-spin was the STEADY STATE,
                // not an edge case: the three deadlines (10 ms audio, 16.667 ms video, 33 ms position)
                // practically never land on a whole-millisecond boundary together, so nearly every wait
                // gave back its fraction to be spun. ~59 ms of every second, on a thread at
                // AboveNormal priority - and a show runs one clock PER VOICE, so a 13-stem cue paid it
                // thirteen times over (measured ~10.5% of a core per clock thread, ~1.5 cores total).
                // Rounding up costs at most a millisecond of lateness on one tick; the accumulators
                // below still advance by exact intervals, so the cadence itself does not drift.
                var waitMs = (int)Math.Ceiling(sleep.TotalMilliseconds);
                if (waitHandle.WaitOne(waitMs)) break;
            }

            elapsed = Stopwatch.GetElapsedTime(sessionStart);

            if (elapsed >= nextAudio)
            {
                var audioBurst = 0;
                while (elapsed >= nextAudio && audioBurst++ < 64)
                {
                    SafeInvoke(AudioTick);
                    nextAudio += _audioTickInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextAudio <= elapsed)
                    nextAudio += _audioTickInterval;
            }

            if (elapsed >= nextVideo)
            {
                var videoBurst = 0;
                while (elapsed >= nextVideo && videoBurst++ < 64)
                {
                    SafeInvoke(VideoTick);
                    nextVideo += _videoTickInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextVideo <= elapsed)
                    nextVideo += _videoTickInterval;
            }

            if (elapsed >= nextPosition)
            {
                var posBurst = 0;
                while (elapsed >= nextPosition && posBurst++ < 8)
                {
                    RaisePositionChanged(CurrentPosition);
                    nextPosition += PositionChangedInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextPosition <= elapsed)
                    nextPosition += PositionChangedInterval;
            }
        }
    }

    private void SafeInvoke(EventHandler? handler)
    {
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            if (_log is { } l)
                l.LogError(ex, "MediaClock.{Event} subscriber threw", handler.Method.Name);
            else
                MediaDiagnostics.LogError(ex, $"MediaClock subscriber ({handler.Method.Name})");
        }
    }

    private void RaisePositionChanged(TimeSpan position)
    {
        var handler = PositionChanged;
        if (handler is null) return;
        try { handler.Invoke(this, position); }
        catch (Exception ex)
        {
            if (_log is { } l)
                l.LogError(ex, "MediaClock.PositionChanged subscriber threw");
            else
                MediaDiagnostics.LogError(ex, "MediaClock.PositionChanged subscriber");
        }
    }

    /// <summary>
    /// Binds the master anchor to one <see cref="ClockReading"/>; must be called under <see cref="_gate"/>.
    /// Anchor, high-water and epoch id always move together - taking them from a single reading is what
    /// keeps the trio coherent when the master re-anchors concurrently.
    /// </summary>
    private void AnchorMasterUnlocked(ClockReading reading)
    {
        _masterEpochId = reading.EpochId;
        _masterAnchor = reading.Elapsed;
        _masterElapsedHighWater = reading.Elapsed;
    }

    /// <summary>
    /// Computes the running position; must be called under <see cref="_gate"/>. Master-epoch handling
    /// (plan D): the master reports its epoch, so the boundary is an ID COMPARISON, not an inference from a
    /// regression. On a new id the old epoch's accrued time is folded into <see cref="_basePosition"/> and
    /// the anchor moves to the new epoch, so position stays continuous and immediately counts the new
    /// segment (an output Flush at seek/pause/natural-EOF, a device restart, a composite handing off).
    /// Within one id the master is monotonic by contract: a regression is a violation or a torn read, and is
    /// held at the epoch high-water. Holding is what makes a transient DIP that later recovers cost nothing
    /// - the previous "advancing regression ⇒ new epoch" fold re-anchored at the dip floor and then
    /// double-counted the recovery as forward progress.
    /// </summary>
    private TimeSpan ComputePositionUnlocked()
    {
        if (!_isRunning) return _basePosition;
        if (_master is not null)
        {
            var reading = _master.Read();
            if (reading.EpochId != _masterEpochId)
            {
                var folded = _masterElapsedHighWater - _masterAnchor;
                TraceLog.LogDebug(
                    "Position: master epoch {From}->{To} (elapsed={Elapsed}) - folding {FoldedMs}ms and re-anchoring",
                    _masterEpochId, reading.EpochId, reading.Elapsed, folded.TotalMilliseconds);
                _basePosition += folded;
                AnchorMasterUnlocked(reading);
                return _basePosition;
            }

            if (reading.Elapsed > _masterElapsedHighWater)
                _masterElapsedHighWater = reading.Elapsed;
            return _basePosition + (_masterElapsedHighWater - _masterAnchor);
        }
        return _basePosition + _stopwatch.Elapsed;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaClock));
    }
}
