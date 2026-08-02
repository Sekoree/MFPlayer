using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void AHealthyProjectHasNoErrors()
    {
        var issues = ProjectValidator.Validate(new TestProject().WithFoldbackFed().Project);

        Assert.True(ProjectValidator.IsRunnable(issues));
    }

    /// <summary>
    /// The state screen 06 exists to catch, and the one HaPlay could not even express: sound that
    /// would silently vanish.
    /// </summary>
    [Fact]
    public void AFedButUnpatchedOutputIsAnError()
    {
        var fixture = new TestProject();
        var lobby = new LogicalAudioChannel { Name = "Lobby" };
        fixture.Project.AudioPatch.LogicalChannels.Add(lobby);
        fixture.Track.Sends.Add(new CueAudioSend { SourceChannel = 0, LogicalChannelId = lobby.Id });

        var issues = ProjectValidator.Validate(fixture.Project);

        var issue = Assert.Single(issues, i => i.Message.Contains("patched to nothing"));
        Assert.Equal(ShowValidationSeverity.Error, issue.Severity);
        Assert.Equal("logicalOutput", issue.SubjectKind);
        Assert.Equal(lobby.Id.ToString(), issue.SubjectId);
    }

    /// <summary>A dead channel wastes an output but loses nothing, so it is a warning.</summary>
    [Fact]
    public void APatchedButUnfedOutputIsOnlyAWarning()
    {
        var issues = ProjectValidator.Validate(new TestProject().Project);

        var issue = Assert.Single(issues, i => i.Message.Contains("no cue sends to it") && i.Message.Contains("Fold L"));
        Assert.Equal(ShowValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void DuplicateOutputNamesAreRejectedCaseInsensitively()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.LogicalChannels.Add(new LogicalAudioChannel { Name = "main l" });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("More than one logical output"));
    }

    [Fact]
    public void ACellPastTheEndOfItsLineIsAnError()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.MainL.Id, LineId = fixture.Wedge.Id, LineChannel = 5,
        });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue =>
            issue.Severity == ShowValidationSeverity.Error && issue.Message.Contains("which has 2"));
    }

    [Fact]
    public void AGainOutsideTheUsualRangeWarnsRatherThanBlocking()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Project.AudioPatch.Cells[0].GainDb = 24;

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.True(ProjectValidator.IsRunnable(issues));
        Assert.Contains(issues, issue => issue.Message.Contains("outside the usual"));
    }

    [Fact]
    public void ANonFiniteGainIsAnError()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Cells[0].GainDb = double.NaN;

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue =>
            issue.Severity == ShowValidationSeverity.Error && issue.Message.Contains("not a finite number"));
    }

    [Fact]
    public void AChannelInTwoOutputGroupsIsAnError()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Groups.Add(new OutputGroup
        {
            Name = "Also main", MemberIds = [fixture.MainL.Id],
        });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("is in both"));
    }

    [Fact]
    public void ADanglingJumpTargetIsAnError()
    {
        var fixture = new TestProject();
        fixture.List.Cues.Remove(fixture.Track);

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue =>
            issue.Severity == ShowValidationSeverity.Error && issue.Message.Contains("jumps to a cue"));
    }

    /// <summary>A jump with nowhere to go looks exactly like a cue that did not fire.</summary>
    [Fact]
    public void ATargetlessJumpIsAnError()
    {
        var fixture = new TestProject();
        fixture.Jump.TargetCueIds.Clear();

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("jump with no target"));
    }

    [Fact]
    public void ASnapshotCellNamingADeletedOutputIsAnError()
    {
        var fixture = new TestProject();
        fixture.Snapshot.Cells[0].LogicalChannelId = Guid.NewGuid();

        var issues = ProjectValidator.Validate(fixture.Project);

        var issue = Assert.Single(issues, i => i.Message.Contains("Snapshot “Act 1”"));
        Assert.Equal("snapshot", issue.SubjectKind);
        Assert.Equal(fixture.Snapshot.Id.ToString(), issue.SubjectId);
    }

    [Fact]
    public void DuplicateCueNumbersInOneListAreAnError()
    {
        var fixture = new TestProject();
        fixture.Jump.Number = fixture.Track.Number;

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("more than one cue numbered"));
    }

    /// <summary>An unlabelled cue is worth saying, but a show with one must still open.</summary>
    [Fact]
    public void AnUnlabelledCueIsOnlyAWarning()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.Label = "";

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.True(ProjectValidator.IsRunnable(issues));
        Assert.Contains(issues, issue => issue.Message.Contains("has no label"));
    }

    [Fact]
    public void AnOutboundLaneWithNoEndpointIsAnError()
    {
        var fixture = new TestProject();
        fixture.Track.EffectLanes.Add(new EffectLane { Kind = EffectLaneKind.OscRamp });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("outbound OscRamp lane with no endpoint"));
    }

    [Fact]
    public void AnOutOfOrderEffectLaneIsAnError()
    {
        var fixture = new TestProject();
        fixture.Track.EffectLanes.Add(new EffectLane
        {
            Kind = EffectLaneKind.Volume,
            Points = [new LanePoint(0, 1), new LanePoint(0.8, 0.5), new LanePoint(0.4, 0)],
        });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("out-of-order"));
    }

    [Fact]
    public void APlacementOnADeletedCompositionIsAnError()
    {
        var fixture = new TestProject();
        fixture.Track.Placement = new LayerPlacement { CompositionId = Guid.NewGuid() };

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("composition that no longer exists"));
    }

    [Fact]
    public void AClockMasterAtTheWrongRateWarns()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Interface.SampleRate = 44_100;

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue =>
            issue.Severity == ShowValidationSeverity.Warning && issue.Message.Contains("natively at the mix rate"));
    }

    [Fact]
    public void StandbyOnACueThatLeftTheListWarns()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.List.StandbyCueId = Guid.NewGuid();

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("standby on a cue"));
    }

    [Fact]
    public void EveryIssueCarriesANavigationTarget()
    {
        var fixture = new TestProject();
        fixture.Jump.TargetCueIds.Clear();

        var issues = ProjectValidator.Validate(fixture.Project);

        // "document"-scoped rules may have no id, but every issue says what KIND of thing it is about —
        // otherwise the status view has nowhere to send the operator.
        Assert.All(issues, issue => Assert.False(string.IsNullOrEmpty(issue.SubjectKind)));
    }
}
