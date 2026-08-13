using System.Diagnostics;
using Avalonia.Threading;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The authoring paths that quietly rewrite what an operator reads during a show.
/// </summary>
/// <remarks>
/// A cue number is called over comms and a standby cursor is watched all night. Both had defects that
/// only appear on a real document - dotted numbering, or a cue disabled for one performance - so the
/// fixtures here are shaped like a show rather than like a minimal case.
/// </remarks>
public class CueNumberingAndStandbyTests
{
    /// <summary>Replaces the scoped list's contents with a dotted, grouped running order.</summary>
    private static CueList Dotted(CuesViewModel cues)
    {
        var list = cues.ScopedList!;
        list.Cues.Clear();
        list.Cues.Add(new GroupCueNode
        {
            Number = "1",
            Label = "Scene one",
            Children =
            [
                new CommentCueNode { Number = "1.1", Label = "a" },
                new CommentCueNode { Number = "1.2", Label = "b" },
            ],
        });
        list.Cues.Add(new CommentCueNode { Number = "2", Label = "after" });
        cues.Refresh();
        return list;
    }

    private static string Numbers(CueList list) =>
        string.Join(" ", list.Flatten().Select(cue => cue.Number.Text));

    [Fact]
    public Task AddingInsideAGroupKeepsTheDottedNumbering() => ShellFixture.WithShell(shell =>
    {
        var list = Dotted(shell.Cues);
        var group = (GroupCueNode)list.Cues[0];

        ShellFixture.Select(shell.Cues, group.Children[1].Id);
        shell.Cues.AddCue(CueKind.Comment);

        // Auto-renumber-on-insert used to assign bare integers at every depth, so this produced
        // "1 1 2 3 2" - two cues answering to Q1 and two to Q2, inside one list.
        Assert.Equal("1 1.1 1.2 1.3 2", Numbers(list));
    });

    [Fact]
    public Task AddingInsideAGroupNeverCollidesWithTheTopLevel() => ShellFixture.WithShell(shell =>
    {
        var list = Dotted(shell.Cues);
        var group = (GroupCueNode)list.Cues[0];

        ShellFixture.Select(shell.Cues, group.Children[0].Id);
        shell.Cues.AddCue(CueKind.Comment);

        var numbers = list.Flatten()
            .Select(cue => cue.Number.Text)
            .Where(text => text.Length > 0)
            .ToList();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    });

    [Fact]
    public Task InsertingWhereTheLevelIsFullDoesNotReuseTheNumberAbove() =>
        ShellFixture.WithShell(shell =>
        {
            var list = shell.Cues.ScopedList!;
            // Auto-renumber off, so what AutoNumber chose is what the cue keeps.
            shell.Cues.Journal.Project.Settings.AutoRenumberOnInsert = false;
            list.Cues.Clear();
            list.Cues.Add(new CommentCueNode { Number = "1", Label = "a" });
            list.Cues.Add(new CommentCueNode { Number = "1.1", Label = "b" });
            shell.Cues.Refresh();

            ShellFixture.Select(shell.Cues, list.Cues[0].Id);
            var added = shell.Cues.AddCue(CueKind.Comment)!;

            // There is no room at this level - 1.1 is taken by the cue after - so the answer is a
            // level deeper. It used to hand back "1", duplicating the cue above it.
            Assert.NotEqual(list.Cues[0].Number, added.Number);
            Assert.True(list.Cues[0].Number < added.Number, $"{list.Cues[0].Number} < {added.Number}");
            Assert.True(added.Number < list.Cues[2].Number, $"{added.Number} < {list.Cues[2].Number}");
        });

    [Fact]
    public Task SteppingStandbyFromADisabledCueGoesToTheNextOneNotTheTop() =>
        ShellFixture.WithShell(shell =>
        {
            var list = shell.Cues.ScopedList!;
            var order = list.Flatten().ToList();

            // Standby can legally sit on a disabled cue: clicking one puts it there.
            order[3].Enabled = false;
            list.StandbyCueId = order[3].Id;

            shell.Cues.StepStandby(1);

            // It used to find no position in the enabled-only list and clamp to index 0 - jumping to
            // the top of the list mid-show.
            var expected = order.Skip(4).First(cue => cue.Enabled);
            Assert.Equal(expected.Id, list.StandbyCueId);
        });

    [Fact]
    public Task SteppingStandbyBackwardsFromADisabledCueGoesToThePreviousOne() =>
        ShellFixture.WithShell(shell =>
        {
            var list = shell.Cues.ScopedList!;
            var order = list.Flatten().ToList();

            order[3].Enabled = false;
            list.StandbyCueId = order[3].Id;

            shell.Cues.StepStandby(-1);

            var expected = order.Take(3).Last(cue => cue.Enabled);
            Assert.Equal(expected.Id, list.StandbyCueId);
        });

    [Fact]
    public Task StandbyHoldsAtTheEndRatherThanWrapping() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Cues.ScopedList!;
        var last = list.Flatten().Last(cue => cue.Enabled);
        list.StandbyCueId = last.Id;

        shell.Cues.StepStandby(1);

        // Running off the bottom and silently arriving back at cue one is not something an operator
        // can see happen.
        Assert.Equal(last.Id, list.StandbyCueId);
    });

    [Fact]
    public Task SteppingStandbyWithNoCursorLandsOnTheFirstEnabledCue() =>
        ShellFixture.WithShell(shell =>
        {
            var list = shell.Cues.ScopedList!;
            list.StandbyCueId = null;

            shell.Cues.StepStandby(1);

            Assert.Equal(list.Flatten().First(cue => cue.Enabled).Id, list.StandbyCueId);
        });

    [Fact]
    public Task BulkImportKeepsTheChosenOrderAndSelectsTheLast() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Cues.ScopedList!;
        shell.Cues.Journal.Project.Settings.OutsideMedia = OutsideMediaPolicy.KeepInPlace;
        list.Cues.Clear();
        shell.Cues.Refresh();

        string[] paths = ["/nowhere/a.wav", "/nowhere/b.wav", "/nowhere/c.wav"];
        shell.Cues.AddMedia(paths);

        // The run anchors each cue on the one before it. Without that - and without a refresh between
        // files to move the selection - every file would land in the same place and come out reversed.
        Assert.Equal(["a", "b", "c"], list.Cues.Select(cue => cue.Label));
        Assert.Equal(list.Cues[^1].Id, shell.Cues.SelectedCue?.Id);
    });

    [Fact]
    public Task BulkImportIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Cues.ScopedList!;
        shell.Cues.Journal.Project.Settings.OutsideMedia = OutsideMediaPolicy.KeepInPlace;
        list.Cues.Clear();
        shell.Cues.Refresh();

        shell.Cues.AddMedia(["/nowhere/a.wav", "/nowhere/b.wav", "/nowhere/c.wav"]);
        Assert.Equal(3, list.Cues.Count);

        shell.Cues.Journal.Undo();

        Assert.Empty(list.Cues);
    });

    [Fact]
    public Task BulkImportReportsTheDocumentChangedOnce() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Cues.ScopedList!;
        shell.Cues.Journal.Project.Settings.OutsideMedia = OutsideMediaPolicy.KeepInPlace;
        list.Cues.Clear();
        shell.Cues.Refresh();

        var changes = 0;
        shell.Cues.Journal.Changed += () => changes++;

        shell.Cues.AddMedia([.. Enumerable.Range(0, 40).Select(index => $"/nowhere/{index}.wav")]);

        // This is what made a bulk import quadratic rather than linear: every observer of the journal
        // ran per COMMAND, and the shell's observer re-runs the whole project status pass. An import is
        // a batch, not a gesture, so it reports once - see ProjectJournal.Composite(quiet).
        Assert.Equal(1, changes);
        Assert.Equal(40, list.Cues.Count);
    });

    [Fact]
    public Task BulkImportCostDoesNotClimbWithTheSizeOfTheList() => ShellFixture.WithShell(shell =>
    {
        shell.Cues.Journal.Project.Settings.OutsideMedia = OutsideMediaPolicy.KeepInPlace;

        string[] Batch(string tag) =>
            [.. Enumerable.Range(0, 40).Select(index => $"/nowhere/{tag}-{index}.wav")];

        // Twice before timing anything: the first import through this path pays for JIT and for the
        // first tree build, and comparing a cold run against a warm one measures the runtime rather
        // than the code.
        shell.Cues.AddMedia(Batch("warm-one"));
        shell.Cues.AddMedia(Batch("warm-two"));
        var early = Time(() => shell.Cues.AddMedia(Batch("early")));

        // Grow the list well past the batch size, then import the same batch again.
        shell.Cues.AddMedia([.. Enumerable.Range(0, 300).Select(index => $"/nowhere/bulk-{index}.wav")]);
        var late = Time(() => shell.Cues.AddMedia(Batch("late")));

        // Drain before leaving the body. 420 imports queue a lot of dispatcher work, and the PerTest
        // isolation scope resets the dispatcher the moment this returns — on a loaded win-x64 runner that
        // reset landed on a queue that was still draining and threw InvalidProgramException, "You've caused
        // dispatcher loop", out of teardown rather than out of anything this test asserts.
        //
        // Draining here, not retrying: the retry in HeadlessDispatchExtensions is deliberately confined to
        // SETUP failures, where the body has provably not run yet. This one fires after the body, so a retry
        // would re-run it — exactly what that guard's own reasoning rules out.
        Dispatcher.UIThread.RunJobs();

        // A SCALING assertion, not a benchmark, so the allowance is deliberately huge - it has to
        // survive a loaded CI box and a GC landing mid-run. The old behaviour was quadratic in the size
        // of the list and grew without bound (measured at 5 ms a file into a small list and 26 ms a
        // file into a large one, all on the UI thread), so a flat curve clears this by a wide margin
        // and a returning quadratic does not.
        Assert.True(
            late <= (early * 10) + 250,
            $"40 files into the grown list took {late} ms against {early} ms into the small one");
    });

    private static long Time(Action body)
    {
        var watch = Stopwatch.StartNew();
        body();
        watch.Stop();
        return watch.ElapsedMilliseconds;
    }
}
