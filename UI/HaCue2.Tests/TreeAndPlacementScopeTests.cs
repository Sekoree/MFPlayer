using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Two operator-state rules the 2026-08-14 nits pinned: a folded group stays folded through the
/// refresh every edit and save performs, and the cue's placement editor draws the SELECTED cue(s),
/// not the whole show's boxes.
/// </summary>
public sealed class TreeAndPlacementScopeTests
{
    [Fact]
    public Task ACollapsedGroupStaysCollapsedThroughARefresh() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.First();
        var group = new GroupCueNode { Number = "50", Label = "Folded" };
        group.Children.Add(new CommentCueNode { Number = "50.1", Label = "inside" });
        list.Cues.Add(group);
        shell.Cues.Refresh();

        // The control writes the expander state onto the row model; a collapse is exactly this.
        shell.Cues.AllRows.First(row => row.Id == group.Id).IsExpanded = false;

        // An edit-driven refresh (typing a label, saving) replaces every row.
        shell.Cues.Refresh();
        Assert.False(shell.Cues.AllRows.First(row => row.Id == group.Id).IsExpanded);

        // And it survives scoping away and back.
        shell.Cues.SelectedScope = shell.Cues.CueLists.Last();
        shell.Cues.SelectedScope = shell.Cues.CueLists.First();
        Assert.False(shell.Cues.AllRows.First(row => row.Id == group.Id).IsExpanded);
    });

    [Fact]
    public Task DroppingACueIntoACollapsedGroupOpensIt() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.First();
        var group = new GroupCueNode { Number = "60", Label = "Target" };
        list.Cues.Add(group);
        var loose = new CommentCueNode { Number = "61", Label = "dropped" };
        list.Cues.Add(loose);
        shell.Cues.Refresh();

        shell.Cues.AllRows.First(row => row.Id == group.Id).IsExpanded = false;
        shell.Cues.Refresh();

        Assert.True(shell.Cues.MoveCues([loose.Id], group.Id, CueDrop.Inside));

        // The moved cue must be visible in its new home the moment the refresh lands.
        Assert.True(shell.Cues.AllRows.First(row => row.Id == group.Id).IsExpanded);
        Assert.Contains(shell.Cues.AllRows, row => row.Id == loose.Id);
    });

    [Fact]
    public Task ThePlacementEditorShowsOnlyTheSelectedCuesPlacements() => ShellFixture.WithShell(shell =>
    {
        var composition = shell.Project.Compositions[0];
        var music = shell.Project.CueLists.Single(l => l.Name == "Music");
        var cues = music.Flatten().OfType<MediaCueNode>().Take(2).ToList();
        Assert.Equal(2, cues.Count);
        foreach (var cue in cues)
        {
            cue.Placements.Clear();
            cue.Placements.Add(new LayerPlacement { CompositionId = composition.Id, LayerIndex = 0 });
        }

        ShellFixture.Select(shell.Cues, cues[0].Id);
        shell.Cues.Refresh();

        // One box: the OTHER cue is on the same canvas but is not being edited here.
        var shown = shell.Cues.Inspector.Video.Placements;
        Assert.All(shown, box => Assert.Equal(cues[0].Id, box.SubjectId));
        Assert.Single(shown);
    });
}
