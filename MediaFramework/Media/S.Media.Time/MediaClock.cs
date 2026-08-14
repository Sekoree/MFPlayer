using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;

namespace S.Media.Time;

/// <summary>
/// Master playback clock. Free-running by default (backed by
/// <see cref="Stopwatch"/>); call <see cref="SetMaster"/> to slave it to an external
/// <see cref="IPlaybackClock"/> (typically the audio output) so reported position
/// tracks actual played samples instead of wall time.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VideoTick"/> is driven by an internal wall-clock thread regardless of master
/// attachment - it is a "render at this cadence" signal, not "media time advanced by X." When a tick
/// handler runs long or the thread wakes late, the default driver <strong>bursts</strong> missed
/// deadlines (capped) and then fast-forwards the schedule so a long stall does not freeze the process.
/// The rational-rate constructor can instead coalesce missed deadlines, which is the appropriate
/// policy for a rendered composition.
/// </para>
/// <para>
/// <strong>The driver services exactly one deadline grid.</strong> It used to service three - a 100 Hz
/// audio tick, the video grid and a 30 Hz position broadcast - and the outer two had no subscriber
/// anywhere in the framework or the apps, so roughly 130 of every 190 wakes per second raised events
/// nobody handled. Worse, the 30 Hz one was not free: it read <see cref="CurrentPosition"/>, which
/// takes <c>_gate</c> and walks the entire master chain. A show runs one clock per voice plus one per
/// composition, so that cost was paid sixteen times over on a 13-stem cue. Anything that needs a
/// continuous position readout should poll <see cref="CurrentPosition"/> at its own display rate.
/// </para>
/// <para>
/// <see cref="PositionChanged"/> therefore fires on <strong>re-anchor only</strong> - <see cref="Reset"/>
/// and <see cref="Seek"/> raise it synchronously on the caller's thread immediately after updating the
/// stored position (marshal if your UI requires a single context). It is not a cadence.
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
    /// <summary>
    /// Video-tick cadence used by the parameterless constructors (~60 Hz). This is a FALLBACK for
    /// callers that do not know their source's frame rate - a host that does should derive the
    /// interval from it (see <c>VideoFormatPacing.PresentationTickInterval</c>) rather than take this,
    /// because a fixed 60 Hz tick beats against any source that is not exactly 60 fps.
    /// </summary>
    public static readonly TimeSpan DefaultVideoTickInterval = TimeSpan.FromTicks(166_667); // ~60 Hz

    private readonly Stopwatch _stopwatch = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _videoTickInterval;
    private readonly RationalFrameGrid? _videoFrameGrid;
    private readonly VideoTickCatchUpPolicy _videoTickCatchUpPolicy;
    private readonly ILogger? _log;
    private long _lastVideoTickIndex;
    private long _skippedVideoTicks;

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

    /// <summary>
    /// The ONE origin every clock's frame grid is measured from, process-wide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each driver used to anchor its grid at its own <see cref="Start"/>, so thirteen voices at 60 fps
    /// ticked at thirteen unrelated phases: siblings selecting frames for the same master instant could
    /// do so up to a full frame period apart, for no reason other than the order their clocks happened
    /// to start in. A shared origin makes same-rate clocks tick on the SAME instants by construction -
    /// the sync benefit a shared timing wheel would have bought, without putting independent tick
    /// handlers behind one thread (a composition pump is ~5.6 ms of a 16.7 ms budget; three of them
    /// serialized would saturate it and turn independent pumps into a head-of-line queue).
    /// </para>
    /// <para>
    /// It also removes a discontinuity: the grid index no longer restarts at 1 on every Start, so a
    /// pause/resume does not rewind anything derived from it.
    /// </para>
    /// </remarks>
    private static readonly long SharedGridEpoch = Stopwatch.GetTimestamp();

    public MediaClock() : this(DefaultVideoTickInterval, logger: null) { }

    public MediaClock(ILogger? logger)
        : this(DefaultVideoTickInterval, logger) { }

    public MediaClock(TimeSpan videoTickInterval, ILogger? logger = null)
    {
        if (videoTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(videoTickInterval));
        _videoTickInterval = videoTickInterval;
        _videoTickCatchUpPolicy = VideoTickCatchUpPolicy.Burst;
        _log = logger;
    }

    /// <summary>
    /// Creates a clock whose video deadlines lie on an exact rational frame grid. Unlike a rounded
    /// <see cref="TimeSpan"/> interval, absolute-index deadlines do not accumulate timing error.
    /// </summary>
    public MediaClock(
        Rational videoFrameRate,
        VideoTickCatchUpPolicy videoTickCatchUpPolicy = VideoTickCatchUpPolicy.Coalesce,
        ILogger? logger = null)
    {
        if (videoFrameRate.Numerator <= 0 || videoFrameRate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(videoFrameRate));

        _videoFrameGrid = new RationalFrameGrid(videoFrameRate);
        _videoTickInterval = _videoFrameGrid.Value.ApproximatePeriod;
        _videoTickCatchUpPolicy = videoTickCatchUpPolicy;
        _log = logger;
    }

    /// <summary>Raised when the position is explicitly re-established (<see cref="Seek"/>,
    /// <see cref="Reset"/>), synchronously on the caller's thread. NOT a cadence - see the type remarks.</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler? VideoTick;

    /// <summary>Absolute frame-grid index associated with the most recently raised video tick.</summary>
    public long LastVideoTickIndex => Interlocked.Read(ref _lastVideoTickIndex);

    /// <summary>Video deadlines coalesced because the driver woke or completed a handler late.</summary>
    public long SkippedVideoTicks => Interlocked.Read(ref _skippedVideoTicks);

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

    /// <summary>
    /// Shifts the pending start back by <paramref name="lead"/>, so the position crosses zero
    /// <paramref name="lead"/> after <see cref="Start"/> instead of at it.
    /// </summary>
    /// <remarks>
    /// For a voice slaved to an already-advancing master (a silent video cue on the show's audible
    /// clock): anchoring at Start makes its timeline advance immediately, while a sounding voice's
    /// producer clock holds at zero until its first sample is actually audible - one audio-pipeline
    /// depth later. Deferring by that depth puts frame 0 on screen at the same wall moment sample 0
    /// leaves the speaker. Consumers see a briefly negative position and simply wait for zero.
    /// No-op on a running clock - a live cadence is not re-based (that is what <see cref="Seek"/>
    /// is for, and it is a transport action, not a start correction).
    /// </remarks>
    public void DeferStart(TimeSpan lead)
    {
        if (lead <= TimeSpan.Zero)
            return;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_isRunning)
                return;
            _basePosition -= lead;
            TraceLog.LogDebug("DeferStart: lead={LeadMs}ms position={Position}",
                lead.TotalMilliseconds, _basePosition);
        }
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
        // Deadlines are absolute on the SHARED grid, so this loop joins the cadence already in progress
        // rather than starting one of its own: seek forward to the first boundary still ahead of us.
        var nextVideoIndex = FrameIndexAt(Stopwatch.GetElapsedTime(SharedGridEpoch)) + 1;
        var nextVideo     = VideoDeadlineAt(nextVideoIndex);

        var waitHandle = token.WaitHandle;

        while (!token.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(SharedGridEpoch);
            var sleep = nextVideo - elapsed;

            if (sleep > TimeSpan.Zero)
            {
                // WaitHandle.WaitOne(TimeSpan) TRUNCATES to whole milliseconds, so a sub-millisecond
                // remainder waits zero and returns instantly - and this loop then re-runs, and waits
                // zero again, until the deadline finally passes. That busy-spin was the STEADY STATE,
                // not an edge case: the deadlines practically never land on a whole-millisecond
                // boundary, so nearly every wait gave back its fraction to be spun. ~59 ms of every
                // second, on a thread at AboveNormal priority - and a show runs one clock PER VOICE,
                // so a 13-stem cue paid it thirteen times over (measured ~10.5% of a core per clock
                // thread, ~1.5 cores total). Rounding up costs at most a millisecond of lateness on one
                // tick; the accumulator below still advances by exact intervals, so the cadence itself
                // does not drift.
                var waitMs = (int)Math.Ceiling(sleep.TotalMilliseconds);
                if (waitHandle.WaitOne(waitMs)) break;
            }

            elapsed = Stopwatch.GetElapsedTime(SharedGridEpoch);

            if (elapsed >= nextVideo)
            {
                if (_videoTickCatchUpPolicy == VideoTickCatchUpPolicy.Coalesce
                    && _videoFrameGrid is { } grid)
                {
                    var dueIndex = Math.Max(nextVideoIndex, grid.FrameAtOrBefore(elapsed));
                    var skipped = dueIndex - nextVideoIndex;
                    if (skipped > 0)
                        Interlocked.Add(ref _skippedVideoTicks, skipped);
                    Interlocked.Exchange(ref _lastVideoTickIndex, dueIndex);
                    SafeInvoke(VideoTick);
                    nextVideoIndex = dueIndex + 1;
                    nextVideo = grid.DeadlineAt(nextVideoIndex);
                    elapsed = Stopwatch.GetElapsedTime(SharedGridEpoch);
                }
                else
                {
                    var videoBurst = 0;
                    while (elapsed >= nextVideo && videoBurst++ < 64)
                    {
                        Interlocked.Exchange(ref _lastVideoTickIndex, nextVideoIndex++);
                        SafeInvoke(VideoTick);
                        nextVideo = VideoDeadlineAt(nextVideoIndex);
                        elapsed = Stopwatch.GetElapsedTime(SharedGridEpoch);
                    }

                    while (nextVideo <= elapsed)
                    {
                        nextVideoIndex++;
                        nextVideo = VideoDeadlineAt(nextVideoIndex);
                    }
                }
            }
        }
    }

    private TimeSpan VideoDeadlineAt(long frameIndex) => _videoFrameGrid is { } grid
        ? grid.DeadlineAt(frameIndex)
        : TimeSpan.FromTicks(checked(_videoTickInterval.Ticks * frameIndex));

    /// <summary>The grid index at or before <paramref name="elapsed"/> on the shared origin.</summary>
    private long FrameIndexAt(TimeSpan elapsed) => _videoFrameGrid is { } grid
        ? grid.FrameAtOrBefore(elapsed)
        : elapsed.Ticks / _videoTickInterval.Ticks;

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
            else if (reading.Elapsed < _masterElapsedHighWater)
                NoteMasterRegressionUnlocked(reading);
            return _basePosition + (_masterElapsedHighWater - _masterAnchor);
        }
        return _basePosition + _stopwatch.Elapsed;
    }

    /// <summary>
    /// Records that the master went BACKWARDS without announcing a new epoch. Must be called under
    /// <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clamp above is correct and stays: holding at the high-water is the only safe response, and
    /// removing it would let a misbehaving master rewind every playhead derived from it. What was wrong
    /// was that it happened <em>silently</em>. A voice's position passes through several layers that each
    /// enforce this same contract, so a clock that breaks it is absorbed by the first clamp above it and
    /// the symptom appears somewhere else entirely - which is exactly the debugging pattern this
    /// subsystem kept producing. A clamp that fires is not a normal event: it means the layer BELOW is
    /// broken, or a read tore across its re-anchor. Counting it makes the real culprit nameable.
    /// </para>
    /// <para>
    /// Logged once per clock at Warning (with the offending master's type), then counted only - a broken
    /// master is read hundreds of times a second and must not be able to flood the log.
    /// </para>
    /// </remarks>
    private void NoteMasterRegressionUnlocked(ClockReading reading)
    {
        _masterRegressions++;
        var backwardsBy = _masterElapsedHighWater - reading.Elapsed;
        if (backwardsBy > _worstMasterRegression)
            _worstMasterRegression = backwardsBy;
        if (_masterRegressions != 1)
            return;

        TraceLog.LogWarning(
            "Position: master {Master} regressed inside epoch {Epoch} by {BackwardsMs}ms " +
            "(high-water={HighWater}, read={Read}) - held at the high-water. This is a broken per-epoch " +
            "monotonic contract in the master, or a read torn across its re-anchor; further occurrences " +
            "on this clock are counted only.",
            _master?.GetType().Name ?? "(none)", reading.EpochId, backwardsBy.TotalMilliseconds,
            _masterElapsedHighWater, reading.Elapsed);
    }

    /// <summary>
    /// How many times the attached master broke its per-epoch monotonic contract and was held at the
    /// high-water, and the worst single regression. Non-zero means a clock BELOW this one is faulty -
    /// see <see cref="NoteMasterRegressionUnlocked"/>. Reported through <c>MediaPlayer.GetMetrics</c>.
    /// </summary>
    public (long Count, TimeSpan Worst) MasterRegressions
    {
        get { lock (_gate) return (_masterRegressions, _worstMasterRegression); }
    }

    private long _masterRegressions;
    private TimeSpan _worstMasterRegression;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaClock));
    }
}
