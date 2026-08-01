using S.Control;
using Xunit;

namespace S.Control.Tests;

/// <summary>
/// LTC (SMPTE linear timecode) decoding. Every test drives the decoder with a synthesised LTC waveform
/// built by <see cref="LtcSignal"/> below, so the whole thing is deterministic - no audio device, no
/// timing, no fixtures.
/// </summary>
public class LinearTimecodeDecoderTests
{
    private const int SampleRate = 48_000;

    /// <summary>
    /// Generates real LTC audio: 80 bits per frame in biphase mark encoding, terminated by the sync word.
    /// This is the encoder the decoder is the inverse of, written independently from the bit layout in the
    /// spec so the pair does not share a mistake.
    /// </summary>
    private static class LtcSignal
    {
        public static float[] Frames(
            MidiTimecodeValue start, int frameCount, int sampleRate, double fps, float amplitude = 1f,
            bool invert = false)
        {
            // Concatenate every frame's bits and render ONCE. Rendering frame-by-frame would restart the
            // sample position (overwriting frame 1 repeatedly) and reset the carrier polarity at each
            // boundary, which drops the mandatory cell-start transition there and corrupts a bit per frame.
            var bits = new List<bool>();
            var value = start;
            for (var i = 0; i < frameCount; i++)
            {
                bits.AddRange(Encode(value));
                value = MidiTimecodeValue.FromFrameNumber(value.FrameNumber + 1, value.Rate);
            }

            var samples = new List<float>();
            Append(samples, [.. bits], sampleRate, fps, amplitude, invert);
            return [.. samples];
        }

        /// <summary>The 80 wire bits of one frame, LSB-first, sync word last.</summary>
        private static bool[] Encode(MidiTimecodeValue v)
        {
            var bits = new bool[LinearTimecodeDecoder.FrameBits];

            void PutBcd(int offset, int value, int digitBits)
            {
                for (var i = 0; i < 4; i++)
                    bits[offset + i] = ((value % 10) & (1 << i)) != 0;
                var tens = value / 10;
                for (var i = 0; i < digitBits; i++)
                    bits[offset + 8 + i] = (tens & (1 << i)) != 0;
            }

            PutBcd(0, v.Frames, 2);
            PutBcd(16, v.Seconds, 3);
            PutBcd(32, v.Minutes, 3);
            PutBcd(48, v.Hours, 2);
            bits[10] = v.Rate == MidiTimecodeRate.Fps2997Drop; // drop-frame flag

            // Sync word, emitted LSB-first: frame bit 64 carries sync bit 0, matching the wire order
            // the decoder's window reproduces.
            const ushort sync = 0xBFFC;
            for (var i = 0; i < 16; i++)
                bits[64 + i] = ((sync >> i) & 1) != 0;

            return bits;
        }

        private static void Append(
            List<float> samples, bool[] bits, int sampleRate, double fps, float amplitude, bool invert)
        {
            var cellSamples = sampleRate / fps / LinearTimecodeDecoder.FrameBits;
            var level = invert ? -amplitude : amplitude;
            var position = 0d;

            foreach (var bit in bits)
            {
                // Biphase mark: every cell starts with a transition; a `1` adds one at the midpoint.
                level = -level;
                var cellEnd = position + cellSamples;
                if (bit)
                {
                    var mid = position + cellSamples / 2;
                    Fill(samples, position, mid, level);
                    level = -level;
                    Fill(samples, mid, cellEnd, level);
                }
                else
                {
                    Fill(samples, position, cellEnd, level);
                }
                position = cellEnd;
            }
        }

        private static void Fill(List<float> samples, double from, double to, float level)
        {
            var start = (int)Math.Round(from);
            var end = (int)Math.Round(to);
            for (var i = start; i < end; i++)
            {
                while (samples.Count <= i)
                    samples.Add(0f);
                samples[i] = level;
            }
        }
    }

    private static List<MidiTimecodeValue> Decode(float[] audio, int sampleRate = SampleRate)
    {
        var decoder = new LinearTimecodeDecoder(sampleRate);
        var decoded = new List<MidiTimecodeValue>();
        decoder.Feed(audio, decoded.Add);
        return decoded;
    }

    [Fact]
    public void DecodesAContinuousRun_InOrder()
    {
        var start = new MidiTimecodeValue(10, 20, 30, 4, MidiTimecodeRate.Fps25);
        var audio = LtcSignal.Frames(start, frameCount: 6, SampleRate, fps: 25);

        var decoded = Decode(audio);

        // The first sync word only establishes the frame boundary, so decoding starts from the second.
        Assert.NotEmpty(decoded);
        Assert.Equal(new MidiTimecodeValue(10, 20, 30, 5, MidiTimecodeRate.Fps25), decoded[0]);
        for (var i = 1; i < decoded.Count; i++)
            Assert.Equal(decoded[i - 1].FrameNumber + 1, decoded[i].FrameNumber);
    }

    [Theory]
    [InlineData(24, MidiTimecodeRate.Fps24)]
    [InlineData(25, MidiTimecodeRate.Fps25)]
    [InlineData(30, MidiTimecodeRate.Fps30)]
    public void InfersTheFrameRate_FromFrameDuration(double fps, MidiTimecodeRate expected)
    {
        var start = new MidiTimecodeValue(1, 2, 3, 4, expected);
        var audio = LtcSignal.Frames(start, frameCount: 5, SampleRate, fps);

        var decoded = Decode(audio);

        Assert.NotEmpty(decoded);
        Assert.All(decoded, v => Assert.Equal(expected, v.Rate));
    }

    [Fact]
    public void DropFrameFlag_SelectsTwentyNineNineSeven_WhichTimingAloneCannotDistinguish()
    {
        // 29.97 drop-frame runs at almost exactly 30 fps on the wire, so only the flag separates them.
        var start = new MidiTimecodeValue(1, 0, 0, 2, MidiTimecodeRate.Fps2997Drop);
        var audio = LtcSignal.Frames(start, frameCount: 5, SampleRate, fps: 30000d / 1001d);

        var decoded = Decode(audio);

        Assert.NotEmpty(decoded);
        Assert.All(decoded, v => Assert.Equal(MidiTimecodeRate.Fps2997Drop, v.Rate));
    }

    [Fact]
    public void IsPolarityIndependent()
    {
        var start = new MidiTimecodeValue(3, 4, 5, 6, MidiTimecodeRate.Fps25);
        var normal = Decode(LtcSignal.Frames(start, 5, SampleRate, 25));
        var inverted = Decode(LtcSignal.Frames(start, 5, SampleRate, 25, invert: true));

        // A swapped cable, or a deck that records inverted, must decode identically - the decoder reads
        // transition intervals, not levels.
        Assert.NotEmpty(normal);
        Assert.Equal(normal, inverted);
    }

    [Fact]
    public void IsAmplitudeIndependent()
    {
        var start = new MidiTimecodeValue(3, 4, 5, 6, MidiTimecodeRate.Fps25);
        var hot = Decode(LtcSignal.Frames(start, 5, SampleRate, 25, amplitude: 1f));
        var quiet = Decode(LtcSignal.Frames(start, 5, SampleRate, 25, amplitude: 0.02f));

        // LTC arrives at wildly varying levels in practice; a level-dependent decoder would be useless.
        Assert.NotEmpty(hot);
        Assert.Equal(hot, quiet);
    }

    [Fact]
    public void LocksMidStream_WithoutKnowingWhereAFrameBegan()
    {
        var start = new MidiTimecodeValue(7, 8, 9, 10, MidiTimecodeRate.Fps25);
        var audio = LtcSignal.Frames(start, frameCount: 6, SampleRate, fps: 25);

        // Start reading part-way through a frame, as any real capture does.
        var midStream = audio.AsSpan(audio.Length / 3).ToArray();
        var decoded = Decode(midStream);

        // The sync word cannot occur in payload, so lock is achievable blind.
        Assert.NotEmpty(decoded);
        Assert.All(decoded, v => Assert.True(v.IsValid));
    }

    [Fact]
    public void Silence_DecodesNothing_AndDropsLock()
    {
        var decoder = new LinearTimecodeDecoder(SampleRate);
        var decoded = new List<MidiTimecodeValue>();

        var start = new MidiTimecodeValue(1, 1, 1, 1, MidiTimecodeRate.Fps25);
        decoder.Feed(LtcSignal.Frames(start, 4, SampleRate, 25), decoded.Add);
        Assert.NotEmpty(decoded);

        // The edge into silence flushes at most one trailing frame: an interval decoder cannot know a bit
        // has ended until the NEXT transition, so the stream's final bit is necessarily carried by the
        // first sample that follows it. That is inherent, not a defect - so the contract is that silence
        // produces no ONGOING output, which the second feed below pins.
        decoder.Feed(new float[64], decoded.Add);
        var afterBoundary = decoded.Count;

        decoder.Feed(new float[SampleRate / 2], decoded.Add); // half a second of nothing

        Assert.Equal(afterBoundary, decoded.Count);
        Assert.False(decoder.IsLocked, "lock survived a silent stretch");
    }

    [Fact]
    public void Reset_DropsLockAndPartialState()
    {
        var decoder = new LinearTimecodeDecoder(SampleRate);
        var decoded = new List<MidiTimecodeValue>();
        var start = new MidiTimecodeValue(2, 2, 2, 2, MidiTimecodeRate.Fps25);
        decoder.Feed(LtcSignal.Frames(start, 4, SampleRate, 25), decoded.Add);
        Assert.True(decoder.IsLocked);

        decoder.Reset();

        Assert.False(decoder.IsLocked);
    }

    [Fact]
    public void DecodedFrames_DriveTheExistingChaseClock()
    {
        // The point of decoding to MidiTimecodeValue: LTC reuses the chase machinery (stall timeouts,
        // free-run extrapolation, generation counting) rather than duplicating it per transport.
        var ticks = 0L;
        var clock = new MidiTimecodeChaseClock(() => ticks, ticksPerSecond: 1000);
        var start = new MidiTimecodeValue(0, 1, 0, 0, MidiTimecodeRate.Fps25);

        foreach (var value in Decode(LtcSignal.Frames(start, 5, SampleRate, 25)))
        {
            clock.FeedFrame(value);
            ticks += 40; // one 25 fps frame
        }

        var state = clock.Read();
        Assert.True(state.HasSignal);
        Assert.True(state.IsChasing);
        Assert.Equal(MidiTimecodeRate.Fps25, state.Rate);
    }
}
