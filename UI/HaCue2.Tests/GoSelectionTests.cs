using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// GO fires what the operator is LOOKING at (2026-08-14 nit): with click-arms-standby on, a
/// highlighted cue that differs from the armed one wins - but only a selection made since the
/// last GO, so consecutive GOs still walk the list.
/// </summary>
public sealed class GoSelectionTests
{
    [Fact]
    public Task GoFiresTheHighlightedCueWhenStandbyWasMovedElsewhere() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Music");
        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Id == list.Id);
        Assert.True(shell.Project.Settings.ClickMovesStandby);

        var highlighted = list.Cues[1];
        ShellFixture.Select(shell.Cues, highlighted.Id);

        // Standby moves elsewhere out-of-band (a remote command, or a click whose async arm has
        // not landed yet) while the operator keeps looking at their cue.
        list.StandbyCueId = list.Cues[3].Id;

        shell.Cues.Go();

        // The HIGHLIGHTED cue fired: the cursor walked on from it, not from the foreign standby.
        Assert.Equal(CueOrder.NextEnabled(list, highlighted.Id)?.Id, list.StandbyCueId);
    });

    [Fact]
    public Task ConsecutiveGosWalkTheListInsteadOfRefiringTheSelection() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Music");
        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Id == list.Id);
        shell.Cues.DoubleGoGuard = TimeSpan.Zero;

        // Cues[1], because the shell starts with the FIRST row selected - selecting it again is
        // a no-op that raises no selection event, exactly like re-clicking the selected row.
        var first = list.Cues[1];
        ShellFixture.Select(shell.Cues, first.Id);

        shell.Cues.Go();
        var afterFirst = list.StandbyCueId;
        Assert.Equal(CueOrder.NextEnabled(list, first.Id)?.Id, afterFirst);

        // The selection is still parked on the fired cue - leftover state, not a new intent.
        shell.Cues.Go();

        Assert.Equal(CueOrder.NextEnabled(list, afterFirst)?.Id, list.StandbyCueId);
    });

    [Fact]
    public Task ADisabledHighlightedCueDoesNotHijackGo() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Music");
        shell.Cues.SelectedScope = shell.Cues.CueLists.First(scope => scope.Id == list.Id);

        var disabled = list.Cues[1];
        ShellFixture.Select(shell.Cues, disabled.Id);
        disabled.Enabled = false;
        list.StandbyCueId = list.Cues[3].Id;

        shell.Cues.Go();

        // GO stepped from the real standby - a cue the show has switched off cannot be fired by
        // having been the last thing clicked.
        Assert.Equal(CueOrder.NextEnabled(list, list.Cues[3].Id)?.Id, list.StandbyCueId);
    });
}
