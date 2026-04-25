using System.Diagnostics;

namespace S.Media.Core.Clock;

/// <summary>
/// Video clock driven by presented frame PTS values.
/// Uses <see cref="Stopwatch"/> interpolation between frames
/// (same pattern as <c>NDIClock.UpdateFromFrame</c>).
/// </summary>
public sealed class VideoPtsClock : MediaClockBase
{
    // §3.29 / C3: _lastPts and _swAtLastPts are paired state read by Position on
    // consumer threads and written by UpdateFromFrame on the render thread.
    // A short lock is cheaper than paired Interlocked on 64-bit fields and
    // keeps Position tear-free across the pair. Hold time is sub-microsecond.
    private readonly Lock      _stateLock = new();
    private readonly Stopwatch _sw = new();
    private TimeSpan _lastPts;
    private TimeSpan _swAtLastPts;
    private volatile bool _running;
    private bool          _initialised;

    /// <inheritdoc/>
    public override TimeSpan Position
    {
        get
        {
            lock (_stateLock)
            {
                if (!_running) return _lastPts;
                return _initialised
                    ? _lastPts + (_sw.Elapsed - _swAtLastPts)
                    : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Nominal frame rate (e.g. 30, 60). Exposed as a concrete property.</summary>
    public double FrameRate { get; }

    /// <inheritdoc/>
    public override bool IsRunning => _running;

    /// <param name="frameRate">Nominal frame rate exposed to consumers (e.g. 30, 60).</param>
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
        lock (_stateLock) _sw.Start();
        _running = true;
        base.Start();
    }

    /// <inheritdoc/>
    public override void Stop()
    {
        if (!_running) return;
        _running = false;
        lock (_stateLock) _sw.Stop();
        base.Stop();
    }

    /// <inheritdoc/>
    public override void Reset()
    {
        lock (_stateLock)
        {
            _lastPts     = TimeSpan.Zero;
            _swAtLastPts = TimeSpan.Zero;
            _initialised = false;
            _sw.Reset();
        }
    }

    /// <summary>
    /// Called by the render loop after each presented frame.
    /// Records the frame PTS and resets the interpolation stopwatch.
    /// </summary>
    /// <param name="pts">The PTS of the frame that was just presented.</param>
    public void UpdateFromFrame(TimeSpan pts)
    {
        if (pts < TimeSpan.Zero) return;

        lock (_stateLock)
        {
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

            // Re-anchor only on a LARGE jump (seek / stream discontinuity); see the
            // long-form note below for why small forward drift MUST be ignored here.
            //
            // Short version: the AVRouter already runs its own cross-origin drift
            // correction (PtsDriftTracker) on the presentation path, and chasing
            // raw PTS forward here would form a positive feedback loop with that
            // correction. A backward jump of any size is a seek; a forward jump
            // larger than SeekThreshold is also a seek. Everything else is drift —
            // ignore.
            var seekThreshold = TimeSpan.FromMilliseconds(500);
            if (delta >= TimeSpan.Zero && delta < seekThreshold)
                return;
            if (delta < TimeSpan.Zero && -delta < seekThreshold)
                return;

            _lastPts = pts;
            _swAtLastPts = swNow;
        }
    }
}
