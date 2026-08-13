using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Reading an action cue's two boxes as a MIDI message.
/// </summary>
/// <remarks>
/// This is the half that must fail LOUDLY and EARLY. A cue that sends nothing looks exactly like a
/// desk that ignored the message, and the difference is an hour of somebody's get-in - so the status
/// pass runs this same parser on a laptop with no interface in it, and every refusal below has to say
/// what is actually wrong.
/// </remarks>
public class MidiActionTests
{
    private static MidiAction Parse(string address, string arguments = "")
    {
        Assert.Null(MidiActions.TryParse(address, arguments, out var message));
        return message;
    }

    [Fact]
    public void ItDoesNotMatterWhichBoxTheNumbersAreIn()
    {
        // The two boxes exist because OSC has an address and arguments. MIDI has neither, so insisting
        // on a split would make an operator guess which half a channel number belongs in.
        var split = Parse("cc 1 7", "100");
        var whole = Parse("cc 1 7 100");

        Assert.Equal(whole, split);
        Assert.Equal(new MidiAction(MidiActionKind.ControlChange, 1, 7, 100), whole);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("noteon")]
    [InlineData("on")]
    public void ANoteWithNoVelocityIsAFullPress(string kind)
    {
        // Which is what a cue firing a note almost always means, and the one number worth defaulting.
        var message = Parse($"{kind} 3 60");

        Assert.Equal(new MidiAction(MidiActionKind.NoteOn, 3, 60, 127), message);
    }

    [Fact]
    public void ANoteOffDefaultsToZeroVelocityRatherThanFull() =>
        Assert.Equal(new MidiAction(MidiActionKind.NoteOff, 3, 60, 0), Parse("noteoff 3 60"));

    [Fact]
    public void AProgramChangeNeedsOneFewerNumberBecauseItCarriesNoValue() =>
        Assert.Equal(new MidiAction(MidiActionKind.ProgramChange, 16, 5, 0), Parse("pc 16 5"));

    [Fact]
    public void CommasAndExtraSpacingAreReadTheSameWay() =>
        // Somebody typing "cc 1, 7, 100" has said exactly what they meant.
        Assert.Equal(Parse("cc 1 7 100"), Parse("cc 1, 7,  100"));

    [Fact]
    public void ChannelsAreOneToSixteenTheWayEveryDeskNumbersThem()
    {
        Assert.Equal(1, Parse("cc 1 7 100").Channel);
        Assert.Equal(16, Parse("cc 16 7 100").Channel);

        // The wire is 0–15 and that conversion belongs at the wire. An operator reading "channel 1"
        // off the back of a desk must be able to type 1.
        Assert.Contains("1–16", Refuse("cc 0 7 100"), StringComparison.Ordinal);
        Assert.Contains("1–16", Refuse("cc 17 7 100"), StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesOutsideTheSevenBitRangeAreRefusedRatherThanWrapped()
    {
        // 128 wrapped to 0 would be a fader slammed shut by a cue authored to open it.
        Assert.Contains("0–127", Refuse("cc 1 7 128"), StringComparison.Ordinal);
        Assert.Contains("0–127", Refuse("cc 1 128 100"), StringComparison.Ordinal);
        Assert.Contains("0–127", Refuse("cc 1 7 -1"), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingNumberSaysSoAndShowsTheSyntax()
    {
        var refusal = Refuse("cc 1");

        Assert.Contains("missing a number", refusal, StringComparison.Ordinal);
        Assert.Contains("cc <ch> <cc> <value>", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAMidiMessageNamesWhatWasTyped() =>
        Assert.Contains("“sysex”", Refuse("sysex 1 2 3"), StringComparison.Ordinal);

    [Fact]
    public void AnEmptyCueIsRefusedRatherThanSendingSomethingArbitrary() =>
        Assert.Contains("no message to send", Refuse(""), StringComparison.Ordinal);

    [Fact]
    public void AWordThatIsNotANumberIsNamed() =>
        Assert.Contains("“seven”", Refuse("cc 1 seven 100"), StringComparison.Ordinal);

    private static string Refuse(string address, string arguments = "")
    {
        var refusal = MidiActions.TryParse(address, arguments, out _);
        Assert.NotNull(refusal);
        return refusal;
    }
}
