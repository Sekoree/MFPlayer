using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Reordering cues by dragging a row.
/// </summary>
/// <remarks>
/// The move goes through the journal, not through the tree control's built-in row reorder: the rows
/// are a projection of the document that every refresh rebuilds, so a move made in the rows alone
/// would be gone a moment later and could never be undone.
/// </remarks>
public class CueReorderTests
{
    private static CueList Music(ShellViewModel shell) =>
        shell.Project.CueLists.Single(list => list.Name == "Music");

    private static IReadOnlyList<string> Order(CueList list) => [.. list.Cues.Select(cue => cue.Label)];

    [Fact]
    public Task DroppingAfterARowMovesTheCueThere() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);
            var moving = list.Cues[1];
            var target = list.Cues[3];

            Assert.True(shell.Cues.MoveCues([moving.Id], target.Id, CueDrop.After));

            Assert.Equal(
                [before[0], before[2], before[3], before[1], .. before.Skip(4)],
                Order(list));
        });

    [Fact]
    public Task DroppingBeforeARowMovesTheCueThere() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);
            var moving = list.Cues[3];
            var target = list.Cues[1];

            Assert.True(shell.Cues.MoveCues([moving.Id], target.Id, CueDrop.Before));

            Assert.Equal(
                [before[0], before[3], before[1], before[2], .. before.Skip(4)],
                Order(list));
        });

    [Fact]
    public Task DroppingOnAGroupPutsTheCueInsideIt() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var group = list.Cues.OfType<GroupCueNode>().First();
            var moving = list.Cues[1];
            var children = group.Children.Count;

            Assert.True(shell.Cues.MoveCues([moving.Id], group.Id, CueDrop.Inside));

            Assert.DoesNotContain(moving, list.Cues);
            Assert.Equal(children + 1, group.Children.Count);
            Assert.Same(moving, group.Children[^1]);
        });

    /// <summary>
    /// The tree reports "inside" for the middle of ANY row, group or not. On a cue that cannot hold
    /// children that has to mean beside it, not a refused drop the operator sees as the drag failing.
    /// </summary>
    [Fact]
    public Task DroppingInsideACueThatIsNotAGroupLandsAfterIt() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);
            var target = list.Cues[4];

            Assert.IsNotType<GroupCueNode>(target);
            Assert.True(shell.Cues.MoveCues([list.Cues[1].Id], target.Id, CueDrop.Inside));

            Assert.Equal(
                [before[0], before[2], before[3], before[4], before[1], .. before.Skip(5)],
                Order(list));
        });

    /// <summary>A cue dragged out of a group lands beside the row it was dropped on, not back inside.</summary>
    [Fact]
    public Task ACueCanBeDraggedOutOfAGroup() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var group = list.Cues.OfType<GroupCueNode>().First();
            var moving = group.Children[0];
            var target = list.Cues[1];

            Assert.True(shell.Cues.MoveCues([moving.Id], target.Id, CueDrop.Before));

            Assert.DoesNotContain(moving, group.Children);
            Assert.Same(moving, list.Cues[1]);
        });

    [Fact]
    public Task AMoveIsOneUndoStep() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);

            Assert.True(shell.Cues.MoveCues([list.Cues[1].Id], list.Cues[3].Id, CueDrop.After));
            Assert.NotEqual(before, Order(list));

            shell.Undo();

            Assert.Equal(before, Order(list));
        });

    /// <summary>Several rows keep their order, and land as one block.</summary>
    [Fact]
    public Task DraggingSeveralCuesKeepsTheirOrder() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);
            var first = list.Cues[0];
            var second = list.Cues[1];
            var target = list.Cues[3];

            Assert.True(shell.Cues.MoveCues([second.Id, first.Id], target.Id, CueDrop.After));

            Assert.Equal(
                [before[2], before[3], before[0], before[1], .. before.Skip(4)],
                Order(list));
        });

    /// <summary>A group dropped into its own child would delete the branch it is standing on.</summary>
    [Fact]
    public Task AGroupCannotBeDroppedIntoItself() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var group = list.Cues.OfType<GroupCueNode>().First();
            var before = Order(list);

            Assert.False(shell.Cues.MoveCues([group.Id], group.Children[0].Id, CueDrop.After));
            Assert.False(shell.Cues.MoveCues([group.Id], group.Id, CueDrop.Inside));

            Assert.Equal(before, Order(list));
        });

    /// <summary>Dropping a cue back on its own edge is not an edit, so it must not become an undo step.</summary>
    [Fact]
    public Task DroppingACueWhereItAlreadyIsChangesNothing() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);

            Assert.False(shell.Cues.MoveCues([list.Cues[2].Id], list.Cues[1].Id, CueDrop.After));
            Assert.False(shell.Cues.MoveCues([list.Cues[2].Id], list.Cues[3].Id, CueDrop.Before));

            Assert.Equal(before, Order(list));
            Assert.False(shell.CanUndo);
        });

    [Fact]
    public Task TheShowCannotBeReorderedUnderLock() =>
        ShellFixture.WithShell(shell =>
        {
            var list = Music(shell);
            var before = Order(list);
            shell.IsLocked = true;

            Assert.False(shell.Cues.MoveCues([list.Cues[1].Id], list.Cues[3].Id, CueDrop.After));
            Assert.Equal(before, Order(list));
        });

    /// <summary>
    /// The tree really is set up to drag, and its drop reaches the document.
    /// </summary>
    /// <remarks>
    /// Raised rather than gestured: a real drag needs a platform drag source, which the headless
    /// backend has none of. What this covers is the half that was missing - the grid's two events
    /// being wired at all, the drop being answered rather than left to the grid's own row move, and
    /// the drop position mapping onto the right side of the target.
    /// </remarks>
    [Fact]
    public Task TheCueTreeAnswersARowDrop() =>
        ShellFixture.WithShell(shell =>
        {
            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 1_400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tree = view.GetVisualDescendants().OfType<TreeDataGrid>().Single();
            Assert.True(tree.AutoDragDropRows, "the tree is not set up to drag rows at all");

            var list = Music(shell);
            var before = Order(list);
            var moving = shell.Cues.AllRows.First(row => row.Id == list.Cues[1].Id);
            var target = shell.Cues.AllRows.First(row => row.Id == list.Cues[3].Id);

            tree.RaiseEvent(new TreeDataGridRowDragStartedEventArgs([moving]));
            tree.RaiseEvent(new TreeDataGridRowDragEventArgs(
                TreeDataGrid.RowDropEvent,
                new TreeDataGridRow { DataContext = target },
                inner: null!)
            {
                Position = TreeDataGridRowDropPosition.After,
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                [before[0], before[2], before[3], before[1], .. before.Skip(4)],
                Order(list));

            window.Close();
        });

    /// <summary>Under Lock the drag is refused before it starts, so nothing can be dropped.</summary>
    [Fact]
    public Task TheCueTreeRefusesToDragUnderLock() =>
        ShellFixture.WithShell(shell =>
        {
            shell.IsLocked = true;

            var view = new CuesView { DataContext = shell.Cues };
            var window = new Window { Width = 1_400, Height = 900, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tree = view.GetVisualDescendants().OfType<TreeDataGrid>().Single();
            Assert.False(tree.AutoDragDropRows);

            var started = new TreeDataGridRowDragStartedEventArgs([shell.Cues.AllRows.First()]);
            tree.RaiseEvent(started);

            Assert.Equal(Avalonia.Input.DragDropEffects.None, started.AllowedEffects);

            window.Close();
        });
}
