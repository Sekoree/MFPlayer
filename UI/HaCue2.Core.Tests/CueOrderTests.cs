using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Where GO goes next.
/// </summary>
/// <remarks>
/// The cursor is the thing an operator watches all night, so its rules matter more than most: every
/// case here is one where getting it wrong means a cue fires twice, or not at all, in front of an
/// audience.
/// </remarks>
public class CueOrderTests
{
    private static CueList Show(params CueNode[] cues) => new() { Name = "Act 1", Cues = [.. cues] };

    private static MediaCueNode Media(string number) =>
        new() { Number = number, Label = number, MediaPath = $"{number}.wav" };

    private static GroupCueNode Group(string number, params CueNode[] children) =>
        new() { Number = number, Label = number, Children = [.. children] };

    [Fact]
    public void NoCursorMeansTheTopOfTheList()
    {
        var first = Media("1");
        var list = Show(first, Media("2"));

        Assert.Equal(first.Id, CueOrder.NextEnabled(list, null)?.Id);
    }

    [Fact]
    public void TheEndOfTheListIsNothing()
    {
        var last = Media("2");
        var list = Show(Media("1"), last);

        Assert.Null(CueOrder.NextEnabled(list, last.Id));
    }

    [Fact]
    public void AGroupIsSteppedOverRatherThanInto()
    {
        var group = Group("2", Media("2.1"), Media("2.2"));
        var after = Media("3");
        var list = Show(Media("1"), group, after);

        // Firing the group already dealt with everything inside it. Stepping in would fire 2.1 a second
        // time and leave the cursor inside a group that had just played.
        Assert.Equal(after.Id, CueOrder.NextEnabled(list, group.Id)?.Id);
    }

    [Fact]
    public void NestedGroupsAreSteppedOverWhole()
    {
        var inner = Group("2.2", Media("2.2.1"), Media("2.2.2"));
        var outer = Group("2", Media("2.1"), inner);
        var after = Media("3");
        var list = Show(outer, after);

        Assert.Equal(after.Id, CueOrder.NextEnabled(list, outer.Id)?.Id);
    }

    [Fact]
    public void StandbyInsideAGroupStillAdvancesWithinIt()
    {
        var second = Media("2.2");
        var group = Group("2", Media("2.1"), second);
        var list = Show(group, Media("3"));

        // Landing standby on one child of a group and firing just that one is a real thing to do, so
        // advancing from inside the group walks the group rather than jumping out of it.
        Assert.Equal(second.Id, CueOrder.NextEnabled(list, group.Children[0].Id)?.Id);
    }

    [Fact]
    public void ADisabledCueIsSteppedOver()
    {
        var skipped = Media("2");
        skipped.Enabled = false;
        var after = Media("3");
        var list = Show(Media("1"), skipped, after);

        Assert.Equal(after.Id, CueOrder.NextEnabled(list, list.Cues[0].Id)?.Id);
    }

    [Fact]
    public void ADisabledGroupTakesItsChildrenWithIt()
    {
        var group = Group("2", Media("2.1"), Media("2.2"));
        group.Enabled = false;
        var after = Media("3");
        var list = Show(Media("1"), group, after);

        // The operator switched the whole thing off. Walking into a disabled group to find an enabled
        // child would fire part of a group that is not in tonight's show.
        Assert.Equal(after.Id, CueOrder.NextEnabled(list, list.Cues[0].Id)?.Id);
    }

    [Fact]
    public void ACursorOnACueThatIsNoLongerInTheListReportsNothing()
    {
        var list = Show(Media("1"));

        // Deleted out from under the cursor. Returning the top of the list instead would silently
        // rewind the show.
        Assert.Null(CueOrder.NextEnabled(list, Guid.NewGuid()));
    }

    [Fact]
    public void EveryCueKindTakesItsTurn()
    {
        var jump = new JumpCueNode { Number = "2", Label = "jump" };
        var comment = new CommentCueNode { Number = "3", Label = "" };
        var list = Show(Media("1"), jump, comment, Media("4"));

        // Control-flow cues are cues. GO reaching one is how it ever gets to execute, so the cursor
        // has to stop on them rather than treating them as annotations.
        Assert.Equal(jump.Id, CueOrder.NextEnabled(list, list.Cues[0].Id)?.Id);
        Assert.Equal(comment.Id, CueOrder.NextEnabled(list, jump.Id)?.Id);
    }
}
