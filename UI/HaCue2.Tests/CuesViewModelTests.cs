using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The cue view's own behaviour: the tree, the scope navigator, and the transport's editor half.
/// </summary>
/// <remarks>
/// These are the surfaces where a defect is invisible until somebody is driving a show — a navigator
/// that has gone stale, a panel that stopped updating, a STOP that takes the wrong thing down.
/// </remarks>
public class CuesViewModelTests
{
    [Fact]
    public Task AddingAGroupUpdatesTheScopeNavigator() => ShellFixture.WithShell(shell =>
    {
        var before = shell.Cues.Groups.Count;

        shell.Cues.AddCue(CueKind.Group);

        // The navigator was built once in the constructor and never again, so a group added at 20:05
        // did not appear until the app was restarted.
        Assert.Equal(before + 1, shell.Cues.Groups.Count);
    });

    [Fact]
    public Task RemovingAGroupUpdatesTheScopeNavigator() => ShellFixture.WithShell(shell =>
    {
        var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
        ShellFixture.Select(shell.Cues, group.Id);

        var before = shell.Cues.Groups.Count;
        shell.Cues.RemoveSelected();

        Assert.Equal(before - 1, shell.Cues.Groups.Count);
    });

    [Fact]
    public Task ScopingToADeletedGroupFallsBackToItsListRatherThanNothing() =>
        ShellFixture.WithShell(shell =>
        {
            var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
            shell.Cues.SelectedScope = shell.Cues.Groups.First(scope => scope.Id == group.Id);

            ShellFixture.Select(shell.Cues, group.Id);
            shell.Cues.RemoveSelected();

            // Dropping the operator at "no scope" mid-edit would lose their place for no reason.
            Assert.NotNull(shell.Cues.SelectedScope);
            Assert.True(shell.Cues.SelectedScope!.IsList);
        });

    [Fact]
    public Task TheGroupsHeaderNamesTheListInScope() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Name == "Video");

        // It was the literal "GROUPS IN ACT 1" — a heading from a show that only ever existed in the
        // mockup, shown over whatever the operator had actually scoped to.
        Assert.Equal("GROUPS IN VIDEO", shell.Cues.GroupsHeader);
    });

    [Fact]
    public Task GoStepsOverAGroupRatherThanIntoIt() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Music");
        var group = list.Cues.OfType<GroupCueNode>().First();

        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Id == list.Id);
        list.StandbyCueId = group.Id;

        shell.Cues.Go();

        // Firing a group deals with everything inside it, so the cursor lands AFTER the group. Landing
        // on its first child would fire that child twice.
        var children = group.Children.Select(child => child.Id).ToHashSet();
        Assert.False(list.StandbyCueId is { } landed && children.Contains(landed));
    });

    [Fact]
    public Task GoWithNoStandbyFiresFromTheTop() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Music");
        list.StandbyCueId = null;

        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Id == list.Id);
        shell.Cues.Go();

        Assert.NotNull(list.StandbyCueId);
    });

    [Fact]
    public Task DisablingACueIsOneUndoableStep() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        shell.Cues.ToggleEnabled();
        Assert.False(bed.Enabled);

        shell.Undo();
        Assert.True(shell.Project.FindCue(bed.Id)!.Enabled);
    });

    [Fact]
    public Task PanicDoesNotFireOnAReleaseBeforeTheHoldCompletes() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.BeginPanic();
        Assert.True(shell.Cues.IsPanicArming);

        shell.Cues.CancelPanic();

        // A mis-click must do nothing. The label going back is what the operator sees.
        Assert.False(shell.Cues.IsPanicArming);
        Assert.Equal("PANIC", shell.Cues.PanicLabel);
    });

    [Fact]
    public Task TheActivePanelIsEmptyWithNoSession() => ShellFixture.WithShell(shell =>
    {
        // It used to show five invented rows for any project, and nothing at all for a real one. Empty
        // with no engine is the truthful answer.
        shell.Cues.Tick();
        Assert.Empty(shell.Cues.ActiveCues);
    });

    [Fact]
    public Task SelectingACueDoesNotFlipTheRightPanel() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.SelectedRightTab = CuesViewModel.ListsTab;

        ShellFixture.Select(shell.Cues, ShellFixture.Bed(shell.Project).Id);

        // Register item 7: selecting a cue never auto-flips the panel to Cue properties.
        Assert.Equal(CuesViewModel.ListsTab, shell.Cues.SelectedRightTab);
    });

    [Fact]
    public Task AddingMediaIsOneUndoStepForSeveralFiles() => ShellFixture.WithShell(shell =>
    {
        var before = shell.Project.AllCues().Count();

        shell.Cues.AddMedia(["/library/Music/a.flac", "/library/Music/b.flac", "/library/Music/c.flac"]);
        Assert.Equal(before + 3, shell.Project.AllCues().Count());

        // Three files chosen in one picker is one thing the operator did, so it is one thing to undo.
        shell.Undo();
        Assert.Equal(before, shell.Project.AllCues().Count());
    });

    [Fact]
    public Task DuplicatingACueDoesNotShareItsEditableChildren() => ShellFixture.WithShell(shell =>
    {
        var original = ShellFixture.Bed(shell.Project);
        var composition = shell.Project.Compositions.First();
        original.Subtitles = [new SubtitleSelection { Path = "captions.srt" }];
        original.EffectLanes =
        [
            new EffectLane
            {
                Kind = EffectLaneKind.Volume,
                Points = [new LanePoint(0, 1), new LanePoint(1, 0)],
            },
        ];
        original.Placements =
        [
            new LayerPlacement
            {
                CompositionId = composition.Id,
                VideoFx = [new MappingSection { Name = "crop" }],
            },
        ];
        ShellFixture.Select(shell.Cues, original.Id);

        shell.Cues.DuplicateSelected();

        var copy = Assert.Single(
            shell.Project.AllCues().OfType<MediaCueNode>(),
            cue => cue.Id != original.Id && cue.Label == original.Label);
        copy.Sends[0].GainDb = -30;
        copy.Subtitles[0].Path = "different.srt";
        copy.EffectLanes[0].Points[0] = new LanePoint(0, 0);
        copy.Placements[0].VideoFx[0].Name = "changed";

        Assert.NotEqual(copy.Sends[0].GainDb, original.Sends[0].GainDb);
        Assert.Equal("captions.srt", original.Subtitles[0].Path);
        Assert.Equal(1, original.EffectLanes[0].Points[0].Y);
        Assert.Equal("crop", original.Placements[0].VideoFx[0].Name);
        Assert.NotEqual(copy.EffectLanes[0].Id, original.EffectLanes[0].Id);
        Assert.NotEqual(copy.Placements[0].VideoFx[0].Id, original.Placements[0].VideoFx[0].Id);
    });

    [Fact]
    public Task DuplicatingAGroupRetargetsReferencesInsideTheCopy() => ShellFixture.WithShell(shell =>
    {
        var group = Assert.IsType<GroupCueNode>(shell.Cues.AddCue(CueKind.Group));
        group.Label = "Linked group";
        var child = new MediaCueNode { Number = "90.1", Label = "Inside", MediaPath = "inside.wav" };
        var jump = new JumpCueNode
        {
            Number = "90.2", Label = "Again", TargetCueIds = [child.Id],
        };
        group.Children = [child, jump];
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, group.Id);

        shell.Cues.DuplicateSelected();

        var copy = Assert.Single(
            shell.Project.AllCues().OfType<GroupCueNode>(),
            candidate => candidate.Id != group.Id && candidate.Label == group.Label);
        var copiedChild = Assert.IsType<MediaCueNode>(copy.Children[0]);
        var copiedJump = Assert.IsType<JumpCueNode>(copy.Children[1]);

        Assert.NotEqual(child.Id, copiedChild.Id);
        Assert.Equal([copiedChild.Id], copiedJump.TargetCueIds);
    });

    [Fact]
    public Task TransportListSelectorFollowsAGroupScopeAndCanSwitchLists() =>
        ShellFixture.WithShell(shell =>
        {
            var video = shell.Project.CueLists.Single(list => list.Name == "Video");
            var group = new GroupCueNode { Number = "99", Label = "Video timeline" };
            video.Cues.Add(group);
            shell.Cues.Refresh();
            shell.Cues.SelectedScope = shell.Cues.Groups.First(scope => scope.Id == group.Id);

            Assert.StartsWith("Video ·", shell.Cues.ListSelector);

            var musicScope = shell.Cues.CueLists.First(scope => scope.Name == "Music");
            shell.Cues.SelectTransportListCommand.Execute(musicScope);

            Assert.Equal(musicScope.Id, shell.Cues.SelectedScope!.Id);
            Assert.StartsWith("Music ·", shell.Cues.ListSelector);
        });

    [Fact]
    public Task TimelineOnlyOpensForASelectedTimelineGroup() => ShellFixture.WithShell(shell =>
    {
        ShellFixture.Select(shell.Cues, ShellFixture.Bed(shell.Project).Id);
        shell.Cues.OpenTimeline();
        Assert.False(shell.Cues.IsTimelineOpen);

        var group = shell.Project.AllCues().OfType<GroupCueNode>().First();
        group.FireMode = GroupFireMode.Timeline;
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, group.Id);
        shell.Cues.OpenTimeline();

        Assert.True(shell.Cues.IsTimelineOpen);
    });

    [Fact]
    public Task LockedModeBlocksAuthoringBeforeItHasSideEffects() => ShellFixture.WithShell(shell =>
    {
        var before = shell.Project.AllCues().Count();
        shell.IsLocked = true;

        Assert.False(shell.Cues.CanEditDocument);
        Assert.Null(shell.Cues.AddCue(CueKind.Media));
        shell.Cues.AddMedia(["/outside/library/file.wav"]);

        Assert.Equal(before, shell.Project.AllCues().Count());
    });

    [Fact]
    public Task EditingModeAllowsSeekingAndLockedModeKeepsTheSafetyLatch() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.SeekUnlocked = false;

        Assert.True(shell.Cues.CanSeekActive);
        Assert.Equal("SEEK ENABLED", shell.Cues.SeekLockLabel);

        shell.IsLocked = true;

        Assert.False(shell.Cues.CanSeekActive);
        Assert.Equal("SEEK LOCKED", shell.Cues.SeekLockLabel);

        shell.Cues.CanSeekActive = true;

        Assert.True(shell.Cues.SeekUnlocked);
        Assert.True(shell.Cues.CanSeekActive);
    });

    [Fact]
    public Task OpenLockedAppliesBeforeAnyAuthoringPaneIsBuilt() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var project = ShellFixture.Project();
        project.Settings.OpenLocked = true;

        var shell = new ShellViewModel(project);

        Assert.True(shell.IsLocked);
        Assert.True(shell.Journal.IsReadOnly);
        Assert.False(shell.CanEdit);
        Assert.False(shell.Cues.CanEditDocument);
        Assert.False(shell.Audio.CanAuthor);
        Assert.False(shell.Video.CanAuthor);
        Assert.False(shell.Targets.CanAuthor);
    });
}
