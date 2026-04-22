using System.Diagnostics;

namespace S.Media.Core.Clock;

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

    /// <summary>Nominal frame rate (e.g. 30, 60). Exposed as a concrete property.</summary>
    public double FrameRate { get; }

    /// <inheritdoc/>
    public override bool IsRunning => _running;

    /// <param name="frameRate">
    /// Nominal frame rate exposed to consumers (e.g. 30, 60).
    /// </param>
    /// <param name="tickIntervalMs">How often the base Tick event fires (default 16 ms ≈ 60 Hz).</param>
    public VideoPtsClock(double frameRate = 30, double tickIntervalMs = 16)
        : base(TimeSpan.FromMilliseconds(tickIntervalMs))
    {
        FrameRate = frameRate;
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
        var delta = pts - predicted;

        // Only re-anchor on a LARGE jump (seek / stream discontinuity).
        //
        // Small forward drift MUST be ignored here. The AVRouter already runs
        // its own cross-origin drift correction (PtsDriftTracker) on the
        // presentation path, which absorbs decoder-vs-wall offsets by walking
        // the PTS origin in its *relative* comparison. If this clock ALSO
        // chases the raw PTS forward by 20–40 ms chunks, the two correction
        // loops form a positive feedback:
        //
        //   1) drift-tracker absorbs a small decoder-ahead offset → gate opens
        //      for frames whose absolute PTS is slightly past clock.Position;
        //   2) those frames are presented; this method re-anchors _lastPts
        //      forward → clock.Position jumps;
        //   3) gate (driven by the now-advanced clock) lets through frames
        //      even further ahead — GOTO 2.
        //
        // Once the subscription ring fills at ~30 s of content the loop
        // locks at the decoder's free-running rate (~2.3× realtime in the
        // reported log). Limiting re-anchor to real seeks breaks the cycle
        // while still letting the stopwatch interpolate at wall rate between
        // anchors.
        //
        // A backward jump of any size is a seek; a forward jump larger than
        // SeekThreshold is also a seek. Everything else is drift — ignore.
        var seekThreshold = TimeSpan.FromMilliseconds(500);
        if (delta >= TimeSpan.Zero && delta < seekThreshold)
            return;
        if (delta < TimeSpan.Zero && -delta < seekThreshold)
            return;

        _lastPts = pts;
        _swAtLastPts = swNow;
    }
}
