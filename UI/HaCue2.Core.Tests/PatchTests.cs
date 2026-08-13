using HaCue2.Core.Journal;
using HaCue2.Core.Validation;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class PatchTests
{
    /// <summary>The two matrices multiplied: source → logical → device, with the gains composed.</summary>
    [Fact]
    public void AnEffectiveRouteComposesTheSendGainWithTheCellGain()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainR.Id).GainDb = -6;

        var route = Assert.Single(
            PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track, sourceChannel: 1));

        // send −3 dB then cell −6 dB: decibels add because the linear gains multiply.
        Assert.Equal(-9, route.GainDb);
        Assert.Equal("Main R", route.LogicalName);
        Assert.Equal(1, route.LineChannel);
    }

    [Fact]
    public void OneLogicalOutputCanFeedSeveralLinesAndChannels()
    {
        var fixture = new TestProject();
        var ndi = new AudioLineDefinition { Name = "NDI Prog", Channels = 2 };
        fixture.Project.AudioLines.Add(ndi);
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.MainL.Id, LineId = ndi.Id, LineChannel = 0, GainDb = -6,
        });

        var routes = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track, sourceChannel: 0);

        Assert.Equal(2, routes.Count);
        Assert.Contains(routes, route => route.LineName == "18i20" && route.GainDb == 0);
        Assert.Contains(routes, route => route.LineName == "NDI Prog" && route.GainDb == -6);
    }

    /// <summary>
    /// Several logical outputs summing into one device channel is legal and is NOT normalized -
    /// summing is the operator's decision, and meters make the result visible.
    /// </summary>
    [Fact]
    public void SeveralLogicalOutputsMaySumIntoOneDeviceChannel()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.FoldL.Id, LineId = fixture.Interface.Id, LineChannel = 0, GainDb = -6,
        });
        fixture.Track.Sends.Add(new CueAudioSend { SourceChannel = 0, LogicalChannelId = fixture.FoldL.Id });

        var landing = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track)
            .Where(route => route.LineId == fixture.Interface.Id && route.LineChannel == 0)
            .ToList();

        Assert.Equal(2, landing.Count);
        Assert.Contains(landing, route => route.LogicalName == "Main L");
        Assert.Contains(landing, route => route.LogicalName == "Fold L");
    }

    [Fact]
    public void MutingEitherStageSilencesTheRoute()
    {
        var fixture = new TestProject();
        fixture.Track.Sends[0].Muted = true;
        fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainR.Id).Muted = true;

        var routes = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track);

        Assert.All(routes, route => Assert.True(route.Muted));
        Assert.All(routes, route => Assert.False(route.IsAudible));
    }

    [Fact]
    public void AtTheSilenceFloorARouteIsNotAudible()
    {
        var fixture = new TestProject();
        fixture.Track.Sends[0].GainDb = PatchOperations.SilenceFloorDb;

        var route = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track, sourceChannel: 0)[0];

        Assert.False(route.IsAudible);
    }

    [Fact]
    public void AnAbsentLineKeepsItsCellsAndReportsTheLineAsAbsent()
    {
        var fixture = new TestProject();
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.MainL.Id, LineId = Guid.NewGuid(), LineChannel = 0,
        });

        var routes = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track, sourceChannel: 0);

        // The cell survives; it just cannot say which device it means on this machine.
        Assert.Contains(routes, route => route.LineName == "(absent)");
    }

    /// <summary>Reordering a logical output must never retarget a cue: ids bind, positions do not.</summary>
    [Fact]
    public void ReorderingAndRenamingLeaveEveryRouteAlone()
    {
        var fixture = new TestProject();
        var before = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track)
            .Select(route => (route.SourceChannel, route.LineChannel, route.GainDb))
            .OrderBy(route => route.SourceChannel).ThenBy(route => route.LineChannel)
            .ToList();

        fixture.Project.AudioPatch.LogicalChannels.Reverse();
        fixture.MainL.Name = "Front L";

        var after = PatchOperations.EffectiveRoutes(fixture.Project, fixture.Track)
            .Select(route => (route.SourceChannel, route.LineChannel, route.GainDb))
            .OrderBy(route => route.SourceChannel).ThenBy(route => route.LineChannel)
            .ToList();

        Assert.Equal(before, after);
    }

    // ── snapshots ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARecallOnlyTouchesTheCellsTheSnapshotStores()
    {
        var fixture = new TestProject();
        var mainBefore = fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainL.Id).GainDb;

        var result = PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);

        Assert.True(result.IsClean);
        Assert.Equal(2, result.CellsApplied);
        // Fold L/R were stored at 0 dB and the live patch had them at −3.
        Assert.Equal(0, fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.FoldL.Id).GainDb);
        // Main L was not in the snapshot, so it keeps its value.
        Assert.Equal(mainBefore, fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainL.Id).GainDb);
    }

    [Fact]
    public void RecallingTwiceLandsOnTheSameState()
    {
        var fixture = new TestProject();

        PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);
        var once = Snapshotted(fixture);
        PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);

        Assert.Equal(once, Snapshotted(fixture));
    }

    [Fact]
    public void TwoSnapshotsOwningDifferentCellsDoNotUndoEachOther()
    {
        var fixture = new TestProject();
        var lobby = new PatchSnapshot
        {
            Name = "Interval",
            Cells =
            [
                new PatchCell
                {
                    LogicalChannelId = fixture.MainL.Id, LineId = fixture.Interface.Id,
                    LineChannel = 0, GainDb = -10,
                },
            ],
        };
        fixture.Project.PatchSnapshots.Add(lobby);

        PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);
        PatchOperations.Recall(fixture.Project, lobby.Id);

        Assert.Equal(-10, fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainL.Id).GainDb);
        Assert.Equal(0, fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.FoldL.Id).GainDb);
    }

    /// <summary>
    /// A cell naming a deleted channel is reported and skipped - never slid onto a neighbouring
    /// channel, which is how a recall ends up feeding the wrong speaker.
    /// </summary>
    [Fact]
    public void ABrokenSnapshotCellIsReportedAndAppliesNothing()
    {
        var fixture = new TestProject();
        fixture.Snapshot.Cells.Add(new PatchCell
        {
            LogicalChannelId = Guid.NewGuid(), LineId = fixture.Interface.Id, LineChannel = 4, GainDb = 0,
        });
        var cellCount = fixture.Project.AudioPatch.Cells.Count;

        var result = PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);

        var broken = Assert.Single(result.Broken);
        Assert.Contains("logical output", broken.Reason);
        Assert.Equal(cellCount, fixture.Project.AudioPatch.Cells.Count);
        // The rest of the snapshot still applied: one bad cell must not cost the operator the recall.
        Assert.Equal(2, result.CellsApplied);
    }

    [Fact]
    public void ACellPastTheEndOfItsLineIsReported()
    {
        var fixture = new TestProject();
        fixture.Snapshot.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.MainL.Id, LineId = fixture.Wedge.Id, LineChannel = 7,
        });

        var result = PatchOperations.Recall(fixture.Project, fixture.Snapshot.Id);

        Assert.Contains(result.Broken, broken => broken.Reason.Contains("only 2 channels"));
    }

    [Fact]
    public void ACapturedSnapshotDoesNotFollowLaterPatchEdits()
    {
        var fixture = new TestProject();
        var captured = PatchOperations.Capture(fixture.Project, [fixture.MainL.Id]);

        fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainL.Id).GainDb = -20;

        Assert.Equal(0, captured[0].GainDb);
    }

    // ── inline patch-cue level changes ───────────────────────────────────────────────────────────

    [Fact]
    public void ALevelChangeWithNoLineTouchesEveryCellFedByThatOutput()
    {
        var fixture = new TestProject();
        var ndi = new AudioLineDefinition { Name = "NDI Prog", Channels = 2 };
        fixture.Project.AudioLines.Add(ndi);
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.FoldL.Id, LineId = ndi.Id, LineChannel = 0,
        });

        var result = PatchOperations.ApplyLevels(fixture.Project,
            [new PatchLevelChange { LogicalChannelId = fixture.FoldL.Id, GainDb = -12 }]);

        Assert.Equal(2, result.CellsApplied);
        Assert.All(
            fixture.Project.AudioPatch.Cells.Where(cell => cell.LogicalChannelId == fixture.FoldL.Id),
            cell => Assert.Equal(-12, cell.GainDb));
    }

    [Fact]
    public void ALevelChangeThatMatchesNoCellIsReportedRatherThanCountedAsDone()
    {
        var fixture = new TestProject();

        var result = PatchOperations.ApplyLevels(fixture.Project,
            [new PatchLevelChange { LogicalChannelId = fixture.MainL.Id, LineId = fixture.Wedge.Id }]);

        Assert.Equal(0, result.CellsApplied);
        Assert.Contains(result.Broken, broken => broken.Reason.Contains("no patch cell"));
    }

    // ── output groups (register item 9) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Linked editing moves the group by a DELTA, so a deliberate L/R trim survives a nudge.
    /// </summary>
    [Fact]
    public void NudgingOneGroupMemberMovesTheOthersByTheSameDelta()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        // Give the pair an existing imbalance worth preserving.
        fixture.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == fixture.MainR.Id).GainDb = -2;

        ProjectEdits.NudgeGroupGain(journal, fixture.MainL.Id, fixture.Interface.Id, 0, -6);

        Assert.Equal(-6, Gain(fixture, fixture.MainL.Id, 0));
        Assert.Equal(-8, Gain(fixture, fixture.MainR.Id, 1));
        // One gesture, one undo step.
        Assert.Single(journal.Log);

        journal.Undo();
        Assert.Equal(0, Gain(fixture, fixture.MainL.Id, 0));
        Assert.Equal(-2, Gain(fixture, fixture.MainR.Id, 1));
    }

    [Fact]
    public void AnUngroupedChannelNudgesAlone()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        ProjectEdits.NudgeGroupGain(journal, fixture.FoldL.Id, fixture.Interface.Id, 2, -3);

        Assert.Equal(-6, Gain(fixture, fixture.FoldL.Id, 2));
        Assert.Equal(-3, Gain(fixture, fixture.FoldR.Id, 3));
    }

    private static double Gain(TestProject fixture, Guid channelId, int lineChannel) =>
        fixture.Project.AudioPatch.Cells
            .First(cell => cell.Matches(channelId, fixture.Interface.Id, lineChannel)).GainDb;

    private static string Snapshotted(TestProject fixture) =>
        string.Join(
            "|",
            fixture.Project.AudioPatch.Cells
                .OrderBy(cell => cell.LogicalChannelId).ThenBy(cell => cell.LineChannel)
                .Select(cell => $"{cell.LogicalChannelId}:{cell.LineChannel}:{cell.GainDb}:{cell.Muted}"));

    // ── deleting an audio line ────────────────────────────────────────────────────────────────

    [Fact]
    public void DeletingALineTakesEverythingThatPointedAtItInOneStep()
    {
        var show = new TestProject();
        var journal = new ProjectJournal(show.Project);
        var line = show.Interface;

        show.Project.Audition.AudioLineId = line.Id;
        var cells = show.Project.AudioPatch.Cells.Count(cell => cell.LineId == line.Id);
        Assert.True(cells > 0);
        Assert.Equal(line.Id, show.Project.AudioPatch.ClockMasterLineId);

        ProjectEdits.DeleteAudioLine(journal, line.Id);

        // Every one of these fails differently if left behind: a cell on a missing line routes
        // nothing, a snapshot recalls a cell that cannot land, and a clock master or audition rig
        // pointing at nothing takes the rig with it.
        Assert.DoesNotContain(show.Project.AudioLines, item => item.Id == line.Id);
        Assert.DoesNotContain(show.Project.AudioPatch.Cells, cell => cell.LineId == line.Id);
        Assert.DoesNotContain(show.Snapshot.Cells, cell => cell.LineId == line.Id);
        Assert.Null(show.Project.AudioPatch.ClockMasterLineId);
        Assert.Null(show.Project.Audition.AudioLineId);
    }

    [Fact]
    public void UndoingADeleteBringsTheLineAndItsPatchBack()
    {
        var show = new TestProject();
        var journal = new ProjectJournal(show.Project);
        var before = show.Project.AudioPatch.Cells.Count;

        ProjectEdits.DeleteAudioLine(journal, show.Interface.Id);
        journal.Undo();

        // ONE step for the whole cascade. Several would mean an operator pressing undo once still had
        // a patch missing the cells that belonged to the line they just restored.
        Assert.Contains(show.Project.AudioLines, item => item.Id == show.Interface.Id);
        Assert.Equal(before, show.Project.AudioPatch.Cells.Count);
        Assert.Equal(show.Interface.Id, show.Project.AudioPatch.ClockMasterLineId);
    }

    [Fact]
    public void DeletingALineNothingUsesStillWorks()
    {
        var show = new TestProject();
        var journal = new ProjectJournal(show.Project);

        ProjectEdits.DeleteAudioLine(journal, show.Wedge.Id);

        Assert.DoesNotContain(show.Project.AudioLines, item => item.Id == show.Wedge.Id);
    }

    [Fact]
    public void DeletingALineThatIsAlreadyGoneDoesNothing()
    {
        var show = new TestProject();
        var journal = new ProjectJournal(show.Project);

        ProjectEdits.DeleteAudioLine(journal, Guid.NewGuid());

        // No command, so no undo entry - a stale row double-clicked must not push an empty step onto
        // the stack and make the next undo do nothing visible.
        Assert.False(journal.CanUndo);
        Assert.Equal(2, show.Project.AudioLines.Count);
    }

    [Fact]
    public void TheConfirmationCountsWhatWillActuallyGo()
    {
        var show = new TestProject();
        show.Project.Audition.AudioLineId = show.Interface.Id;

        var references = ProjectReferences.To(
            show.Project, ProjectReferences.AudioLine, show.Interface.Id);

        var described = string.Join(" · ", references.Select(item => item.Description));

        // Counted from the document rather than described in general terms: "removes 4 patch cells" is
        // something an operator can weigh, and "may affect the patch" is not.
        Assert.Contains("the patch uses 4 of its channels", described, StringComparison.Ordinal);
        Assert.Contains("snapshot “Act 1” stores a cell on it", described, StringComparison.Ordinal);
        Assert.Contains("it is the clock master", described, StringComparison.Ordinal);
        Assert.Contains("the audition rig monitors through it", described, StringComparison.Ordinal);
    }
}

