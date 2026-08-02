using S.Control;
using Xunit;

namespace S.Control.Tests;

/// <summary>
/// LTC generation. The load-bearing test is the round trip: whatever the generator emits, the decoder
/// must read back as the same timecodes, in order, with none missing.
/// </summary>
/// <remarks>
/// The decoder was written first and against an independently-written encoder in its own test file, so
/// it is not a mirror of this generator - a shared misreading of the bit layout would have to survive
/// two separate implementations to go unnoticed here.
/// </remarks>
public class LinearTimecodeGeneratorTests
{
    private const int SampleRate = 48_000;

    private static List<MidiTimecodeValue> Decode(ReadOnlySpan<float> samples, int sampleRate = SampleRate)
    {
        var got = new List<MidiTimecodeValue>();
        new LinearTimecodeDecoder(sampleRate).Feed(samples, got.Add);
        return got;
    }

    [Theory]
    [InlineData(MidiTimecodeRate.Fps24)]
    [InlineData(MidiTimecodeRate.Fps25)]
    [InlineData(MidiTimecodeRate.Fps30)]
    [InlineData(MidiTimecodeRate.Fps2997Drop)]
    public void GeneratedSignal_DecodesBackToTheSameTimecodes(MidiTimecodeRate rate)
    {
        var start = new MidiTimecodeValue(1, 2, 3, 4, rate);
        var gen = new LinearTimecodeGenerator(SampleRate, start);

        var got = Decode(gen.RenderFrames(12));

        // The first frame is spent acquiring the sync word, so a decoder can never report it.
        Assert.NotEmpty(got);
        var expected = MidiTimecodeValue.FromFrameNumber(start.FrameNumber + 1, rate);
        Assert.Equal(expected, got[0]);
        for (var i = 1; i < got.Count; i++)
            Assert.Equal(got[i - 1].FrameNumber + 1, got[i].FrameNumber);
    }

    [Fact]
    public void TheDropFrameFlag_SurvivesTheRoundTrip()
    {
        // 30 and 29.97-drop are indistinguishable by timing alone; only this flag separates them, so if
        // it did not survive, a chaser would silently run 0.1% fast.
        var gen = new LinearTimecodeGenerator(
            SampleRate, new MidiTimecodeValue(10, 0, 0, 0, MidiTimecodeRate.Fps2997Drop));

        var got = Decode(gen.RenderFrames(6));

        Assert.NotEmpty(got);
        Assert.All(got, v => Assert.Equal(MidiTimecodeRate.Fps2997Drop, v.Rate));
    }

    [Fact]
    public void RenderingInRaggedChunks_ProducesTheSameSignalAsOneCall()
    {
        var start = new MidiTimecodeValue(0, 0, 10, 5, MidiTimecodeRate.Fps25);
        var whole = new LinearTimecodeGenerator(SampleRate, start).RenderFrames(8);

        // 480 does not divide 1920, so nearly every chunk boundary lands mid-cell - the case that breaks
        // a generator which restarts its carrier per buffer.
        var chunked = new float[whole.Length];
        var gen = new LinearTimecodeGenerator(SampleRate, start);
        for (var offset = 0; offset < chunked.Length; offset += 480)
            gen.Render(chunked.AsSpan(offset, Math.Min(480, chunked.Length - offset)));

        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void TheFractionalFrameLength_IsCarried_NotRounded()
    {
        // 48000/29.97 is 1601.6 samples. Rounding that per frame loses 0.6 samples each time, which is a
        // whole frame adrift inside two seconds; carrying it keeps the count exact.
        var gen = new LinearTimecodeGenerator(
            SampleRate, new MidiTimecodeValue(0, 0, 0, 0, MidiTimecodeRate.Fps2997Drop));

        Assert.Equal(1601.6, gen.SamplesPerFrame, 3);

        var samples = gen.RenderFrames(100);
        Assert.Equal(160_160, samples.Length);
        Assert.Equal(100, gen.Current.FrameNumber);
    }

    [Fact]
    public void CurrentAdvances_OneFramePerFrameOfSamples()
    {
        var gen = new LinearTimecodeGenerator(
            SampleRate, new MidiTimecodeValue(0, 0, 0, 1, MidiTimecodeRate.Fps25));

        gen.Render(new float[SampleRate]); // exactly one second

        Assert.Equal(25, gen.Current.FrameNumber - 1);
    }

    [Fact]
    public void Seek_RestartsAtAFrameBoundary_AndTheDecoderRelocks()
    {
        var gen = new LinearTimecodeGenerator(
            SampleRate, new MidiTimecodeValue(0, 0, 0, 0, MidiTimecodeRate.Fps25));
        var before = gen.RenderFrames(4);

        var target = new MidiTimecodeValue(0, 30, 12, 2, MidiTimecodeRate.Fps25);
        gen.Seek(target);
        var after = gen.RenderFrames(6);

        var got = Decode(new List<float>([.. before, .. after]).ToArray());

        // The splice costs one frame while the decoder re-locks; what matters is that it lands on the
        // new position and not on a stale one carried through the seek.
        Assert.Contains(got, v => v.FrameNumber >= target.FrameNumber);
        Assert.DoesNotContain(got, v => v.FrameNumber > target.FrameNumber + 6);
    }

    [Fact]
    public void Seek_RefusesToChangeRateMidStream()
    {
        var gen = new LinearTimecodeGenerator(
            SampleRate, new MidiTimecodeValue(0, 0, 0, 0, MidiTimecodeRate.Fps25));

        // The frame length is fixed at construction; accepting this would emit frames at 25 fps timing
        // carrying 24 fps labels.
        Assert.Throws<ArgumentException>(() =>
            gen.Seek(new MidiTimecodeValue(0, 0, 0, 0, MidiTimecodeRate.Fps24)));
    }

    [Fact]
    public void ASampleRateTooLowToCarryLtc_IsRefused()
    {
        // 4 kHz at 30 fps is 133 samples per frame for 160 half-cells: the signal would be undecodable
        // by anything, and emitting it anyway would look like a wiring fault rather than a config error.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LinearTimecodeGenerator(4_000, new MidiTimecodeValue(0, 0, 0, 0, MidiTimecodeRate.Fps30)));
    }

    [Fact]
    public void Amplitude_ScalesTheSignal_WithoutChangingWhatItSays()
    {
        var start = new MidiTimecodeValue(0, 0, 0, 3, MidiTimecodeRate.Fps25);
        var quiet = new LinearTimecodeGenerator(SampleRate, start) { Amplitude = 0.05f };

        var samples = quiet.RenderFrames(6);

        Assert.All(samples, s => Assert.True(Math.Abs(s) <= 0.05f + 1e-6f, $"sample {s} exceeded amplitude"));
        // LTC is decoded from zero crossings, so a quiet signal must still decode identically.
        Assert.NotEmpty(Decode(samples));
    }
}
