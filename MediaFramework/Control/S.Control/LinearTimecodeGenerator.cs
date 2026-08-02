namespace S.Control;

/// <summary>
/// Generates LTC (SMPTE linear timecode) audio: pull samples out, get a signal
/// <see cref="LinearTimecodeDecoder"/> - or any other machine on the planet - can chase.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pull, not push.</b> <see cref="Render"/> fills whatever span it is handed and remembers where it
/// stopped, so an audio callback asking for 480 samples out of a 1600-sample frame gets exactly that and
/// the next call resumes mid-cell. That is the whole reason this is a class with state rather than a
/// static "make me a frame" helper: LTC frames do not divide into audio buffers, and a generator that
/// restarted at each buffer boundary would drop the mandatory cell-start transition there and corrupt a
/// bit per buffer.
/// </para>
/// <para>
/// <b>No clock of its own.</b> Time advances by exactly one frame per frame's worth of samples emitted,
/// so the timecode is paced by whatever consumes the audio. Feed it to a device and the device's clock
/// paces it; feed it to a file writer and it renders faster than real time with identical output. This
/// is the same discipline the decoder follows and it is what makes both testable without a timer.
/// </para>
/// <para>
/// <b>Drift.</b> A frame is <c>sampleRate / fps</c> samples, which is fractional for 29.97 (1601.6 at
/// 48 kHz). The remainder carries into the next frame rather than being rounded away, so an hour of
/// output ends on the frame it should rather than some tens of frames early.
/// </para>
/// <para>Not thread-safe: one generator per output stream.</para>
/// </remarks>
public sealed class LinearTimecodeGenerator
{
    /// <summary>Half-cells in one frame: 80 bits, each of which can transition at its midpoint.</summary>
    private const int HalfCellsPerFrame = LinearTimecodeDecoder.FrameBits * 2;

    private const ushort SyncWord = 0xBFFC;

    private readonly int _sampleRate;

    // Frame length as an EXACT rational: a frame is _den/_num samples. Doubles cannot hold 1601.6, and
    // carrying an inexact remainder frame by frame drifts just enough that the hundredth boundary lands
    // at 1601.5999999 and never fires. Integer arithmetic makes every boundary exact forever.
    private readonly long _num;
    private readonly long _den;

    /// <summary>Level of each half-cell of the current frame, +1 or -1. Rebuilt once per frame.</summary>
    private readonly sbyte[] _levels = new sbyte[HalfCellsPerFrame];
    private readonly bool[] _bits = new bool[LinearTimecodeDecoder.FrameBits];

    /// <summary>Samples emitted since the last seek. The whole position is derived from this.</summary>
    private long _sampleIndex;

    /// <summary>Frame number at <see cref="_sampleIndex"/> == 0.</summary>
    private long _baseFrame;

    /// <summary>Whole frames completed since the last seek, and the sub-frame remainder in _den units.</summary>
    private long _framesElapsed;
    private long _remainder;

    /// <summary>Carrier level at the end of the last half-cell emitted, so frames join seamlessly.</summary>
    private sbyte _runningLevel = 1;

    private MidiTimecodeValue _current;

    /// <param name="sampleRate">Sample rate of the audio being rendered into.</param>
    /// <param name="start">Timecode of the first frame emitted.</param>
    /// <remarks>
    /// A frame must be long enough that its 160 half-cells are each at least a sample - below roughly
    /// 8 kHz the signal cannot represent LTC at all, and silently emitting a waveform nothing can decode
    /// is worse than refusing.
    /// </remarks>
    public LinearTimecodeGenerator(int sampleRate, MidiTimecodeValue start)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "sample rate must be positive");
        if (!start.IsValid)
            throw new ArgumentException("the starting timecode is not a valid label for its rate", nameof(start));

        var (num, den) = FrameRatio(start.Rate);
        _num = num;
        _den = den * sampleRate;

        var frameSamples = (double)_den / _num;
        if (frameSamples < HalfCellsPerFrame)
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                $"{sampleRate} Hz gives {frameSamples:0.##} samples per {MidiTimecodeRates.Label(start.Rate)} " +
                $"frame; LTC needs at least {HalfCellsPerFrame} to carry its half-cells.");

        _sampleRate = sampleRate;
        _current = start;
        _baseFrame = start.FrameNumber;
        BuildFrame();
    }

    /// <summary>Peak level of the square wave. Real-world LTC is fed hot; this leaves headroom.</summary>
    public float Amplitude { get; set; } = 0.8f;

    /// <summary>The timecode of the frame currently being rendered.</summary>
    public MidiTimecodeValue Current => _current;

    /// <summary>The frame rate this generator was built for.</summary>
    public MidiTimecodeRate Rate => _current.Rate;

    /// <summary>Samples in one LTC frame at this generator's rate (fractional for 29.97).</summary>
    public double SamplesPerFrame => (double)_den / _num;

    /// <summary>
    /// Jumps to <paramref name="value"/>, restarting at a frame boundary.
    /// </summary>
    /// <remarks>
    /// The partially-rendered frame is abandoned rather than completed. A chaser sees one malformed
    /// frame and re-locks on the next sync word, which is exactly what it does after any splice - the
    /// alternative, finishing a frame that names the old position, would hand it a good frame carrying
    /// a timecode that is no longer true.
    /// </remarks>
    public void Seek(MidiTimecodeValue value)
    {
        if (!value.IsValid)
            throw new ArgumentException("the seek target is not a valid timecode label for its rate", nameof(value));
        if (value.Rate != _current.Rate)
            throw new ArgumentException(
                $"generator is running at {MidiTimecodeRates.Label(_current.Rate)}; " +
                $"cannot seek to a {MidiTimecodeRates.Label(value.Rate)} position.", nameof(value));

        _current = value;
        _sampleIndex = 0;
        _framesElapsed = 0;
        _remainder = 0;
        _baseFrame = value.FrameNumber;
        BuildFrame();
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the next samples of the LTC signal, advancing the
    /// timecode as whole frames are completed.
    /// </summary>
    /// <returns>The number of samples written, always <c>destination.Length</c>.</returns>
    public int Render(Span<float> destination)
    {
        var high = Amplitude;

        for (var i = 0; i < destination.Length; i++)
        {
            // Which half-cell this sample falls in. Deriving it from the position (rather than counting
            // edges) is what keeps cell boundaries exact at fractional frame lengths.
            var half = (int)(HalfCellsPerFrame * _remainder / _den);
            destination[i] = _levels[half] * high;

            _sampleIndex++;
            var scaled = _sampleIndex * _num;
            var frames = scaled / _den;
            _remainder = scaled - (frames * _den);

            if (frames != _framesElapsed)
            {
                _framesElapsed = frames;
                _runningLevel = _levels[HalfCellsPerFrame - 1];
                _current = MidiTimecodeValue.FromFrameNumber(_baseFrame + frames, _current.Rate);
                BuildFrame();
            }
        }

        return destination.Length;
    }

    /// <summary>Renders <paramref name="frameCount"/> whole frames into a new buffer. Tests and file writers.</summary>
    public float[] RenderFrames(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);

        // Render up to the exact sample index at which frame (elapsed + frameCount) begins, counting from
        // wherever we are now - so this really does add frameCount boundaries even mid-frame.
        var targetFrame = _framesElapsed + frameCount;
        var targetIndex = ((targetFrame * _den) + _num - 1) / _num; // ceil
        var buffer = new float[Math.Max(0, targetIndex - _sampleIndex)];
        Render(buffer);
        return buffer;
    }

    /// <summary>Lays out the 80 wire bits of <see cref="_current"/> and turns them into half-cell levels.</summary>
    private void BuildFrame()
    {
        Array.Clear(_bits);

        PutBcd(_bits, 0, _current.Frames, tensBits: 2);
        PutBcd(_bits, 16, _current.Seconds, tensBits: 3);
        PutBcd(_bits, 32, _current.Minutes, tensBits: 3);
        PutBcd(_bits, 48, _current.Hours, tensBits: 2);
        _bits[10] = _current.Rate == MidiTimecodeRate.Fps2997Drop;

        // Sync word LSB-first: frame bit 64 carries sync bit 0. The decoder's rolling window reproduces
        // exactly this order, and getting it backwards is the classic way to build a stream that looks
        // right on a scope and never locks.
        for (var i = 0; i < 16; i++)
            _bits[64 + i] = ((SyncWord >> i) & 1) != 0;

        // Biphase mark: every cell begins with a transition, a `1` adds one at its midpoint. So a `0`
        // holds one level across both its half-cells and a `1` splits them.
        var level = _runningLevel;
        for (var bit = 0; bit < LinearTimecodeDecoder.FrameBits; bit++)
        {
            level = (sbyte)-level;
            _levels[bit * 2] = level;
            if (_bits[bit])
                level = (sbyte)-level;
            _levels[bit * 2 + 1] = level;
        }
    }

    private static void PutBcd(bool[] bits, int offset, int value, int tensBits)
    {
        var units = value % 10;
        for (var i = 0; i < 4; i++)
            bits[offset + i] = (units & (1 << i)) != 0;

        var tens = value / 10;
        for (var i = 0; i < tensBits; i++)
            bits[offset + 8 + i] = (tens & (1 << i)) != 0;
    }

    /// <summary>Sample rate this generator renders at.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Exact frames-per-second as num/den. 29.97 is 30000/1001, not a rounded decimal.</summary>
    private static (long Num, long Den) FrameRatio(MidiTimecodeRate rate) => rate switch
    {
        MidiTimecodeRate.Fps24 => (24, 1),
        MidiTimecodeRate.Fps25 => (25, 1),
        MidiTimecodeRate.Fps2997Drop => (30_000, 1_001),
        MidiTimecodeRate.Fps30 => (30, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, "unknown timecode rate"),
    };
}
