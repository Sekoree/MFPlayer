using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Reading and writing a trim window's times.
/// </summary>
/// <remarks>
/// The case this exists for is "trim thirty minutes off the front and ten off the end of a recording".
/// Before it, that was <c>1800.0</c> and <c>length − 600</c> typed into two boxes, against a length the
/// app never showed. Every rule below is about that sentence being typeable as it is said.
/// </remarks>
public class ClipTimeTests
{
    [Theory]
    [InlineData("1800", 1_800_000)]        // plain seconds still work
    [InlineData("30:00", 1_800_000)]
    [InlineData("2.5", 2_500)]
    [InlineData("1:05:30.250", 3_930_250)]
    [InlineData("0:02", 2_000)]
    [InlineData("90", 90_000)]             // a leading field carries past 59
    public void ATimeIsReadHoweverItIsWritten(string text, int expected) =>
        Assert.Equal(expected, ClipTimes.Parse(text));

    [Fact]
    public void AMinusCountsBackFromTheEnd()
    {
        var length = TimeSpan.FromMinutes(120);

        // "ten minutes off the end" said the way it is meant, rather than as 7200 − 600.
        Assert.Equal(6_600_000, ClipTimes.Parse("-10:00", length));
        Assert.Equal(6_600_000, ClipTimes.Parse("−10:00", length));
    }

    [Fact]
    public void AFromTheEndTimeIsRefusedWhenNothingHasProbedTheFile() =>
        // Resolving it against an assumed length would put the out-point somewhere nobody chose, and
        // the cue would end early on a file nobody had looked at.
        Assert.Null(ClipTimes.Parse("-10:00"));

    [Fact]
    public void MoreThanTenMinutesOffTheEndOfAShortFileLandsAtZeroRatherThanBelowIt() =>
        Assert.Equal(0, ClipTimes.Parse("-10:00", TimeSpan.FromMinutes(2)));

    [Theory]
    [InlineData("1:90")]        // ninety seconds is not a seconds field
    [InlineData("1:2:3:4")]
    [InlineData("half past")]
    [InlineData("")]
    [InlineData("1.5:30")]      // only the last field may be fractional
    [InlineData("-")]
    public void SomethingThatIsNotATimeIsRefused(string text) =>
        Assert.Null(ClipTimes.Parse(text, TimeSpan.FromHours(1)));

    [Theory]
    [InlineData(2_250, "2.250")]
    [InlineData(2_000, "2")]
    [InlineData(90_000, "1:30")]
    [InlineData(1_800_000, "30:00")]
    [InlineData(3_930_250, "1:05:30.250")]
    [InlineData(0, "0")]
    public void ATimeIsWrittenAsTheShortestSensibleClockReading(int milliseconds, string expected) =>
        // Hours only when there are hours, thousandths only when they are not zero: a sting reads
        // "2.250" and a concert reads "1:05:30", instead of both carrying the other's noise.
        Assert.Equal(expected, ClipTimes.Format(milliseconds));

    [Fact]
    public void WhatIsWrittenReadsBackTheSame()
    {
        // The field round-trips through itself on every focus change, so a value that drifted would
        // walk a trim point every time somebody clicked away from it.
        foreach (var milliseconds in new[] { 0, 1, 999, 1_000, 59_999, 60_000, 1_800_000, 3_930_250 })
            Assert.Equal(milliseconds, ClipTimes.Parse(ClipTimes.Format(milliseconds)));
    }
}
