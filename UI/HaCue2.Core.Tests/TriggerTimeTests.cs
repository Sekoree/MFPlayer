using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Reading the times a schedule or a timecode binding fires at.
/// </summary>
/// <remarks>
/// A time that will not parse is a cue that can never fire, and unlike a wire binding there is no
/// device to blame for it - so the status pass runs this and the operator learns about it while
/// authoring rather than from the cue's absence on the night.
/// </remarks>
public class TriggerTimeTests
{
    [Theory]
    [InlineData("22:30", (22 * 3600) + (30 * 60))]
    [InlineData("22:30:00", (22 * 3600) + (30 * 60))]
    [InlineData("22:30:15", (22 * 3600) + (30 * 60) + 15)]
    [InlineData("00:00", 0)]
    [InlineData("23:59:59", (23 * 3600) + (59 * 60) + 59)]
    public void ATimeOfDayIsSecondsSinceMidnight(string input, double expected) =>
        Assert.Equal(expected, TriggerTimes.Schedule(input));

    [Theory]
    [InlineData("24:00")]     // a day has 24 hours numbered 0..23
    [InlineData("22:60")]
    [InlineData("22:30:60")]
    [InlineData("22")]        // one field is a number, not a time
    [InlineData("22:30:00:00")] // four fields is a TIMECODE, and must not read as a wall clock
    [InlineData("half ten")]
    [InlineData("")]
    [InlineData("-1:00")]
    public void SomethingThatIsNotATimeOfDayIsRefused(string input) =>
        Assert.Null(TriggerTimes.Schedule(input));

    [Fact]
    public void ATimecodeLabelCountsItsFramesInTheStreamsOwnRate()
    {
        // The rate comes from the sender, not the document: 01:00:00:12 at 25 fps and the same label
        // at 30 fps name different instants, and only the stream knows which one is arriving.
        Assert.Equal(0.5, TriggerTimes.Timecode("00:00:00:12", 24)!.Value, 3);
        Assert.Equal(0.4, TriggerTimes.Timecode("00:00:00:12", 30)!.Value, 3);
        Assert.Equal(
            (1 * 3600) + (12 * 60) + 44 + (7 / 25d),
            TriggerTimes.Timecode("01:12:44:07", 25)!.Value,
            3);
    }

    [Theory]
    [InlineData("01:12:44")]      // three fields is a wall clock, and must not read as timecode
    [InlineData("01:12:44:25")]   // frame 25 does not exist at 25 fps
    [InlineData("24:00:00:00")]
    [InlineData("01:12:44:07:00")]
    public void SomethingThatIsNotATimecodeIsRefused(string input) =>
        Assert.Null(TriggerTimes.Timecode(input, 25));

    [Fact]
    public void AStreamWithNoRateCannotPlaceALabel() =>
        // Zero would divide the frame field by nothing. Refusing beats a position of infinity.
        Assert.Null(TriggerTimes.Timecode("01:12:44:07", 0));

    [Fact]
    public void TheAuthorTimeCheckAcceptsThirtyFpsLabels()
    {
        // Checked against the HIGHEST common rate, so authoring against a 30 fps deck is accepted on a
        // machine where nobody has yet plugged in the stream that would have said so.
        Assert.Null(TriggerTimes.Refuse(TriggerInputKind.Timecode, "01:12:44:29"));
        Assert.NotNull(TriggerTimes.Refuse(TriggerInputKind.Timecode, "01:12:44:30"));
    }

    [Fact]
    public void AWireSourceHasNoTimeToGetWrong()
    {
        // "note 3 ch 1" is not a time and must never be judged as one.
        Assert.Null(TriggerTimes.Refuse(TriggerInputKind.MidiIn, "note 3 ch 1"));
        Assert.Null(TriggerTimes.Refuse(TriggerInputKind.OscIn, "/hacue/go"));
        Assert.Null(TriggerTimes.Refuse(TriggerInputKind.Keyboard, "Space"));
    }

    [Fact]
    public void ARefusalNamesWhatWasTypedAndWhatWasWanted()
    {
        var wrong = TriggerTimes.Refuse(TriggerInputKind.Schedule, "22:3o");

        Assert.NotNull(wrong);
        Assert.Contains("22:3o", wrong, StringComparison.Ordinal);
        Assert.Contains("22:30", wrong, StringComparison.Ordinal);
    }
}
