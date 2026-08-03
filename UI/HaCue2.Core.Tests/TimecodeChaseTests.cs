using HaCue2.Engine;
using S.Control;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The adapter between arriving MIDI records and the framework's chase clock.
/// </summary>
/// <remarks>
/// The decoding is S.Control's and is tested there. What is tested here is the part HaCue2 owns and
/// could get wrong on its own: recognising timecode on the same path bindings arrive on, keeping it
/// off that path, and forgetting it when the ports close.
/// </remarks>
public class TimecodeChaseTests
{
    /// <summary>One quarter-frame message, as the MIDI session hands it over.</summary>
    private static ControlMonitorRecord QuarterFrame(byte data) =>
        new() { RawBytes = [0xF1, data] };

    /// <summary>
    /// The eight nibbles of 01:12:44:07 at 25 fps.
    /// </summary>
    /// <remarks>
    /// Piece in the high nibble, value in the low one, in the order the spec emits them: frame LSN,
    /// frame MSN, second LSN, second MSN, minute LSN, minute MSN, hour LSN, then hour MSN with the two
    /// rate bits above it.
    /// </remarks>
    private static readonly byte[] Window =
        [0x07, 0x10, 0x2C, 0x32, 0x4C, 0x50, 0x61, 0x72];

    [Fact]
    public void ANoteIsNotTimecodeAndIsLeftForTheBindings()
    {
        var chase = new TimecodeChase();

        // Returning true here would swallow every note-on before a single binding was matched.
        Assert.False(chase.Feed(new ControlMonitorRecord { RawBytes = [0x90, 60, 100] }));
        Assert.False(chase.Read().HasSignal);
    }

    [Fact]
    public void ARecordWithNoBytesIsNotTimecode() =>
        Assert.False(new TimecodeChase().Feed(new ControlMonitorRecord()));

    [Fact]
    public void AQuarterFrameIsConsumedSoItNeverReachesABinding() =>
        // Consumed even before eight of them have assembled anything: a stream of quarter-frames
        // matched against bindings is four messages per frame hitting the no-repeat filter all night.
        Assert.True(new TimecodeChase().Feed(QuarterFrame(0x07)));

    [Fact]
    public void EightQuarterFramesAssembleAPosition()
    {
        var chase = new TimecodeChase();

        foreach (var piece in Window)
            chase.Feed(QuarterFrame(piece));

        var state = chase.Read();

        Assert.True(state.HasSignal);
        Assert.Equal(MidiTimecodeRate.Fps25, state.Rate);
        Assert.Equal(1, state.Position.Hours);
        Assert.Equal(12, state.Position.Minutes);
        Assert.Equal(44, state.Position.Seconds);
    }

    [Fact]
    public void ClosingThePortsForgetsWhereTheSenderWas()
    {
        var chase = new TimecodeChase();

        foreach (var piece in Window)
            chase.Feed(QuarterFrame(piece));

        Assert.True(chase.Read().HasSignal);

        chase.Reset();

        // Without this the transport chip would keep showing the last label a now-unplugged sender
        // reached, which reads as a live chase over a dead cable.
        Assert.False(chase.Read().HasSignal);
    }

    [Fact]
    public void SomethingThatIsNotAFullFrameLocateIsRefused()
    {
        var chase = new TimecodeChase();

        // A SysEx that is not MTC — every one of them arrives on this path, and the decoder is what
        // decides. Reporting it as consumed would hide identity replies and desk dumps from the
        // bindings and from the wire monitor.
        Assert.False(chase.Feed(new ControlMonitorRecord { RawBytes = [0xF0, 0x7E, 0x00, 0x06, 0x01, 0xF7] }));
        Assert.False(chase.Read().HasSignal);
    }
}
