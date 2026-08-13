using HaCue2.Engine;
using S.Control;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Matching an inbound message against a binding's pattern.
/// </summary>
/// <remarks>
/// The whole reason this is pure and separate from the device layer: every case here is one somebody
/// would otherwise discover with a controller in their hands during a get-in, and none of them needs
/// a MIDI port to reproduce.
/// </remarks>
public class TriggerMatchingTests
{
    private static TriggerSignal Note(int number, int channel = 1, int velocity = 127) =>
        new(Guid.Empty, IsMidi: true, "", ControlMIDIMessageType.NoteOn, channel, number, velocity);

    private static TriggerSignal NoteOff(int number, int channel = 1) =>
        new(Guid.Empty, IsMidi: true, "", ControlMIDIMessageType.NoteOff, channel, number, 0);

    private static TriggerSignal Cc(int number, int channel = 1, int value = 64) =>
        new(Guid.Empty, IsMidi: true, "", ControlMIDIMessageType.ControlChange, channel, number, value);

    private static TriggerSignal Osc(string address, double? value = null) =>
        new(Guid.Empty, IsMidi: false, address, ControlMIDIMessageType.Unknown, 0, 0, value);

    [Fact]
    public void ANoteMatchesItsOwnPattern() =>
        Assert.True(TriggerMatching.Matches(Note(3), "note 3 ch 1"));

    [Fact]
    public void AnOmittedChannelMatchesAnyChannel()
    {
        // What somebody means when they do not mention a channel. Requiring one would make the
        // commonest binding the fiddliest to write.
        Assert.True(TriggerMatching.Matches(Note(3, channel: 1), "note 3"));
        Assert.True(TriggerMatching.Matches(Note(3, channel: 9), "note 3"));
    }

    [Fact]
    public void AStatedChannelIsHonoured()
    {
        Assert.True(TriggerMatching.Matches(Note(3, channel: 1), "note 3 ch 1"));
        Assert.False(TriggerMatching.Matches(Note(3, channel: 2), "note 3 ch 1"));
    }

    [Fact]
    public void ANoteOffNeverMatchesANoteOnPattern()
    {
        // Every note produces both. A binding written as "note 3" that also fired on release would
        // fire its cue twice for one press.
        Assert.False(TriggerMatching.Matches(NoteOff(3), "note 3"));
        Assert.True(TriggerMatching.Matches(NoteOff(3), "note off 3"));
    }

    [Fact]
    public void ANoteOffPatternIsNotMatchedByANoteOn() =>
        Assert.False(TriggerMatching.Matches(Note(3), "note off 3"));

    [Fact]
    public void TheWrongNumberDoesNotMatch() =>
        Assert.False(TriggerMatching.Matches(Note(4), "note 3"));

    [Theory]
    [InlineData("cc 7 ch 1")]
    [InlineData("cc 7")]
    [InlineData("control 7")]
    public void AControlChangeMatchesItsSpellings(string pattern) =>
        Assert.True(TriggerMatching.Matches(Cc(7), pattern));

    [Fact]
    public void AControlChangeIsNotANote()
    {
        Assert.False(TriggerMatching.Matches(Cc(7), "note 7"));
        Assert.False(TriggerMatching.Matches(Note(7), "cc 7"));
    }

    [Fact]
    public void PatternsAreCaseInsensitiveAndForgiveWhitespace()
    {
        Assert.True(TriggerMatching.Matches(Note(3), "  NOTE 3 CH 1  "));
        Assert.True(TriggerMatching.Matches(Cc(7), "CC 7"));
    }

    [Fact]
    public void AnUnknownKindMatchesNothing()
    {
        // Refused rather than guessed: a typo'd binding that silently matched everything would fire
        // every cue on the first message that arrived.
        Assert.False(TriggerMatching.Matches(Note(3), "nte 3"));
        Assert.False(TriggerMatching.Matches(Note(3), ""));
    }

    [Fact]
    public void AnOscAddressMatchesExactly()
    {
        Assert.True(TriggerMatching.Matches(Osc("/hacue/go"), "/hacue/go"));
        Assert.False(TriggerMatching.Matches(Osc("/hacue/go/2"), "/hacue/go"));
    }

    [Fact]
    public void AnOscWildcardMatchesAPrefix()
    {
        Assert.True(TriggerMatching.Matches(Osc("/hacue/go/2"), "/hacue/go*"));
        Assert.True(TriggerMatching.Matches(Osc("/hacue/go"), "/hacue/go*"));
        Assert.False(TriggerMatching.Matches(Osc("/other"), "/hacue/go*"));
    }

    [Fact]
    public void OscAddressesAreCaseSensitive() =>
        // Because OSC addresses are. Folding case here would make HaCue2 match things the sender
        // did not send.
        Assert.False(TriggerMatching.Matches(Osc("/HaCue/Go"), "/hacue/go"));

    [Fact]
    public void AMidiSignalNeverMatchesAnOscPattern() =>
        Assert.False(TriggerMatching.Matches(Note(3), "/hacue/go"));

    // ── scaling ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AControllerScalesAcrossItsRange()
    {
        Assert.Equal(0, TriggerMatching.Scale(Cc(7, value: 0), 0, 1)!.Value, 3);
        Assert.Equal(1, TriggerMatching.Scale(Cc(7, value: 127), 0, 1)!.Value, 3);
        Assert.Equal(0.5, TriggerMatching.Scale(Cc(7, value: 64), 0, 1)!.Value, 1);
    }

    [Fact]
    public void ARangeCanBeInvertedOrOffset()
    {
        // "cc 7 rides the master trim from −60 to +12" is an ordinary thing to author.
        Assert.Equal(-60, TriggerMatching.Scale(Cc(7, value: 0), -60, 12)!.Value, 3);
        Assert.Equal(12, TriggerMatching.Scale(Cc(7, value: 127), -60, 12)!.Value, 3);
    }

    [Fact]
    public void APitchBendUsesItsOwnFullScale()
    {
        var bend = new TriggerSignal(
            Guid.Empty, IsMidi: true, "", ControlMIDIMessageType.PitchBend, 1, 0, 16_383);

        // 14-bit, not 7. Treating it as 0–127 would peg the parameter at maximum for the whole of the
        // upper 99% of the wheel's travel.
        Assert.Equal(1, TriggerMatching.Scale(bend, 0, 1)!.Value, 3);
    }

    [Fact]
    public void ASignalWithNoValueScalesToNothing() =>
        // A note-on bound to a fader-shaped parameter. Null is what lets the caller refuse rather than
        // write an arbitrary number into a master trim.
        Assert.Null(TriggerMatching.Scale(Osc("/hacue/go"), 0, 1));

    // ── reading what arrived ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOutboundRecordIsNotATrigger()
    {
        var record = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Output,
            Protocol = ControlMonitorProtocol.OSC,
            Address = "/hacue/go",
        };

        // Otherwise every binding's behaviour would depend on what the app itself had just sent.
        Assert.Null(TriggerMatching.Read(record, Guid.Empty));
    }

    [Fact]
    public void AnInboundOscRecordBecomesASignal()
    {
        var record = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.OSC,
            Address = "/hacue/go",
            OSCArguments = [new ControlMonitorOSCArgumentRecord { Kind = "f", FloatValue = 0.25 }],
        };

        var signal = TriggerMatching.Read(record, Guid.Empty);

        Assert.NotNull(signal);
        Assert.Equal("/hacue/go", signal!.Value.Address);
        Assert.Equal(0.25, signal.Value.Value!.Value, 3);
    }

    [Fact]
    public void AnInboundMidiRecordBecomesASignal()
    {
        var record = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.MIDI,
            MIDIMessageType = ControlMIDIMessageType.NoteOn,
            MIDIChannel = 1,
            MIDINote = 3,
            MIDIValue = 127,
        };

        var signal = TriggerMatching.Read(record, Guid.Empty);

        Assert.NotNull(signal);
        Assert.True(TriggerMatching.Matches(signal!.Value, "note 3 ch 1"));
    }

    [Fact]
    public void ASignalDescribesItselfTheWayABindingIsWritten()
    {
        // The monitor prints what this returns, so an operator can copy a line they can see into a
        // binding and have it work - which is learn-by-hand, before a Learn button exists.
        Assert.Equal("note 3 ch 1", Note(3).Describe());
        Assert.Equal("cc 7 ch 1", Cc(7).Describe());
        Assert.Equal("/hacue/go", Osc("/hacue/go").Describe());

        Assert.True(TriggerMatching.Matches(Note(3), Note(3).Describe()));
        Assert.True(TriggerMatching.Matches(Cc(7), Cc(7).Describe()));
    }
}
