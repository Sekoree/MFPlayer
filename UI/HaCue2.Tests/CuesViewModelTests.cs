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
}
