using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class MediaEditTests
{
    [Fact]
    public void RelinkingByFileNameFindsAMovedFileAnywhereUnderTheNewRoot()
    {
        var fixture = Missing("sfx/storm-bed.flac");
        var store = new FakeStore { "/new/root/audio/effects/storm-bed.flac" };
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Relink(journal, "/new/root", RelinkStrategy.ByFileName, store: store);

        Assert.True(result.IsComplete);
        Assert.Equal("/new/root/audio/effects/storm-bed.flac", fixture.Track.MediaPath);
    }

    [Fact]
    public void RelinkingBySubPathKeepsTheStructureUnderTheNewRoot()
    {
        var fixture = Missing("sfx/storm-bed.flac");
        var store = new FakeStore { "/new/root/sfx/storm-bed.flac" };
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Relink(journal, "/new/root", RelinkStrategy.BySubPath, store: store);

        Assert.True(result.IsComplete);
        Assert.Equal("/new/root/sfx/storm-bed.flac", fixture.Track.MediaPath);
    }

    /// <summary>
    /// A relink that fixed nine of ten files and said "done" produces a show that fails on the tenth
    /// cue, live, with no record of which one.
    /// </summary>
    [Fact]
    public void WhatCouldNotBeFoundIsReportedRatherThanSilentlySkipped()
    {
        var fixture = Missing("sfx/storm-bed.flac");
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Relink(journal, "/new/root", RelinkStrategy.ByFileName,
            store: new FakeStore());

        Assert.False(result.IsComplete);
        Assert.Contains("storm-bed.flac", Assert.Single(result.Unresolved));
        Assert.Equal("sfx/storm-bed.flac", fixture.Track.MediaPath);
    }

    /// <summary>
    /// Only MISSING references are touched. On a machine where the old root is still mounted, relinking
    /// everything would silently move the show onto a different copy of the same media.
    /// </summary>
    [Fact]
    public void FilesThatAlreadyResolveAreLeftAlone()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/old/root/track.flac";
        var store = new FakeStore { "/old/root/track.flac", "/new/root/track.flac" };
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Relink(journal, "/new/root", RelinkStrategy.ByFileName, store: store);

        Assert.Empty(result.Changed);
        Assert.Equal("/old/root/track.flac", fixture.Track.MediaPath);
    }

    [Fact]
    public void ARelinkIsOneUndoableEdit()
    {
        var fixture = Missing("sfx/storm-bed.flac");
        fixture.Project.Compositions[0].IdleImagePath = "art/logo.png";
        var store = new FakeStore { "/new/root/storm-bed.flac", "/new/root/logo.png" };
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        var result = MediaEdits.Relink(journal, "/new/root", RelinkStrategy.ByFileName, store: store);

        Assert.Equal(2, result.Changed.Count);
        Assert.Single(journal.Log);

        journal.Undo();

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    [Fact]
    public void ConsolidateCopiesEveryFileAndRewritesToTheFolder()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/elsewhere/track.flac";
        fixture.Project.Compositions[0].IdleImagePath = "/art/logo.png";
        var store = new FakeStore { "/elsewhere/track.flac", "/art/logo.png" };
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Consolidate(journal, "/tour/media", store: store);

        Assert.True(result.IsComplete);
        Assert.Equal("track.flac", fixture.Track.MediaPath);
        Assert.Equal("logo.png", fixture.Project.Compositions[0].IdleImagePath);
        Assert.Contains("/tour/media/track.flac", store.Copied);
        Assert.Contains("/tour/media/logo.png", store.Copied);
    }

    /// <summary>
    /// Two cues may legitimately reference "loop.wav" from different folders. Flattening both onto one
    /// name would silently make the second play the first's audio.
    /// </summary>
    [Fact]
    public void TwoFilesWithTheSameNameDoNotOverwriteEachOther()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/a/loop.wav";
        var second = new MediaCueNode { Number = 9, Label = "Second", MediaPath = "/b/loop.wav" };
        fixture.List.Cues.Add(second);
        var store = new FakeStore { "/a/loop.wav", "/b/loop.wav" };
        var journal = new ProjectJournal(fixture.Project);

        MediaEdits.Consolidate(journal, "/tour/media", store: store);

        Assert.Equal("loop.wav", fixture.Track.MediaPath);
        Assert.Equal("loop-2.wav", second.MediaPath);
        Assert.Equal(2, store.Copied.Count);
    }

    /// <summary>
    /// What cannot be copied keeps pointing where it already pointed. The alternative is a project that
    /// LOOKS consolidated and half-works at the venue.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeCopiedIsReportedAndLeftPointingWhereItWas()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/locked/track.flac";
        var store = new FakeStore { "/locked/track.flac" };
        store.RefuseToCopy.Add("/locked/track.flac");
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Consolidate(journal, "/tour/media", store: store);

        Assert.False(result.IsComplete);
        Assert.Contains("could not copy", Assert.Single(result.Unresolved));
        Assert.Equal("/locked/track.flac", fixture.Track.MediaPath);
    }

    [Fact]
    public void ConsolidateReportsMediaThatWasAlreadyMissing()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/gone/track.flac";
        var journal = new ProjectJournal(fixture.Project);

        var result = MediaEdits.Consolidate(journal, "/tour/media", store: new FakeStore());

        Assert.Contains("was not found", Assert.Single(result.Unresolved));
    }

    [Fact]
    public void ConsolidateIsOneUndoableEdit()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "/elsewhere/track.flac";
        var store = new FakeStore { "/elsewhere/track.flac" };
        var journal = new ProjectJournal(fixture.Project);
        var before = HaCueProjectFile.Serialize(fixture.Project);

        MediaEdits.Consolidate(journal, "/tour/media", store: store);
        Assert.Single(journal.Log);

        journal.Undo();

        Assert.Equal(before, HaCueProjectFile.Serialize(fixture.Project));
    }

    private static TestProject Missing(string path)
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = path;
        return fixture;
    }

    private sealed class FakeStore : IMediaStore, IEnumerable<string>
    {
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);

        public HashSet<string> RefuseToCopy { get; } = new(StringComparer.Ordinal);
        public List<string> Copied { get; } = [];

        public void Add(string path) => _files.Add(path);

        public bool Exists(string path) => _files.Contains(path);

        public IEnumerable<string> Enumerate(string directory) =>
            _files.Where(file => file.StartsWith(directory, StringComparison.Ordinal));

        public bool Copy(string sourcePath, string destinationPath)
        {
            if (RefuseToCopy.Contains(sourcePath))
                return false;

            Copied.Add(destinationPath);
            _files.Add(destinationPath);
            return true;
        }

        public IEnumerator<string> GetEnumerator() => _files.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
