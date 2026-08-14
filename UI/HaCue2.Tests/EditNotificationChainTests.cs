using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// That an edit always reaches everything that listens for one.
/// </summary>
/// <remarks>
/// <para>
/// This is the chain that turned one crash into a broken show. The shell's own refresh is the FIRST
/// subscriber to the journal and the engine reload is a later one, and a multicast delegate stops at
/// the first handler that throws - so a fault while rebuilding a panel stopped the engine ever being
/// told about that edit, or any edit after it. The document and the rig then diverged in silence.
/// </para>
/// <para>
/// The journal is usually driven from a binding setter, where Avalonia files the throw as a validation
/// error, so none of it produced a message either.
/// </para>
/// </remarks>
public class EditNotificationChainTests
{
    [Fact]
    public Task AnEditStillReachesLaterSubscribersAfterTheTreeShrinks() =>
        ShellFixture.WithShell(shell =>
        {
            var reached = 0;
            // Subscribed after the shell's own handlers, exactly where the engine reload sits.
            shell.Journal.Changed += () => reached++;

            var cues = shell.Cues;
            var list = cues.ScopedList!;

            // The shape that used to throw: a selection held deep in the tree, then the tree replaced
            // underneath it.
            cues.SelectedCue = cues.AllRows.Last();
            list.Cues.Clear();
            cues.Refresh();

            shell.Journal.Do(new SetValueCommand<string>(
                list.Id, "name", "cues", () => list.Name, value => list.Name = value, "Act two",
                "rename list"));

            Assert.True(reached > 0, "an edit after the tree shrank never reached the later subscribers");
        });

    [Fact]
    public Task ScopingIntoAndOutOfAGroupKeepsTheChainAlive() => ShellFixture.WithShell(shell =>
    {
        var reached = 0;
        shell.Journal.Changed += () => reached++;

        var cues = shell.Cues;
        var group = cues.Groups.First();

        cues.SelectedScope = group;
        cues.SelectedCue = cues.AllRows.Last();
        cues.SelectedScope = cues.Scopes.First(scope => scope.IsList);

        var before = reached;
        cues.AddCue(CueKind.Comment);

        Assert.True(reached > before, "an edit after a scope change never reached the later subscribers");
    });

    [Fact]
    public Task PlacingCoverArtOnACanvasNamesTheStreamAndKeepsTheChainAlive() =>
        ShellFixture.WithShell(shell =>
        {
            var reached = 0;
            shell.Journal.Changed += () => reached++;

            var cue = ShellFixture.Bed(shell.Project);
            ShellFixture.Select(shell.Cues, cue.Id);

            var composition = shell.Project.Compositions[0];
            shell.Cues.Inspector.Video.PlacementTarget =
                shell.Project.Compositions.IndexOf(composition);

            var before = reached;
            shell.Cues.Inspector.Video.PlaceOnComposition();

            // The gesture that produced the original crash report. It must land the placement AND keep
            // the notification chain intact, because the engine learns about the placement through it.
            Assert.Single(cue.Placements);
            Assert.Equal(composition.Id, cue.Placements[0].CompositionId);
            Assert.True(reached > before, "placing on a canvas never reached the later subscribers");
        });
}
