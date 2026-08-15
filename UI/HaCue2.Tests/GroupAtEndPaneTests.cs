using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The group pane's at-end field tells the truth (2026-08-15 nit): "loop" is the pass count's job
/// (0 = forever) and the group-level at-end never looped anything, so it is no longer offered -
/// and with an infinite pass count the field greys, because the end it governs never comes.
/// </summary>
public sealed class GroupAtEndPaneTests
{
    private static GroupCueNode Selected(ShellViewModel shell, int loopCount, AtListEnd atEnd = AtListEnd.Hold)
    {
        var list = shell.Project.CueLists.First();
        var group = new GroupCueNode
        {
            Number = "70",
            Label = "bed",
            FireMode = GroupFireMode.Playlist,
            LoopCount = loopCount,
            AtEnd = atEnd,
        };
        list.Cues.Add(group);
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, group.Id);
        return group;
    }

    [Fact]
    public Task LoopIsNotOfferedAndLegacyLoopReadsAsHold() => ShellFixture.WithShell(shell =>
    {
        _ = Selected(shell, loopCount: 2, atEnd: AtListEnd.Loop);
        var pane = shell.Cues.Inspector.GroupPane;

        Assert.Equal(["hold last", "next list"], pane.AtEndOptions);
        Assert.Equal(0, pane.AtEndIndex); // legacy Loop always behaved as Hold at group level

        pane.AtEndIndex = 1;
        Assert.Equal(AtListEnd.NextList,
            ((GroupCueNode)shell.Project.CueLists.First().Cues.Last()).AtEnd);
    });

    [Fact]
    public Task AForeverLoopingPlaylistGreysTheEndPolicy() => ShellFixture.WithShell(shell =>
    {
        _ = Selected(shell, loopCount: 0);
        var pane = shell.Cues.Inspector.GroupPane;

        Assert.False(pane.AtEndEnabled);
        Assert.Contains("forever", pane.AtEndHint);
    });

    [Fact]
    public Task AFinitePassCountKeepsTheEndPolicyLive() => ShellFixture.WithShell(shell =>
    {
        _ = Selected(shell, loopCount: 3);
        var pane = shell.Cues.Inspector.GroupPane;

        Assert.True(pane.AtEndEnabled);
        Assert.Equal("", pane.AtEndHint);
    });
}
