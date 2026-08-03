using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// What a recording is called (register item 30).
/// </summary>
public class RecordPatternTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 8, 3, 14, 30, 5, TimeSpan.Zero);

    private static string Expand(string pattern, string project = "Hamlet", string list = "Act One", int attempt = 0) =>
        RecordPattern.Expand(pattern, new RecordPattern.RecordNaming(project, list, Moment, attempt));

    [Fact]
    public void EveryTokenExpands()
    {
        Assert.Equal("2026-08-03", Expand("{date}"));
        Assert.Equal("143005", Expand("{time}"));
        Assert.Equal("Hamlet", Expand("{project}"));
        Assert.Equal("Act One", Expand("{list}"));
        Assert.Equal("1", Expand("{n}"));
    }

    [Fact]
    public void EveryAdvertisedTokenIsOneThatWorks()
    {
        // The dropdown and the expander cannot drift: a token offered by the insert menu that expanded
        // to itself would put a literal "{takes}" in the operator's filenames.
        foreach (var token in RecordPattern.Tokens)
            Assert.DoesNotContain(token.Token, Expand(token.Token), StringComparison.Ordinal);
    }

    [Fact]
    public void TokensAreCaseInsensitive()
    {
        // Somebody typing {Date} meant {date}, and a literal "{Date}" in the filename would be a
        // baffling thing to have to discover.
        Assert.Equal("2026-08-03", Expand("{Date}"));
        Assert.Equal("2026-08-03", Expand("{DATE}"));
    }

    [Fact]
    public void TheCounterIsOneBased()
    {
        Assert.Equal("show-1", Expand("show-{n}", attempt: 0));
        Assert.Equal("show-2", Expand("show-{n}", attempt: 1));
    }

    [Fact]
    public void TokensCombineWithLiteralText()
    {
        Assert.Equal("Hamlet-2026-08-03-1", Expand(RecordPattern.Default));
        Assert.Equal("show 2026-08-03 at 143005", Expand("show {date} at {time}"));
    }

    [Fact]
    public void AnUnknownTokenIsLeftStanding()
    {
        // Visible rather than dropped: a typo that vanished would name every recording the same thing
        // and leave the operator with no clue why.
        Assert.Equal("show-{bogus}", Expand("show-{bogus}"));
    }

    [Fact]
    public void AnUnclosedBraceIsLiteral()
    {
        Assert.Equal("show-{date", Expand("show-{date"));
    }

    [Fact]
    public void AnEmptyPatternFallsBackToTheDefault()
    {
        Assert.Equal(Expand(RecordPattern.Default), Expand(""));
        Assert.Equal(Expand(RecordPattern.Default), Expand("   "));
    }

    // ── a pattern becomes a path ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/etc/shadow")]
    public void ASubstitutedValueCannotEscapeTheRecordingFolder(string hostile)
    {
        var name = Expand("{project}", project: hostile);

        // The expansion is joined onto the recording directory, so a separator surviving it would write
        // outside the folder the operator chose. Odd name, right folder.
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public void APatternItselfCannotEscapeTheRecordingFolder()
    {
        var name = Expand("../../escape");

        Assert.DoesNotContain('/', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public void CharactersOtherPlatformsRefuseAreStripped()
    {
        // Stripped on Linux too: a show file travels, and a recording named "act:one?" written here
        // would fail to open on the Windows machine it is carried to.
        var name = Expand("{project}", project: "act:one?<two>|three\"four*");

        foreach (var forbidden in ":?<>|\"*")
            Assert.DoesNotContain(forbidden, name);
    }

    [Fact]
    public void ALeadingDotIsRemoved()
    {
        // A recording that hid itself on Unix would be reported as "it did not record".
        Assert.False(Expand(".hidden").StartsWith('.'));
    }

    [Fact]
    public void ANameThatCleansAwayEntirelyStillGetsOne()
    {
        // Better a file called "recording" than an empty filename, which is not a path at all.
        Assert.Equal("recording", Expand("{project}", project: "///"));
        Assert.Equal("recording", Expand("..."));
    }

    [Fact]
    public void TheHelpExampleIsRenderedByTheSameCodeAsTheFilename()
    {
        // The popover shows what Expand returns rather than a hand-written imitation, so the example
        // and the file cannot disagree.
        Assert.Equal(
            RecordPattern.Expand(
                "{project}-{list}", new RecordPattern.RecordNaming("Hamlet", "Act One", Moment)),
            RecordPattern.Example("{project}-{list}", "Hamlet", Moment));
    }
}
