using System.Diagnostics;

namespace S.Media.Core;

/// <summary>
/// Clock backed by an external hardware time source (e.g. <c>Pa_GetStreamTime</c>).
/// Falls back to a <see cref="Stopwatch"/> seamlessly whenever the provider returns
/// a non-positive value, and re-syncs as soon as valid hardware time resumes.
/// </summary>
public class HardwareClock : MediaClockBase
{
    private readonly Func<double> _secondsProvider;
    private readonly double       _sampleRate;

    // Fallback state — guarded by _fallbackLock (SpinLock: low-contention, sub-μs hold time)
    private SpinLock           _fallbackLock = new(enableThreadOwnerTracking: false);
    private readonly Stopwatch _fallbackSw = new();
    private TimeSpan           _lastValidPosition;
    private bool               _usingFallback;

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
                    // Re-sync fallback whenever hardware is valid
                    if (_usingFallback)
                    {
                        _usingFallback = false;
                        _fallbackSw.Reset();
                    }
                    _lastValidPosition = TimeSpan.FromSeconds(hw);
                    return _lastValidPosition;
                }

                // Hardware unavailable — continue from last known position via stopwatch
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
        base.Stop();
    }

    public override void Reset()
    {
        _lastValidPosition = TimeSpan.Zero;
        _usingFallback     = false;
        _fallbackSw.Reset();
    }

    /// <summary>
    /// Updates the tick timer interval to match the output buffer duration.
    /// Call this after the hardware stream is opened and the exact buffer size is known.
    /// </summary>
    public void UpdateTickInterval(int framesPerBuffer) =>
        SetTickInterval(TimeSpan.FromSeconds(framesPerBuffer / _sampleRate));
}

