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
    private TimeSpan   _lastFramePosition;
    private TimeSpan   _swAtLastFrame;
    private bool       _running;
    private readonly double _sampleRate;

    public override TimeSpan Position =>
        _running ? _lastFramePosition + (_sw.Elapsed - _swAtLastFrame) : _lastFramePosition;

    public override double SampleRate => _sampleRate;
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
        Log.LogDebug("NDIClock stopping at position={Position}", _lastFramePosition);
        _running = false;
        _sw.Stop();
        base.Stop();
    }

    public override void Reset()
    {
        Log.LogDebug("NDIClock reset");
        _lastFramePosition = TimeSpan.Zero;
        _swAtLastFrame     = TimeSpan.Zero;
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
        _lastFramePosition = TimeSpan.FromTicks(ndiTimestamp);
        _swAtLastFrame     = _sw.Elapsed;
    }
}
