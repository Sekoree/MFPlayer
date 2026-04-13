using System.Diagnostics;

namespace S.Media.Core;

/// <summary>
/// Video clock driven by presented frame PTS values.
/// Uses <see cref="Stopwatch"/> interpolation between frames
/// (same pattern as <c>NDIClock.UpdateFromFrame</c>).
/// </summary>
public sealed class VideoPtsClock : MediaClockBase
{
    private readonly Stopwatch _sw = new();
    private TimeSpan _lastPts;
    private TimeSpan _swAtLastPts;
    private bool     _running;
    private bool     _initialised;

    /// <inheritdoc/>
    public override TimeSpan Position =>
        _running
            ? (_initialised
                ? _lastPts + (_sw.Elapsed - _swAtLastPts)
                : TimeSpan.Zero)
            : _lastPts;

    /// <inheritdoc/>
    public override double SampleRate { get; }

    /// <inheritdoc/>
    public override bool IsRunning => _running;

    /// <param name="sampleRate">
    /// Nominal sample rate exposed to consumers.
    /// For video this is typically the frame rate (e.g. 30, 60).
    /// </param>
    /// <param name="tickIntervalMs">How often the base Tick event fires (default 16 ms ≈ 60 Hz).</param>
    public VideoPtsClock(double sampleRate = 30, double tickIntervalMs = 16)
        : base(TimeSpan.FromMilliseconds(tickIntervalMs))
    {
        SampleRate = sampleRate;
    }

    /// <inheritdoc/>
    public override void Start()
    {
        if (_running) return;
        _sw.Start();
        _running = true;
        base.Start();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        if (!_running) return;
        _running = false;
        _sw.Stop();
        base.Stop();
    }

    /// <inheritdoc/>
    public override void Reset()
    {
        _lastPts     = TimeSpan.Zero;
        _swAtLastPts = TimeSpan.Zero;
        _initialised = false;
        _sw.Reset();
    }

    /// <summary>
    /// Called by the render loop after each presented frame.
    /// Records the frame PTS and resets the interpolation stopwatch.
    /// </summary>
    /// <param name="pts">The PTS of the frame that was just presented.</param>
    public void UpdateFromFrame(TimeSpan pts)
    {
        if (pts < TimeSpan.Zero) return;

        var swNow = _sw.Elapsed;

        // Accept PTS=0 as a valid initial anchor so the clock is properly
        // synchronised from the very first presented frame. Without this, the
        // clock runs freely from Start() and races ahead of the actual frame
        // timeline, causing the mixer to drop all frames as stale.
        if (!_initialised)
        {
            _initialised = true;
            _lastPts = pts;
            _swAtLastPts = swNow;
            return;
        }

        var predicted = _lastPts + (swNow - _swAtLastPts);

        // Never pull the clock backwards/behind current wall-clock progression.
        if (pts <= predicted)
            return;

        // Ignore tiny forward jitter; only apply a meaningful resync correction.
        double fps = SampleRate > 1 ? SampleRate : 30.0;
        var minCorrection = TimeSpan.FromSeconds(0.5 / fps);
        if (pts - predicted < minCorrection)
            return;

        _lastPts = pts;
        _swAtLastPts = swNow;
    }
}
