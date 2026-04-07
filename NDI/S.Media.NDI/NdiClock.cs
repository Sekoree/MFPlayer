using S.Media.Core.Clock;

namespace S.Media.NDI;

/// <summary>
/// <see cref="MediaClockBase"/> backed by NDI frame timestamps (100 ns ticks).
/// Falls back gracefully to elapsed time between frames for the sub-tick position.
/// </summary>
public sealed class NdiClock : MediaClockBase
{
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
    public NdiClock(double sampleRate = 48000, double tickIntervalMs = 10)
        : base(TimeSpan.FromMilliseconds(tickIntervalMs))
    {
        _sampleRate = sampleRate;
    }

    public override void Start()
    {
        if (_running) return;
        _sw.Start();
        _running = true;
        base.Start();
    }

    public override void Stop()
    {
        if (!_running) return;
        _running = false;
        _sw.Stop();
        base.Stop();
    }

    public override void Reset()
    {
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
        if (ndiTimestamp <= 0) return;
        _lastFramePosition = TimeSpan.FromTicks(ndiTimestamp);
        _swAtLastFrame     = _sw.Elapsed;
    }
}
