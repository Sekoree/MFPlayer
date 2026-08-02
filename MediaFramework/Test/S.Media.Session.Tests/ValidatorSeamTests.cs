using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Document validation split along the engine/cue seam: <see cref="CueListValidator"/> owns cue-list
/// invariants, <see cref="ShowDocumentValidator"/> owns everything a clip/composition/route host needs, and
/// composing them reports exactly what one pass used to.
/// </summary>
public sealed class ValidatorSeamTests
{
    private static ShowDocument Doc(
        IReadOnlyList<CueDefinition>? cues = null,
        IReadOnlyList<ShowClipBinding>? clips = null,
        IReadOnlyList<OutputPatchRoute>? routes = null) => new(
        Version: 1,
        Cues: cues ?? [],
        Clips: clips ?? [],
        Compositions: [],
        Routes: routes ?? []);

    [Fact]
    public void ARouteOnACueFreeDocument_Validates()
    {
        // The bug the seam exposed: a route's SourceId is matched against a CLIP id at play time
        // (ResolveOutputChannelMap), but validation checked it against CUE ids. That only ever worked
        // because the two were the same string - with no cues, a perfectly good route was rejected.
        var doc = Doc(
            clips: [new ShowClipBinding("stinger", "/a.wav")],
            routes: [new OutputPatchRoute("stinger", ShowSession.MasterOutputId)]);

        Assert.Empty(ShowDocumentValidator.Validate(doc));
    }

    [Fact]
    public void ARouteNamingNoClip_IsStillRejected()
    {
        var doc = Doc(
            clips: [new ShowClipBinding("stinger", "/a.wav")],
            routes: [new OutputPatchRoute("typo", ShowSession.MasterOutputId)]);

        Assert.Contains(
            ShowDocumentValidator.Validate(doc),
            e => e.Message.Contains("typo", StringComparison.Ordinal) && e.Message.Contains("clip", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledRoute_IsNotChecked()
    {
        var doc = Doc(routes: [new OutputPatchRoute("nothing", "nowhere", Enabled: false)]);

        Assert.Empty(ShowDocumentValidator.Validate(doc));
    }

    [Fact]
    public void TheCueValidatorRunsStandalone()
    {
        var errors = CueListValidator.Validate(
        [
            new CueDefinition("a", 1, "A"),
            new CueDefinition("a", 2, "B"),  // duplicate id
            new CueDefinition("c", 1, "C"),  // duplicate number
        ]);

        Assert.Contains(errors, e => e.Message.Contains("duplicate cue id", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("duplicate cue number", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyCueList_IsValid()
    {
        // An engine host with no cues runs none of the cue rules, and that is not a degenerate case.
        Assert.Empty(CueListValidator.Validate([]));
    }

    [Fact]
    public void CueProblemsStillSurfaceThroughTheDocumentValidator()
    {
        // The split must be invisible to callers: one pass, one error list.
        var doc = Doc(cues: [new CueDefinition("a", 1, "A"), new CueDefinition("a", 2, "B")]);

        Assert.Contains(
            ShowDocumentValidator.Validate(doc),
            e => e.Message.Contains("duplicate cue id", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownFollowOn_IsACueRule()
    {
        var cues = new[] { new CueDefinition("a", 1, "A") with { FollowOnCueId = "ghost" } };

        Assert.Contains(
            CueListValidator.Validate(cues),
            e => e.Message.Contains("ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAutoContinueCycle_IsStillCaught()
    {
        // The one rule with real teeth: a cycle here auto-continues forever at show time.
        var cues = new[]
        {
            new CueDefinition("a", 1, "A") with { FollowOnCueId = "b", AutoContinue = true },
            new CueDefinition("b", 2, "B") with { FollowOnCueId = "a", AutoContinue = true },
        };

        Assert.NotEmpty(CueListValidator.Validate(cues));
    }

    [Fact]
    public void AnIssueCarriesItsSubject_SoAHostCanNavigateToIt()
    {
        var doc = Doc(
            clips: [new ShowClipBinding("stinger", "/a.wav")],
            routes: [new OutputPatchRoute("typo", ShowSession.MasterOutputId)]);

        var issue = Assert.Single(ShowDocumentValidator.Validate(doc));

        // Parsing the id back out of the sentence works until someone rewords the sentence.
        Assert.Equal("route", issue.SubjectKind);
        Assert.Equal("typo", issue.SubjectId);
    }

    [Fact]
    public void AClipIssueIsAttributedToItsClip()
    {
        var doc = Doc(clips: [new ShowClipBinding("stinger", "   ")]);

        var issue = Assert.Single(ShowDocumentValidator.Validate(doc));

        Assert.Equal("clip", issue.SubjectKind);
        Assert.Equal("stinger", issue.SubjectId);
        Assert.Equal(ShowValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void AnEmptyCueLabel_WarnsRatherThanBlockingTheShow()
    {
        var doc = Doc(cues: [new CueDefinition("a", 1, "")]);

        var issue = Assert.Single(ShowDocumentValidator.Validate(doc));

        // Deliberate behaviour change: this used to refuse to open the show. A missing label is a cosmetic
        // gap in the operator's list and cannot affect playback.
        Assert.Equal(ShowValidationSeverity.Warning, issue.Severity);
        Assert.Equal("cue", issue.SubjectKind);
        ShowDocumentValidator.ThrowIfInvalid(doc); // must not throw
    }

    [Fact]
    public void ADocumentWithBothAWarningAndAnError_StillThrows()
    {
        var doc = Doc(cues: [new CueDefinition("a", 1, ""), new CueDefinition("a", 2, "B")]);

        var thrown = Assert.Throws<ShowDocumentValidationException>(() => ShowDocumentValidator.ThrowIfInvalid(doc));

        // The exception carries the blocking problems only - a warning in that list would read as a cause.
        Assert.All(thrown.Errors, e => Assert.Equal(ShowValidationSeverity.Error, e.Severity));
        Assert.Contains(thrown.Errors, e => e.Message.Contains("duplicate cue id", StringComparison.Ordinal));
    }

    [Fact]
    public void AWholeDocumentRule_HasNoSubject()
    {
        var doc = Doc() with { Version = ShowDocumentValidator.CurrentVersion + 1 };

        var issue = Assert.Single(ShowDocumentValidator.Validate(doc));

        Assert.Null(issue.SubjectKind);
    }

    [Fact]
    public void AnUnknownStopTarget_IsACueRule()
    {
        var cues = new[] { new CueDefinition("a", 1, "A") with { StopTargetIds = ["ghost"] } };

        Assert.Contains(
            CueListValidator.Validate(cues),
            e => e.Message.Contains("stop-target", StringComparison.Ordinal));
    }
}
