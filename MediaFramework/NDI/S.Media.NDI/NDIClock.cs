using Microsoft.Extensions.Logging;
using S.Media.Core;
using S.Media.Core.Clock;

namespace S.Media.NDI;

/// <summary>
/// <see cref="MediaClockBase"/> backed by NDI frame timestamps (100 ns ticks).
/// Falls back gracefully to elapsed time between frames for the sub-tick position.
/// </summary>
public sealed class NDIClock : MediaClockBase, ISuppressesAutoAvDriftCorrection
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIClock));

    private readonly System.Diagnostics.Stopwatch _sw         = new();
    // All three tick fields are read on any thread (IMediaClock.Position consumers) and
    // written on the capture thread (UpdateFromFrame).  Interlocked reads/writes avoid
    // torn 64-bit reads on 32-bit hosts and provide a release/acquire barrier so consumers
    // see a consistent snapshot of (lastFramePosition, swAtLastFrame).
    private long       _lastFramePositionTicks;
    private long       _swAtLastFrameTicks;
    // Monotonic floor for Position reads across update races/jitter.
    private long _monotonicFloorTicks;
    // Seqlock-style version for atomic (position, stopwatch-origin) snapshots.
    // Even = stable, odd = writer in progress.
    private int _snapshotVersion;
    private volatile bool _running;
    private readonly double _sampleRate;

    public override TimeSpan Position
    {
        get
        {
            if (!_running)
                return TimeSpan.FromTicks(Interlocked.Read(ref _lastFramePositionTicks));

            while (true)
            {
                int v1 = Volatile.Read(ref _snapshotVersion);
                if ((v1 & 1) != 0)
                    continue; // writer is updating; retry

                long lastFrame = Interlocked.Read(ref _lastFramePositionTicks);
                long swAtLast  = Interlocked.Read(ref _swAtLastFrameTicks);
                long swNow     = _sw.Elapsed.Ticks;

                int v2 = Volatile.Read(ref _snapshotVersion);
                if (v1 != v2 || (v2 & 1) != 0)
                    continue; // observed torn snapshot; retry

                long elapsed = swNow - swAtLast;
                if (elapsed < 0) elapsed = 0;
                long candidate = lastFrame + elapsed;
                long floor = Interlocked.Read(ref _monotonicFloorTicks);
                if (candidate < floor) candidate = floor;
                PublishMonotonicFloor(candidate);
                return TimeSpan.FromTicks(candidate);
            }
        }
    }

    /// <summary>Nominal sample rate (exposed as a concrete property, no longer on IMediaClock).</summary>
    public double SampleRate => _sampleRate;
    public override bool   IsRunning  => _running;

    /// <param name="sampleRate">Nominal sample rate (used by consumers; NDI frame sync handles actual timing).</param>
    /// <param name="tickIntervalMs">How often the base Tick event fires (default 10 ms).</param>
    public NDIClock(double sampleRate = 48000, double tickIntervalMs = 10)
        : base(TimeSpan.FromMilliseconds(tickIntervalMs))
    {
        _sampleRate = sampleRate;
    }

    public override void Start()
    {
        if (_running) return;
        Log.LogDebug("NDIClock starting: sampleRate={SampleRate}", _sampleRate);
        _sw.Start();
        _running = true;
        base.Start();
    }

    public override void Stop()
    {
        if (!_running) return;
        Log.LogDebug("NDIClock stopping at position={Position}", TimeSpan.FromTicks(Interlocked.Read(ref _lastFramePositionTicks)));
        _running = false;
        _sw.Stop();
        // §4.16 / N4 — drop the writer claim so a fresh Start can pick a new
        // leader. Without this a restart would stay with whichever channel
        // claimed the lead last time even if the policy intended the other.
        ResetWriterClaim();
        base.Stop();
    }

    public override void Reset()
    {
        Log.LogDebug("NDIClock reset");
        Interlocked.Exchange(ref _lastFramePositionTicks, 0);
        Interlocked.Exchange(ref _swAtLastFrameTicks, 0);
        Interlocked.Exchange(ref _monotonicFloorTicks, 0);
        ResetWriterClaim();
        _sw.Reset();
    }

    /// <summary>
    /// Clears the position and monotonic floor without stopping the internal
    /// stopwatch, so the clock can be re-seeded from a new source's first
    /// frame.  Use this on file/source switches where the NDI endpoint stays
    /// alive across sources — without this the monotonic floor from the
    /// previous file's last PTS blocks all advances for the new file.
    /// </summary>
    public void ResetForNewSource()
    {
        long swNow = _sw.Elapsed.Ticks;
        Interlocked.Increment(ref _snapshotVersion);
        Interlocked.Exchange(ref _lastFramePositionTicks, 0);
        Interlocked.Exchange(ref _swAtLastFrameTicks, swNow);
        Interlocked.Increment(ref _snapshotVersion);
        Interlocked.Exchange(ref _monotonicFloorTicks, 0);
        ResetWriterClaim();
    }

    /// <summary>
    /// Called by NDI channel implementations each time a frame arrives.
    /// <paramref name="ndiTimestamp"/> is in 100 ns units (NDI SDK convention).
    /// Pass 0 / negative to skip the update.
    /// </summary>
    public void UpdateFromFrame(long ndiTimestamp)
    {
        // Guard: skip zero/negative and NDIlib_recv_timestamp_undefined (INT64_MAX = long.MaxValue).
        if (ndiTimestamp <= 0 || ndiTimestamp == long.MaxValue) return;

        // Publish a consistent (framePts, swAtLastFrame) pair.
        // NOTE: we intentionally do NOT clamp ndiTimestamp to the monotonic floor here.
        // The Position getter has its own floor to prevent backward reads, but clamping
        // the incoming frame timestamp would inflate _lastFramePositionTicks by the
        // receiver-vs-sender clock rate difference each frame, causing accumulating
        // drift (~2 ms/s) that eventually triggers the SDL3 catch-up drop path.
        long swNow = _sw.Elapsed.Ticks;
        Interlocked.Increment(ref _snapshotVersion); // odd: writer active
        Interlocked.Exchange(ref _lastFramePositionTicks, ndiTimestamp);
        Interlocked.Exchange(ref _swAtLastFrameTicks, swNow);
        Interlocked.Increment(ref _snapshotVersion); // even: stable
        PublishMonotonicFloor(ndiTimestamp);
    }

    private void PublishMonotonicFloor(long candidate)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _monotonicFloorTicks);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref _monotonicFloorTicks, candidate, current) == current)
                return;
        }
    }

    // §4.16 / N4 — atomic "first writer wins" claim. 0 = unclaimed, 1 = audio,
    // 2 = video. Once set, only the matching caller successfully commits an
    // update; the other side's TryUpdateFromFrame is a no-op. Reset on Stop
    // so a restart behaves like a fresh session.
    private int _writerClaim;

    internal const int WriterClaimAudio = 1;
    internal const int WriterClaimVideo = 2;

    /// <summary>
    /// §4.16 / N4 — called by the capture loop under <see cref="NDIClockPolicy.FirstWriter"/>.
    /// Returns <see langword="true"/> when the caller has (or has just won)
    /// the writer claim and updated the clock; <see langword="false"/> when
    /// the other side owns the lead and this call was ignored.
    /// </summary>
    internal bool TryUpdateFromFrame(long ndiTimestamp, int writerKind)
    {
        if (ndiTimestamp <= 0 || ndiTimestamp == long.MaxValue) return false;

        int current = Volatile.Read(ref _writerClaim);
        if (current == 0)
        {
            // Race: attempt to claim the lead. Losers see the winner's value
            // and fall through to the "not leader" branch below.
            current = Interlocked.CompareExchange(ref _writerClaim, writerKind, 0);
            if (current == 0) current = writerKind;
        }
        if (current != writerKind) return false;

        UpdateFromFrame(ndiTimestamp);
        return true;
    }

    /// <summary>
    /// §4.16 / N4 — resets the writer claim so a fresh Start can pick a new
    /// leader. Called from <see cref="Stop"/> (paired with the restart path).
    /// </summary>
    internal void ResetWriterClaim() => Interlocked.Exchange(ref _writerClaim, 0);
}
