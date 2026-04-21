using Microsoft.Extensions.Logging;
using S.Media.Core;

namespace S.Media.NDI;

/// <summary>
/// <see cref="MediaClockBase"/> backed by NDI frame timestamps (100 ns ticks).
/// Falls back gracefully to elapsed time between frames for the sub-tick position.
/// </summary>
public sealed class NDIClock : MediaClockBase
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIClock));

    private readonly System.Diagnostics.Stopwatch _sw         = new();
    // All three tick fields are read on any thread (IMediaClock.Position consumers) and
    // written on the capture thread (UpdateFromFrame).  Interlocked reads/writes avoid
    // torn 64-bit reads on 32-bit hosts and provide a release/acquire barrier so consumers
    // see a consistent snapshot of (lastFramePosition, swAtLastFrame).
    private long       _lastFramePositionTicks;
    private long       _swAtLastFrameTicks;
    private volatile bool _running;
    private readonly double _sampleRate;

    public override TimeSpan Position
    {
        get
        {
            if (!_running)
                return TimeSpan.FromTicks(Interlocked.Read(ref _lastFramePositionTicks));
            long lastFrame = Interlocked.Read(ref _lastFramePositionTicks);
            long swAtLast  = Interlocked.Read(ref _swAtLastFrameTicks);
            return TimeSpan.FromTicks(lastFrame + (_sw.Elapsed.Ticks - swAtLast));
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
        base.Stop();
    }

    public override void Reset()
    {
        Log.LogDebug("NDIClock reset");
        Interlocked.Exchange(ref _lastFramePositionTicks, 0);
        Interlocked.Exchange(ref _swAtLastFrameTicks, 0);
        _sw.Reset();
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
        // Sample the stopwatch BEFORE publishing the new frame position so a concurrent
        // reader never sees (newPos, oldSwAtLast) — which would produce a time jump.
        long swNow = _sw.Elapsed.Ticks;
        Interlocked.Exchange(ref _swAtLastFrameTicks, swNow);
        Interlocked.Exchange(ref _lastFramePositionTicks, ndiTimestamp);
    }
}
