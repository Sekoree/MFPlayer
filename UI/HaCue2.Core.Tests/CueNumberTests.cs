using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Cue numbers: stored as written, ordered as numbers.
/// </summary>
/// <remarks>
/// These exist because the model held a <c>decimal</c> first, and the HaPlay projects this app has to
/// open turned out to use three-level numbers (1.1.1, 1.2.1) throughout. Every case below is one a
/// decimal or a plain string gets wrong.
/// </remarks>
public sealed class CueNumberTests
{
    [Theory]
    [InlineData("1", "2")]
    [InlineData("2", "10")]
    [InlineData("12", "12.1")]
    [InlineData("12.1", "12.2")]
    [InlineData("12.2", "12.10")]
    [InlineData("12.10", "13")]
    [InlineData("1.1.1", "1.1.2")]
    [InlineData("1.1.3", "1.2")]
    [InlineData("1.2.1", "2")]
    public void NumbersOrderTheWayAnOperatorReadsThem(string lower, string higher)
    {
        Assert.True(new CueNumber(lower) < new CueNumber(higher), $"{lower} should sort before {higher}");
        Assert.True(new CueNumber(higher) > new CueNumber(lower));
    }

    [Fact]
    public void ThreeLevelNumbersSurviveARoundTrip()
    {
        // The case a decimal could not hold at all: 1.1.1 is not a number, it is three of them.
        var project = new TestProject().Project;
        var list = project.CueLists[0];
        list.Cues.Clear();
        list.Cues.Add(new CommentCueNode { Number = "1.1.1", Label = "deep" });
        list.Cues.Add(new CommentCueNode { Number = "1.1.10", Label = "deeper" });

        var reloaded = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));

        Assert.Equal("1.1.1", reloaded.CueLists[0].Cues[0].Number.Text);
        Assert.Equal("1.1.10", reloaded.CueLists[0].Cues[1].Number.Text);
    }

    [Fact]
    public void TenAfterAPointIsNotTheSameCueAsOne()
    {
        // As decimals these were equal, so one of the two cues silently disappeared from any lookup
        // keyed by number, and the duplicate-number check reported a collision that was not one.
        Assert.NotEqual(new CueNumber("12.1"), new CueNumber("12.10"));
        Assert.True(new CueNumber("12.1") < new CueNumber("12.10"));
    }

    [Fact]
    public void ANumberIsWrittenAsAPlainJsonString()
    {
        var project = new TestProject().Project;
        project.CueLists[0].Cues[0].Number = "7.5";

        // The shape HaPlay writes, so an importer is a mapping rather than a translation.
        Assert.Contains("\"number\": \"7.5\"", HaCueProjectFile.Serialize(project));
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("1.1.1")]
    [InlineData("")]
    [InlineData("  13  ")]
    public void ValidNumbersParse(string text) => Assert.True(CueNumber.TryParse(text, out _));

    [Theory]
    [InlineData("12,5")]
    [InlineData("12.")]
    [InlineData(".5")]
    [InlineData("Q12")]
    [InlineData("12.5a")]
    [InlineData("-1")]
    public void NonsenseIsRefusedRatherThanCoerced(string text)
    {
        // Refusing keeps the field showing the last good value. A number the app quietly rewrote is one
        // the paper running order no longer matches.
        Assert.False(CueNumber.TryParse(text, out _));
    }

    [Fact]
    public void LeadingZerosAreTheSameNumber()
    {
        Assert.Equal(new CueNumber("12.5"), new CueNumber("012.05"));
        Assert.Equal("12.5", new CueNumber("012.05").Text);
    }

    [Fact]
    public void AChildIsTheParentPlusOneLevel()
    {
        Assert.Equal("12.3", new CueNumber("12").Child(3).Text);
        Assert.Equal("1.1.2", new CueNumber("1.1").Child(2).Text);
        Assert.Equal(3, new CueNumber("1.1").Child(2).Depth);
    }

    [Fact]
    public void AnUnnumberedCueSortsFirstAndIsNotADuplicate()
    {
        Assert.True(CueNumber.Empty < new CueNumber("1"));

        var project = new TestProject().Project;
        var list = project.CueLists[0];
        list.Cues.Clear();
        list.Cues.Add(new CommentCueNode { Label = "a note" });
        list.Cues.Add(new CommentCueNode { Label = "another note" });

        // Two comments with no number are two comments, not a numbering collision.
        var issues = Validation.ProjectValidator.Validate(project);
        Assert.DoesNotContain(issues, issue => issue.Message.Contains("more than one cue numbered"));
    }
}
