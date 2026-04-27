using System.Diagnostics;

namespace S.Media.Core.Clock;

/// <summary>
/// Clock backed by an external hardware time source (e.g. <c>Pa_GetStreamTime</c>).
/// Falls back to a <see cref="Stopwatch"/> seamlessly whenever the provider returns
/// a non-positive value, and re-syncs as soon as valid hardware time resumes.
///
/// <para>
/// Implements <see cref="ISuppressesAutoAvDriftCorrection"/>: hardware time sources
/// represent the <em>output / DAC side</em> of the audio pipeline, offset from decoder
/// PTS by the full audio output buffer / resampler / sink latency. Comparing the audio
/// decode head against this clock (as <see cref="S.Media.Core.Routing.IAVRouter.GetAvDrift"/>
/// does) therefore mixes pipeline depth into the drift signal and produces large
/// "phantom drift" during startup settling. The playback layer routes around this by
/// using <see cref="S.Media.Core.Routing.IAVRouter.GetAvStreamHeadDrift"/> instead,
/// which baselines the first measurement and only tracks subsequent change.
/// </para>
/// </summary>
public class HardwareClock : MediaClockBase, ISuppressesAutoAvDriftCorrection
{
    private readonly Func<double> _secondsProvider;
    private readonly double       _sampleRate;

    // Fallback state — guarded by _fallbackLock (SpinLock: low-contention, sub-μs hold time)
    private SpinLock           _fallbackLock = new(enableThreadOwnerTracking: false);
    private readonly Stopwatch _fallbackSw = new();
    private TimeSpan           _lastValidPosition;
    private bool               _usingFallback;

    // Interpolation between hardware updates. Some audio backends (ALSA, JACK)
    // return the same Pa_GetStreamTime value between audio callbacks, making the
    // clock appear "frozen" for an entire buffer period. The PTS gate then sees
    // frames advancing past the frozen clock and holds them back, cutting content
    // FPS in half. A local stopwatch bridges the gap, re-syncing whenever the
    // hardware time actually changes.
    private readonly Stopwatch _interpSw = new();
    private TimeSpan           _lastHwPosition;

    // §3.31 / C5: debounce exit from fallback — require N consecutive valid hw
    // reads before trusting the hardware timer again. A single flaky valid read
    // during a driver stall would otherwise reset the fallback stopwatch,
    // snapping Position backwards by the amount we'd interpolated.
    private const int FallbackExitDebounce = 2;
    private int _consecutiveValidReads;

    private volatile bool _running;

    /// <param name="secondsProvider">
    /// Delegate returning the current hardware time in seconds (e.g. Pa_GetStreamTime).
    /// Return a value ≤ 0 to signal "not available".
    /// </param>
    /// <param name="sampleRate">Sample rate of the associated hardware output.</param>
    /// <param name="tickInterval">
    /// How often the <see cref="IMediaClock.Tick"/> event fires.
    /// Defaults to 20 ms when <see langword="null"/>.
    /// </param>
    public HardwareClock(
        Func<double> secondsProvider,
        double       sampleRate,
        TimeSpan?    tickInterval = null)
        : base(tickInterval ?? TimeSpan.FromMilliseconds(20))
    {
        ArgumentNullException.ThrowIfNull(secondsProvider);
        _secondsProvider = secondsProvider;
        _sampleRate      = sampleRate;
    }

    // ── IMediaClock ────────────────────────────────────────────────────────

    /// <summary>
    /// The hardware sample rate. Exposed as a concrete property (no longer on IMediaClock).
    /// Used by PortAudioClock and other hardware-backed clocks.
    /// </summary>
    public double SampleRate => _sampleRate;

    public override bool     IsRunning  => _running;

    public override TimeSpan Position
    {
        get
        {
            double hw = _secondsProvider();
            bool taken = false;
            try
            {
                _fallbackLock.Enter(ref taken);
                if (hw > 0.0)
                {
                    if (_usingFallback)
                    {
                        _consecutiveValidReads++;
                        if (_consecutiveValidReads < FallbackExitDebounce)
                            return _lastValidPosition + _fallbackSw.Elapsed;

                        _usingFallback = false;
                        _consecutiveValidReads = 0;
                        _fallbackSw.Reset();
                    }

                    var hwPos = TimeSpan.FromSeconds(hw);
                    if (hwPos != _lastHwPosition)
                    {
                        _lastHwPosition = hwPos;
                        _lastValidPosition = hwPos;
                        _interpSw.Restart();
                        return hwPos;
                    }

                    // Hardware time unchanged since last read (common on backends
                    // that update Pa_GetStreamTime only on audio callbacks).
                    // Interpolate forward from the last known position using a
                    // local stopwatch so the clock advances continuously and the
                    // PTS gate doesn't freeze-lock to half rate.
                    return _lastHwPosition + _interpSw.Elapsed;
                }

                _consecutiveValidReads = 0;
                if (!_usingFallback)
                {
                    _usingFallback = true;
                    _fallbackSw.Restart();
                }
                return _lastValidPosition + _fallbackSw.Elapsed;
            }
            finally
            {
                if (taken) _fallbackLock.Exit(useMemoryBarrier: false);
            }
        }
    }

    public override void Start()
    {
        _running = true;
        base.Start();
    }

    public override void Stop()
    {
        _running = false;
        _fallbackSw.Stop();
        _interpSw.Stop();
        base.Stop();
    }

    public override void Reset()
    {
        _lastValidPosition = TimeSpan.Zero;
        _lastHwPosition    = TimeSpan.Zero;
        _usingFallback     = false;
        _consecutiveValidReads = 0;
        _fallbackSw.Reset();
        _interpSw.Reset();
    }

    /// <summary>
    /// Updates the tick timer interval to match the output buffer duration.
    /// Call this after the hardware stream is opened and the exact buffer size is known.
    /// </summary>
    public void UpdateTickInterval(int framesPerBuffer) =>
        SetTickInterval(TimeSpan.FromSeconds(framesPerBuffer / _sampleRate));
}

