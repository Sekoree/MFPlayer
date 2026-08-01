namespace S.Control;

/// <summary>
/// Decodes LTC (SMPTE linear timecode) from an audio signal: float samples in, complete timecode frames
/// out. Pure and deterministic - no clock of its own, no timers, no allocation per sample - so it unit
/// tests against generated waveforms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why LTC is easier to chase than MTC.</b> MIDI time code dribbles one timecode across eight
/// quarter-frame messages spanning two frames, which is where most of <see cref="MidiTimecodeDecoder"/>'s
/// complexity lives. LTC carries a whole 80-bit frame per video frame, so a decoded frame is complete the
/// moment its sync word arrives. Feed the result to
/// <see cref="MidiTimecodeChaseClock.FeedFrame"/> and the existing chase machinery - stall timeouts,
/// free-run extrapolation, jump classification - applies unchanged.
/// </para>
/// <para>
/// <b>Signal format.</b> Each frame is 80 bits in biphase mark encoding: every bit cell begins with a
/// transition, and a <c>1</c> has an additional transition at the cell's midpoint. So a <c>0</c> cell is
/// one long interval and a <c>1</c> cell is two short ones. That makes the decoder polarity-independent
/// (it measures intervals between zero crossings, not levels) and largely amplitude-independent, which
/// matters because LTC is routinely recorded hot, quiet, or inverted.
/// </para>
/// <para>
/// <b>Bit layout.</b> Frame units/tens, seconds, minutes and hours occupy fixed BCD nibbles; bit 10 is
/// the drop-frame flag; bits 64..79 are the sync word <c>0011 1111 1111 1101</c>. The sync word cannot
/// occur elsewhere in the stream, which is what lets a decoder lock without knowing where a frame began.
/// </para>
/// <para>
/// <b>Rate inference.</b> The wire does not carry the frame rate, only the drop-frame flag. The rate is
/// therefore inferred from how long a frame actually took, which is why <see cref="Feed"/> needs the
/// sample rate. Drop-frame is only legal at 29.97, so the flag disambiguates the 30/29.97 pair that
/// timing alone cannot separate.
/// </para>
/// <para>Not thread-safe: give each capture stream its own decoder.</para>
/// </remarks>
public sealed class LinearTimecodeDecoder
{
    /// <summary>Bits in one LTC frame.</summary>
    public const int FrameBits = 80;

    /// <summary>The 16-bit sync word that terminates every frame, LSB-first as it appears on the wire.</summary>
    private const ushort SyncWord = 0xBFFC;

    private readonly int _sampleRate;

    // Zero-crossing interval measurement.
    private float _previousSample;
    private int _samplesSinceTransition;

    // Biphase decoding: a short interval is half a bit cell, so two shorts make one `1`.
    private bool _halfBitPending;
    private double _referenceCellSamples;

    // Rolling 80-bit window. Bits shift DOWN and the newest lands at bit 79, so after 80 bits frame bit
    // K sits at window bit K - payload is then simply the low 64 bits and the sync word the next 16,
    // both already in wire order. (Shifting the other way inverts both, which is easy to get wrong.)
    private UInt128 _window;
    private bool _locked;

    // Frame timing, for rate inference.
    private long _sampleIndex;
    private long _lastFrameSampleIndex = -1;

    public LinearTimecodeDecoder(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        _sampleRate = sampleRate;
    }

    /// <summary>True once a sync word has been seen, i.e. the decoder knows where frames end.</summary>
    public bool IsLocked => _locked;

    /// <summary>Drops bit lock and all partial state. Call when the capture stream restarts.</summary>
    public void Reset()
    {
        _previousSample = 0f;
        _samplesSinceTransition = 0;
        _halfBitPending = false;
        _referenceCellSamples = 0;
        _window = UInt128.Zero;
        _locked = false;
        _lastFrameSampleIndex = -1;
    }

    /// <summary>
    /// Decodes a block of mono samples, invoking <paramref name="onFrame"/> for each complete timecode.
    /// Interleaved capture should be de-interleaved by the caller (LTC lives on one channel).
    /// </summary>
    /// <param name="samples">Mono audio. Any amplitude; polarity does not matter.</param>
    /// <param name="onFrame">Called once per decoded frame, in order.</param>
    public void Feed(ReadOnlySpan<float> samples, Action<MidiTimecodeValue> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        foreach (var sample in samples)
        {
            _sampleIndex++;
            _samplesSinceTransition++;

            // Transitions are zero crossings, so the decode is independent of level and of polarity -
            // an inverted or quiet LTC track decodes identically.
            var crossed = (_previousSample < 0f && sample >= 0f) || (_previousSample >= 0f && sample < 0f);
            _previousSample = sample;
            if (!crossed)
            {
                // A run far longer than a bit cell means the signal stopped (or was never LTC): drop
                // lock rather than let a stale half-bit corrupt the next frame.
                if (_referenceCellSamples > 0 && _samplesSinceTransition > _referenceCellSamples * 4)
                {
                    _halfBitPending = false;
                    _locked = false;
                    _referenceCellSamples = 0;
                }
                continue;
            }

            var interval = _samplesSinceTransition;
            _samplesSinceTransition = 0;

            if (_referenceCellSamples <= 0)
            {
                // Bootstrap: assume the first interval is a whole cell. Wrong half the time, but the
                // running average corrects within a few bits and nothing is emitted before a sync word.
                _referenceCellSamples = interval;
                continue;
            }

            // Half a cell (within tolerance) ⇒ part of a `1`; otherwise a whole cell ⇒ `0`.
            var isHalf = interval < _referenceCellSamples * 0.75;
            if (isHalf)
            {
                if (!_halfBitPending)
                {
                    _halfBitPending = true;
                    continue; // wait for the cell's second half
                }
                _halfBitPending = false;
                PushBit(true, onFrame);
                // Track the cell length from confirmed bits, which follows playback speed (varispeed,
                // tape wow) without a PLL. A `1` is two half-cells, so this interval implies cell = 2×.
                _referenceCellSamples = (_referenceCellSamples * 7 + interval * 2) / 8;
            }
            else
            {
                // A long interval while half a `1` was pending means the two did not belong together -
                // discard the orphan rather than emit a bit the signal never carried.
                _halfBitPending = false;
                PushBit(false, onFrame);
                // A `0` occupies the whole cell, so the interval IS the cell estimate.
                _referenceCellSamples = (_referenceCellSamples * 7 + interval) / 8;
            }
        }
    }

    private void PushBit(bool bit, Action<MidiTimecodeValue> onFrame)
    {
        _window = (_window >> 1) | ((UInt128)(bit ? 1UL : 0UL) << (FrameBits - 1));

        // The sync word is the one 16-bit pattern that cannot occur in payload, so it is safe to test on
        // every bit rather than only on an expected boundary - which is exactly what lets the decoder
        // lock mid-stream without knowing where a frame began.
        if ((ushort)((_window >> 64) & 0xFFFF) != SyncWord)
            return;

        _locked = true;

        var frameSamples = _lastFrameSampleIndex < 0 ? 0 : _sampleIndex - _lastFrameSampleIndex;
        _lastFrameSampleIndex = _sampleIndex;
        if (frameSamples <= 0)
            return; // the first sync word only establishes the boundary; the next one has a duration

        if (TryDecode((ulong)_window, frameSamples, out var value))
            onFrame(value);
    }

    /// <summary>Reads the BCD time fields out of a complete 64-bit payload and infers the rate.</summary>
    private bool TryDecode(ulong payload, long frameSamples, out MidiTimecodeValue value)
    {
        value = default;

        var frames = (int)(payload & 0xF) + (int)((payload >> 8) & 0x3) * 10;
        var seconds = (int)((payload >> 16) & 0xF) + (int)((payload >> 24) & 0x7) * 10;
        var minutes = (int)((payload >> 32) & 0xF) + (int)((payload >> 40) & 0x7) * 10;
        var hours = (int)((payload >> 48) & 0xF) + (int)((payload >> 56) & 0x3) * 10;
        var dropFrame = ((payload >> 10) & 1) != 0;

        var rate = InferRate(frameSamples, dropFrame);
        var candidate = new MidiTimecodeValue(hours, minutes, seconds, frames, rate);
        if (!candidate.IsValid)
            return false; // a corrupt frame decodes to nonsense BCD; drop it rather than chase it

        value = candidate;
        return true;
    }

    /// <summary>
    /// Infers the frame rate from measured frame duration. The wire carries only the drop-frame flag, so
    /// 30 and 29.97 are indistinguishable by timing at any realistic tolerance - the flag separates them,
    /// since drop-frame is only defined at 29.97.
    /// </summary>
    private MidiTimecodeRate InferRate(long frameSamples, bool dropFrame)
    {
        if (dropFrame)
            return MidiTimecodeRate.Fps2997Drop;

        var fps = _sampleRate / (double)frameSamples;
        if (fps < 24.5)
            return MidiTimecodeRate.Fps24;
        if (fps < 27.5)
            return MidiTimecodeRate.Fps25;
        return MidiTimecodeRate.Fps30;
    }
}
