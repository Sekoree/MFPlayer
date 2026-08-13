using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Renumbering, which is the one edit that can silently rewrite an operator's paper running order.
/// </summary>
/// <remarks>
/// The insert path and the Renumber dialog used to be two implementations of this, and they disagreed:
/// the dialog carried a group's number down to its children and the insert path assigned bare integers
/// at every depth. These tests pin the shared rule so they cannot drift apart again.
/// </remarks>
public sealed class CueRenumberTests
{
    private static CueList DottedList() => new()
    {
        Name = "Act one",
        Cues =
        [
            new GroupCueNode
            {
                Number = "1",
                Label = "Scene one",
                Children =
                [
                    new CommentCueNode { Number = "1.1", Label = "a" },
                    new CommentCueNode { Number = "1.2", Label = "b" },
                    new GroupCueNode
                    {
                        Number = "1.3",
                        Label = "beat",
                        Children = [new CommentCueNode { Number = "1.3.1", Label = "c" }],
                    },
                ],
            },
            new CommentCueNode { Number = "2", Label = "after" },
        ],
    };

    private static string Numbers(CueList list) =>
        string.Join(" ", list.Flatten().Select(cue => cue.Number.Text));

    [Fact]
    public void ChildrenAreNumberedUnderTheirGroup()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });

        CueRenumber.Apply(journal, list.Cues);

        // The whole point: a group's children hang off ITS number. Bare integers here would collide
        // with the top level - two cues answering to Q1 and two to Q2.
        Assert.Equal("1 1.1 1.2 1.3 1.3.1 2", Numbers(list));
    }

    [Fact]
    public void RenumberingOnlyAGroupsChildrenKeepsThePrefix()
    {
        var list = DottedList();
        var group = (GroupCueNode)list.Cues[0];
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });

        // What auto-renumber-on-insert does: one level, under whatever owns it.
        group.Children.Insert(1, new CommentCueNode { Label = "new" });
        CueRenumber.Apply(journal, group.Children, group.Number);

        Assert.Equal("1 1.1 1.2 1.3 1.4 1.4.1 2", Numbers(list));
    }

    [Fact]
    public void StartAndStepApplyToTheTopLevelOnly()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });

        CueRenumber.Apply(journal, list.Cues, start: 10, step: 10);

        // "Start at 10, step 10" means 10 and 20 with 10.1, 10.2 inside - not 10.10, 10.20. The step
        // belongs to the level the operator asked about.
        Assert.Equal("10 10.1 10.2 10.3 10.3.1 20", Numbers(list));
    }

    [Fact]
    public void ACueAlreadyReadingCorrectlyIsNotRewritten()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });

        CueRenumber.Apply(journal, list.Cues);

        // Every number already reads that way, so there is nothing to undo - a renumber that rewrote
        // each cue would mark a clean document dirty for changing nothing.
        Assert.False(journal.CanUndo);
    }

    [Fact]
    public void AQuietCompositeReportsOnceRatherThanPerCommand()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });
        var changes = 0;
        journal.Changed += () => changes++;

        using (journal.Composite("renumber", "cues", quiet: true))
            CueRenumber.Apply(journal, list.Cues, start: 5, step: 5);

        // An observer of this journal can be expensive - the shell re-runs the whole project status
        // pass on every change - so a batch that reported per cue cost that pass per cue.
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnOrdinaryCompositeStillReportsEveryStep()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });
        var changes = 0;
        journal.Changed += () => changes++;

        using (journal.Composite("renumber", "cues"))
            CueRenumber.Apply(journal, list.Cues, start: 5, step: 5);

        // Silence is OPT-IN. A continuous gesture can be wrapped in a composite too - a patch-gain
        // drag, a layer move - and those want the views following the pointer.
        Assert.True(changes > 1, $"an ordinary composite reported {changes} time(s)");
    }

    [Fact]
    public void AQuietCompositeIsStillOneUndoStep()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });
        var before = Numbers(list);

        using (journal.Composite("renumber", "cues", quiet: true))
            CueRenumber.Apply(journal, list.Cues, start: 5, step: 5);

        journal.Undo();

        Assert.Equal(before, Numbers(list));
    }

    [Fact]
    public void TheWholeRunIsOneUndoStep()
    {
        var list = DottedList();
        var journal = new ProjectJournal(new HaCueProject { CueLists = [list] });
        var before = Numbers(list);

        using (journal.Composite("renumber", "cues"))
            CueRenumber.Apply(journal, list.Cues, start: 5, step: 5);

        Assert.NotEqual(before, Numbers(list));

        journal.Undo();

        Assert.Equal(before, Numbers(list));
    }
}
