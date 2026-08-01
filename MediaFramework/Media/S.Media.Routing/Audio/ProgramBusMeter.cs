namespace S.Media.Routing;

/// <summary>Measured level of one logical channel, as of the moment it was sampled.</summary>
/// <param name="PeakDb">Decaying peak in dBFS; <see cref="ProgramBusMeter.SilenceDb"/> when the
/// channel has been silent long enough for the peak to fall off the bottom.</param>
/// <param name="RmsDb">Smoothed RMS in dBFS over roughly <see cref="ProgramBusMeter.RmsWindow"/>,
/// or <see cref="ProgramBusMeter.SilenceDb"/> while silent.</param>
/// <param name="Clipped">Sticky: at least one sample reached full scale since the last
/// <see cref="ProgramBusMeter.ResetClip"/>.</param>
public readonly record struct ProgramChannelLevel(float PeakDb, float RmsDb, bool Clipped);

/// <summary>
/// Per-logical-channel peak/RMS metering for the V-wide program bus.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a "logical output level" is physically meaningful: a logical channel has no
/// device of its own, so it can only be measured where the program sum exists - inside
/// <see cref="ProgramBusSource.ReadInto"/>, after every producer has mixed and before the V×R patch
/// fans the bus out to terminals. Metering after the patch would measure devices, not logical outputs,
/// and would count a channel patched to three terminals three times.
/// </para>
/// <para>
/// Two properties are deliberate, and both are corrections of the app-side meter this replaces.
/// <b>Reads are non-destructive</b>, so any number of consumers (an output strip, a diagnostics window,
/// a test) can observe the same meter without stealing each other's values - a read-and-reset meter
/// silently zeroes whichever consumer polls second. And <b>decay is measured in frames, not wall
/// time</b>: the meter never reads a clock, so a UI polling at 30 Hz cannot miss a peak that happened
/// between two polls (the peak decays from the audio thread, not from the observer), and the behaviour
/// is deterministic under test rather than dependent on scheduling.
/// </para>
/// <para>
/// Ballistics beyond this - the operator's choice of PPM/VU, hold time, and whether clip clears on
/// click or on a timer - are presentation and belong to the host, which is why this exposes a raw
/// decaying peak plus a smoothed RMS rather than a finished meter deflection.
/// </para>
/// <para>Thread-safety: <see cref="Observe"/> is called from the audio thread only.
/// <see cref="Snapshot"/> and <see cref="ResetClip"/> may be called from any thread; they read
/// per-channel state that is written by a single writer, so a snapshot may straddle a chunk boundary
/// but can never tear a single value.</para>
/// </remarks>
public sealed class ProgramBusMeter
{
    /// <summary>The floor reported for silence. Anything quieter reads as exactly this.</summary>
    public const float SilenceDb = -120f;

    /// <summary>Peak fall-back rate. 20 dB/s is the usual broadcast-PPM return.</summary>
    public const float PeakDecayDbPerSecond = 20f;

    /// <summary>Nominal averaging window for the RMS estimate.</summary>
    public static readonly TimeSpan RmsWindow = TimeSpan.FromMilliseconds(300);

    private static readonly float SilenceLinear = DbToLinear(SilenceDb);

    private readonly int _channels;
    private readonly float[] _peak;      // linear, decaying
    private readonly float[] _meanSquare; // linear power, exponentially smoothed
    private readonly bool[] _clipped;
    private readonly float _peakDecayPerFrame;
    private readonly float _rmsAlphaPerFrame;

    public ProgramBusMeter(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _channels = channels;
        _peak = new float[channels];
        _meanSquare = new float[channels];
        _clipped = new bool[channels];

        // Peak: multiply by this each frame so the level falls PeakDecayDbPerSecond per second.
        _peakDecayPerFrame = (float)Math.Pow(10d, -PeakDecayDbPerSecond / (20d * sampleRate));

        // RMS: one-pole smoothing whose time constant is the window. Clamped so a tiny sample rate
        // cannot produce alpha >= 1 (which would make the estimate ignore history entirely).
        var windowFrames = Math.Max(1d, RmsWindow.TotalSeconds * sampleRate);
        _rmsAlphaPerFrame = (float)Math.Clamp(1d / windowFrames, 1e-6d, 1d);
    }

    public int Channels => _channels;

    /// <summary>
    /// Folds one chunk of interleaved bus audio into the meter. Allocation-free and single-pass; runs
    /// on the audio thread, so it does exactly one multiply-add and two comparisons per sample.
    /// </summary>
    /// <param name="interleaved">Interleaved bus audio, <paramref name="frames"/> ×
    /// <see cref="Channels"/> samples.</param>
    /// <param name="frames">Frame count in <paramref name="interleaved"/>.</param>
    public void Observe(ReadOnlySpan<float> interleaved, int frames)
    {
        if (frames <= 0)
            return;
        if (interleaved.Length < frames * _channels)
            throw new ArgumentException(
                $"expected at least {frames * _channels} samples for {frames} frames of {_channels} channels",
                nameof(interleaved));

        // Decay applied once for the whole chunk rather than per frame: the peak is a display value,
        // and pow() per frame per channel would be a real cost at V=64 for no visible difference.
        var chunkDecay = MathF.Pow(_peakDecayPerFrame, frames);

        for (var channel = 0; channel < _channels; channel++)
        {
            var peak = _peak[channel] * chunkDecay;
            var meanSquare = _meanSquare[channel];
            var clipped = _clipped[channel];

            for (var frame = 0; frame < frames; frame++)
            {
                var sample = interleaved[frame * _channels + channel];
                var magnitude = MathF.Abs(sample);

                if (magnitude > peak)
                    peak = magnitude;
                if (magnitude >= 1f)
                    clipped = true;

                // One-pole towards the instantaneous power.
                meanSquare += _rmsAlphaPerFrame * (sample * sample - meanSquare);
            }

            // Flush denormals and sub-floor values to zero; denormal arithmetic on the audio thread is
            // a real (and famously puzzling) stall on some CPUs.
            _peak[channel] = peak < SilenceLinear ? 0f : peak;
            _meanSquare[channel] = meanSquare < 1e-20f ? 0f : meanSquare;
            _clipped[channel] = clipped;
        }
    }

    /// <summary>
    /// Copies the current level of every channel into <paramref name="destination"/>. Pure: calling it
    /// twice in a row yields the same values (modulo intervening audio), and one consumer's read never
    /// affects another's.
    /// </summary>
    public void Snapshot(Span<ProgramChannelLevel> destination)
    {
        if (destination.Length < _channels)
            throw new ArgumentException(
                $"destination holds {destination.Length} entries, need {_channels}", nameof(destination));

        for (var channel = 0; channel < _channels; channel++)
        {
            destination[channel] = new ProgramChannelLevel(
                LinearToDb(_peak[channel]),
                LinearToDb(MathF.Sqrt(_meanSquare[channel])),
                _clipped[channel]);
        }
    }

    /// <summary>Convenience allocation-per-call form of <see cref="Snapshot(Span{ProgramChannelLevel})"/>
    /// for callers off the hot path (a 1 Hz diagnostics poll); prefer the span form in a UI tick.</summary>
    public ProgramChannelLevel[] Snapshot()
    {
        var levels = new ProgramChannelLevel[_channels];
        Snapshot(levels);
        return levels;
    }

    /// <summary>Clears the sticky clip latch on every channel.</summary>
    public void ResetClip() => Array.Clear(_clipped);

    private static float LinearToDb(float linear) =>
        linear <= SilenceLinear ? SilenceDb : 20f * MathF.Log10(linear);

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}
