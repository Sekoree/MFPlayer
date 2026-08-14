using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// "Clean" may only mean "the file holds exactly this document".
/// </summary>
/// <remarks>
/// The manual save serializes on the UI thread and writes asynchronously while the UI stays
/// editable. An edit landing inside that window is real work that is NOT in the file, and the old
/// blanket <c>MarkSaved</c> after the write called the document clean anyway - the one lie the dirty
/// flag must never tell, and how a night's edit could go missing without anyone being warned.
/// </remarks>
public sealed class SaveRevisionTests
{
    private sealed class Rename(string to) : IProjectCommand
    {
        private string _previous = "";

        public string Description => $"rename to {to}";
        public string Domain => "test";

        public void Apply(HaCueProject project)
        {
            _previous = project.Title;
            project.Title = to;
        }

        public void Revert(HaCueProject project) => project.Title = _previous;
    }

    [Fact]
    public void ASaveWhoseSnapshotIsStillCurrentMarksClean()
    {
        var journal = new ProjectJournal(new HaCueProject { Title = "before" });
        journal.Do(new Rename("after"));

        var revision = journal.Revision;

        Assert.True(journal.MarkSavedIfCurrent(revision));
        Assert.False(journal.IsDirty);
    }

    [Fact]
    public void AnEditDuringTheSaveKeepsTheDocumentDirty()
    {
        var journal = new ProjectJournal(new HaCueProject { Title = "before" });
        journal.Do(new Rename("first"));

        // The saver captured this revision when it serialized...
        var revision = journal.Revision;

        // ...and the operator kept editing while the bytes were on their way to disk.
        journal.Do(new Rename("second"));

        Assert.False(journal.MarkSavedIfCurrent(revision));
        Assert.True(journal.IsDirty);
    }

    [Fact]
    public void EveryChangeRouteMovesTheRevision()
    {
        var journal = new ProjectJournal(new HaCueProject { Title = "t" });
        var start = journal.Revision;

        journal.Do(new Rename("a"));
        var afterDo = journal.Revision;
        Assert.NotEqual(start, afterDo);

        journal.Undo();
        var afterUndo = journal.Revision;
        Assert.NotEqual(afterDo, afterUndo);

        journal.Redo();
        var afterRedo = journal.Revision;
        Assert.NotEqual(afterUndo, afterRedo);

        // Un-undoable document writes (a patch cue firing) move it too - and every time, because a
        // save racing the SECOND fire is just as stale as one racing the first.
        journal.MarkDirty(documentChanged: false);
        var afterDirty = journal.Revision;
        Assert.NotEqual(afterRedo, afterDirty);
        journal.MarkDirty(documentChanged: false);
        Assert.NotEqual(afterDirty, journal.Revision);
    }

    [Fact]
    public async Task TheWrittenFileHoldsExactlyTheCapturedSnapshot()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-save");

        try
        {
            var path = Path.Combine(directory.FullName, "show.hacue2proj");
            var project = new HaCueProject { Title = "the captured snapshot" };
            var json = HaCueProjectFile.Serialize(project);

            // The document mutates AFTER serialization, as it can while a write is in flight.
            project.Title = "moved on";

            await HaCueProjectFile.SaveSerializedAsync(json, path);

            var reloaded = HaCueProjectFile.Deserialize(await File.ReadAllTextAsync(path));
            Assert.Equal("the captured snapshot", reloaded.Title);

            // And no stray temp is left beside the show.
            Assert.Single(Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
