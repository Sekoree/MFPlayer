using System.Reflection;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class JournalTests
{
    [Fact]
    public void ACommandRevertsTheDocumentToAByteIdenticalSerialization()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -3));
        Assert.NotEqual(before, HaCueProjectFile.Serialize(fixture.Project));

        journal.Undo();

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    [Fact]
    public void EveryEditKindRevertsToTheSameBytes()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -12));
        journal.Do(new AddItemCommand<CueNode>(
            fixture.List.Cues, new CommentCueNode { Number = 9 }, 3, "cues", "add comment"));
        journal.Do(new RemoveItemCommand<CueNode>(fixture.List.Cues, fixture.Jump, "cues", "remove jump"));
        journal.Do(new SetPatchCellCommand(
            fixture.Project.AudioPatch, fixture.MainL.Id, fixture.Interface.Id, 0, -6, null, "trim"));
        journal.Do(new SetPatchCellCommand(
            fixture.Project.AudioPatch, fixture.FoldL.Id, fixture.Interface.Id, 2, null, null, "unroute"));

        while (journal.Undo())
        {
        }

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    [Fact]
    public void RedoRestoresWhatUndoTookAway()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -3));
        var edited = HaCueProjectFile.Serialize(fixture.Project);

        journal.Undo();
        journal.Redo();

        Assert.Equal(edited, HaCueProjectFile.Serialize(fixture.Project));
    }

    /// <summary>A fader drag is one gesture, so it is one undo step.</summary>
    [Fact]
    public void AStreamOfEditsToOnePropertyIsExactlyOneUndoStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        foreach (var level in new double[] { -7, -8, -9, -10 })
            journal.Do(LevelEdit(fixture.Track, level));

        Assert.Single(journal.Log);
        Assert.Equal(-10, fixture.Track.LevelDb);

        journal.Undo();

        // Back to where the drag STARTED, not to its last frame.
        Assert.Equal(-6, fixture.Track.LevelDb);
    }

    [Fact]
    public void ClosingTheGroupStartsANewUndoStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -7));
        journal.CloseGroup();
        journal.Do(LevelEdit(fixture.Track, -8));

        Assert.Equal(2, journal.Log.Count);

        journal.Undo();
        Assert.Equal(-7, fixture.Track.LevelDb);
    }

    [Fact]
    public void EditsToDifferentPropertiesDoNotCoalesce()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -7));
        journal.Do(new SetValueCommand<string>(
            fixture.Track.Id, "label", "cues",
            () => fixture.Track.Label, value => fixture.Track.Label = value, "Renamed"));

        Assert.Equal(2, journal.Log.Count);
    }

    /// <summary>A multi-selection edit is one thing the operator did, so it is one thing to undo.</summary>
    [Fact]
    public void AMultiSelectionEditIsExactlyOneCompositeStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var cues = fixture.List.Cues;

        using (journal.Composite("set trigger on 3 cues", "cues"))
            foreach (var cue in cues)
            {
                var target = cue;
                journal.Do(new SetValueCommand<CueTrigger>(
                    target.Id, "trigger", "cues",
                    () => target.Trigger, value => target.Trigger = value, CueTrigger.Follow));
            }

        Assert.Single(journal.Log);
        Assert.All(cues, cue => Assert.Equal(CueTrigger.Follow, cue.Trigger));

        journal.Undo();

        Assert.All(cues, cue => Assert.Equal(CueTrigger.Manual, cue.Trigger));
    }

    /// <summary>
    /// A composite opened inside another joins it. Callers compose — a group-linked patch nudge inside
    /// a drag, a delete-with-cleanup inside a multi-selection edit — and the OUTER scope is what the
    /// operator did, so it is what one undo takes back.
    /// </summary>
    [Fact]
    public void ANestedCompositeJoinsTheOpenOneRatherThanThrowing()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        using (journal.Composite("outer", "cues"))
        {
            journal.Do(LevelEdit(fixture.Track, -3));

            using (journal.Composite("inner", "outputs"))
                ProjectEdits.DeleteLogicalChannel(journal, fixture.FoldR.Id);
        }

        Assert.Single(journal.Log);
        Assert.Equal("outer", journal.NextUndo!.Description);

        journal.Undo();

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    /// <summary>
    /// Coalescing applies inside a composite too — otherwise a drag wrapped in one keeps every
    /// intermediate value alive for as long as the undo entry exists.
    /// </summary>
    [Fact]
    public void AStreamOfEditsInsideACompositeCollapsesToOneCommand()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        using (journal.Composite("drag", "cues"))
            foreach (var level in new double[] { -7, -8, -9, -10 })
                journal.Do(LevelEdit(fixture.Track, level));

        var composite = Assert.IsType<CompositeCommand>(journal.NextUndo);
        Assert.Single(composite.Commands);
        Assert.Equal(-10, fixture.Track.LevelDb);

        journal.Undo();
        Assert.Equal(-6, fixture.Track.LevelDb);
    }

    [Fact]
    public void AnEmptyCompositeIsNotAnUndoStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        using (journal.Composite("touched nothing", "cues"))
        {
        }

        Assert.Empty(journal.Log);
        Assert.False(journal.CanUndo);
    }

    /// <summary>
    /// Register item 11: deleting a logical output strips every reference to it as ONE undoable edit.
    /// </summary>
    [Fact]
    public void DeletingALogicalOutputCleansUpEverythingInOneStep()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        ProjectEdits.DeleteLogicalChannel(journal, fixture.MainL.Id);

        Assert.Single(journal.Log);
        Assert.DoesNotContain(fixture.Project.AudioPatch.LogicalChannels, c => c.Id == fixture.MainL.Id);
        Assert.DoesNotContain(fixture.Project.AudioPatch.Cells, c => c.LogicalChannelId == fixture.MainL.Id);
        Assert.DoesNotContain(fixture.Track.Sends, s => s.LogicalChannelId == fixture.MainL.Id);
        Assert.DoesNotContain(fixture.MainGroup.MemberIds, id => id == fixture.MainL.Id);

        journal.Undo();

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    /// <summary>
    /// The invariant that makes the whole design work: undo cost is proportional to what CHANGED, not
    /// to project size. If a command ever captured the document, this fails.
    /// </summary>
    [Fact]
    public void TheJournalHoldsCommandsRatherThanDocumentSnapshots()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -3));
        journal.CloseGroup();
        ProjectEdits.DeleteLogicalChannel(journal, fixture.FoldR.Id);

        foreach (var command in journal.Log)
            AssertHoldsNoDocument(command, depth: 0);
    }

    [Fact]
    public void SavingIsTheOnlyThingThatMakesAProjectClean()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        Assert.False(journal.IsDirty);

        journal.Do(LevelEdit(fixture.Track, -3));
        Assert.True(journal.IsDirty);

        journal.MarkSaved();
        Assert.False(journal.IsDirty);

        journal.Do(LevelEdit(fixture.Track, -4));
        Assert.True(journal.IsDirty);
    }

    /// <summary>
    /// Undoing back to the state on disk is clean again — the dirty flag answers "does this differ
    /// from the file", not "has anything ever happened".
    /// </summary>
    [Fact]
    public void UndoingBackToTheSavedStateIsCleanAgain()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        journal.MarkSaved();

        journal.Do(LevelEdit(fixture.Track, -3));
        journal.CloseGroup();
        journal.Do(LevelEdit(fixture.Track, -4));
        Assert.True(journal.IsDirty);

        journal.Undo();
        journal.Undo();

        Assert.False(journal.IsDirty);
    }

    [Fact]
    public void LoadingClearsTheHistoryThatBelongedToTheOldProject()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);
        journal.Do(LevelEdit(fixture.Track, -3));

        journal.Reset(new TestProject().Project);

        Assert.False(journal.CanUndo);
        Assert.False(journal.CanRedo);
        Assert.False(journal.IsDirty);
    }

    [Fact]
    public void ANewEditDiscardsTheRedoBranch()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(LevelEdit(fixture.Track, -3));
        journal.Undo();
        Assert.True(journal.CanRedo);

        journal.Do(LevelEdit(fixture.Track, -9));

        Assert.False(journal.CanRedo);
    }

    [Fact]
    public void TheUndoToastNamesTheDomainItWouldChange()
    {
        var fixture = new TestProject();
        var journal = new ProjectJournal(fixture.Project);

        journal.Do(new SetPatchCellCommand(
            fixture.Project.AudioPatch, fixture.FoldL.Id, fixture.Interface.Id, 2, 0, null,
            "Fold L → Out 3 gain"));

        Assert.Equal("patch", journal.NextUndo!.Domain);
        Assert.Equal("Fold L → Out 3 gain", journal.NextUndo.Description);
    }

    private static SetValueCommand<double> LevelEdit(MediaCueNode cue, double level) =>
        new(cue.Id, "level", "cues", () => cue.LevelDb, value => cue.LevelDb = value, level);

    /// <summary>
    /// Walks a command's fields looking for a captured <see cref="HaCueProject"/>.
    /// </summary>
    /// <remarks>
    /// Reflection because the property is about what commands may HOLD, and no public surface can
    /// express that. Bounded depth so a model reference reached through a closure does not walk the
    /// whole document — the thing being caught is a document field, which is at depth 1 or 2.
    /// </remarks>
    private static void AssertHoldsNoDocument(object command, int depth)
    {
        if (depth > 3)
            return;

        foreach (var field in command.GetType()
                     .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var value = field.GetValue(command);
            switch (value)
            {
                case HaCueProject:
                    Assert.Fail($"{command.GetType().Name}.{field.Name} captured the whole document.");
                    break;
                case IProjectCommand nested:
                    AssertHoldsNoDocument(nested, depth + 1);
                    break;
                case IEnumerable<IProjectCommand> children:
                    foreach (var child in children)
                        AssertHoldsNoDocument(child, depth + 1);
                    break;
            }
        }
    }
}
