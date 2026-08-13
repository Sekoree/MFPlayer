using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using HaCue2.Machine;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Building a fixture project out of a media library.
/// </summary>
/// <remarks>
/// The seeder is pure - it is handed paths rather than reading a disk - so the SHAPE of what it builds
/// is testable without anybody's music on the machine running the test.
/// </remarks>
public class LibrarySeederTests
{
    private static LibrarySeed Seed(int audio = 8, int video = 4) => new(
        "Fixture",
        "/library",
        [.. Enumerable.Range(0, audio).Select(index => $"/library/Music/track-{index}.flac")],
        [.. Enumerable.Range(0, video).Select(index => $"/library/Videos/clip-{index}.mp4")]);

    [Fact]
    public void ItBuildsAProjectTheEngineWillAccept()
    {
        var project = LibrarySeeder.Build(Seed());

        // The same gate the app's own compile path runs. A fixture the engine refuses is worse than
        // no fixture: it makes every later failure look like the app's fault.
        ShowDocumentValidator.ThrowIfInvalid(ShowCompiler.Compile(project));
    }

    [Fact]
    public void EveryCueKindIsRepresented()
    {
        var cues = LibrarySeeder.Build(Seed()).AllCues().ToList();

        // The control-flow kinds are resolved app-side, so a fixture without them exercises none of
        // that code - and those are exactly the paths with no framework test behind them.
        Assert.Contains(cues, cue => cue is MediaCueNode);
        Assert.Contains(cues, cue => cue is GroupCueNode { FireMode: GroupFireMode.Playlist });
        Assert.Contains(cues, cue => cue is GroupCueNode { FireMode: GroupFireMode.Timeline });
        Assert.Contains(cues, cue => cue is PatchCueNode);
        Assert.Contains(cues, cue => cue is FadeCueNode);
        Assert.Contains(cues, cue => cue is JumpCueNode);
        Assert.Contains(cues, cue => cue is CommentCueNode);
    }

    [Fact]
    public void MediaUnderTheRootIsStoredRelative()
    {
        var project = LibrarySeeder.Build(Seed());
        var paths = project.AllCues().OfType<MediaCueNode>().Select(cue => cue.MediaPath).ToList();

        Assert.NotEmpty(paths);
        // Relative, so the show transports: an absolute path here would tie the fixture to one machine.
        Assert.All(paths, path => Assert.False(Path.IsPathRooted(path), path));
    }

    [Fact]
    public void MediaOutsideTheRootStaysAbsolute()
    {
        var seed = new LibrarySeed("Fixture", "/library", ["/elsewhere/stem.wav"], []);
        var project = LibrarySeeder.Build(seed);

        // Register item 26 allows media outside the root. Keeping it relative would make the reference
        // depend on where the project file happens to sit, which is what breaks on transport.
        var cue = Assert.Single(project.AllCues().OfType<MediaCueNode>());
        Assert.Equal("/elsewhere/stem.wav", cue.MediaPath);
    }

    [Fact]
    public void ItCarriesOneDeliberateRoutingError()
    {
        var report = ProjectStatus.Run(LibrarySeeder.Build(Seed()));

        // A fixture that passes every check teaches nothing about the screen that reports them, so
        // one output is fed by a cue and patched to nothing - the condition register item 25 singles
        // out as an ERROR rather than a warning.
        //
        // Scoped to the routing check rather than the report total: these paths are invented, so the
        // media check legitimately fails too, and asserting on the total would make this test about
        // the fixture's filenames instead of its patch.
        var routing = Assert.Single(report.Checks, check => check.Name == "Logical outputs");

        Assert.Equal(CheckOutcome.Failed, routing.Outcome);
        Assert.Contains(routing.Issues, issue => issue.Message.Contains("Sub", StringComparison.Ordinal));

        // And nothing ELSE about the patch is wrong - one error, deliberately placed.
        Assert.Single(routing.Issues);
    }

    [Fact]
    public void EveryJumpAndPatchCueTargetsSomethingThatExists()
    {
        var project = LibrarySeeder.Build(Seed());

        foreach (var jump in project.AllCues().OfType<JumpCueNode>())
            Assert.All(jump.TargetCueIds, id => Assert.NotNull(project.FindCue(id)));

        foreach (var patch in project.AllCues().OfType<PatchCueNode>())
        {
            Assert.NotNull(patch.SnapshotId);
            Assert.Contains(project.PatchSnapshots, snapshot => snapshot.Id == patch.SnapshotId);
        }
    }

    [Fact]
    public void VideoCuesArePlacedOnTheComposition()
    {
        var project = LibrarySeeder.Build(Seed());
        var composition = Assert.Single(project.Compositions);

        var placed = project.CueLists
            .Single(list => list.Name == "Video")
            .Flatten()
            .OfType<MediaCueNode>()
            .ToList();

        Assert.NotEmpty(placed);
        Assert.All(placed, cue =>
            Assert.Contains(cue.Placements, placement => placement.CompositionId == composition.Id));
    }

    [Fact]
    public void ASmallLibraryStillProducesAValidProject()
    {
        // One file and nothing else. The seeder must not assume it was handed enough for a playlist
        // AND a timeline AND a video list - a first run against a nearly empty folder is a real case.
        var project = LibrarySeeder.Build(new LibrarySeed("Tiny", "/library", ["/library/one.flac"], []));

        ShowDocumentValidator.ThrowIfInvalid(ShowCompiler.Compile(project));
        Assert.Single(project.AllCues().OfType<MediaCueNode>());
    }

    [Fact]
    public void AnEmptyLibraryStillProducesAValidProject()
    {
        var project = LibrarySeeder.Build(new LibrarySeed("Empty", "/library", [], []));

        ShowDocumentValidator.ThrowIfInvalid(ShowCompiler.Compile(project));
        Assert.NotEmpty(project.AudioPatch.LogicalChannels);
    }

    [Fact]
    public void CueNumbersAreUniqueWithinEachList()
    {
        var project = LibrarySeeder.Build(Seed());

        foreach (var list in project.CueLists)
        {
            var numbers = list.Flatten().Select(cue => cue.Number.Text).ToList();
            Assert.Equal(numbers.Count, numbers.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ItCarriesAPatchedButDisarmedRecorder()
    {
        var project = LibrarySeeder.Build(Seed());

        var archive = Assert.Single(project.AudioLines, line => line.Kind == AudioLineKind.FileRecord);

        // Patched, or it would record silence and teach that recording is broken.
        Assert.Equal(2, project.AudioPatch.Cells.Count(cell => cell.LineId == archive.Id));

        // Disarmed, because a fixture that wrote files the moment it opened would be a surprise on
        // somebody's disk.
        Assert.False(archive.Record!.ArmWithShow);

        // And writable: a fixture whose own recorder is refused would be the first thing anybody hit.
        Assert.Null(RecordFormatNames.Problem(archive.Record.Pattern, carriesVideo: false));
    }
}
