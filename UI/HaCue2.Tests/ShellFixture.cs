using Avalonia.Headless;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.ViewModels;

namespace HaCue2.Tests;

/// <summary>
/// A shell over a small real project, built on the headless session.
/// </summary>
/// <remarks>
/// <para>
/// The project comes from <see cref="LibrarySeeder"/> — the same generator that builds the app's real
/// fixture — rather than from a hand-written one. Two reasons: it exercises every cue kind, and it
/// means the seeder and the view-models are tested against the same document, so a change that breaks
/// one is caught by the other rather than by neither.
/// </para>
/// <para>
/// No engine is started. A shell with no session is a fully working EDITOR, which is what these tests
/// are about — the transport's live half belongs to <c>HaCue2.Core.Tests</c>, where it can be tested
/// without devices.
/// </para>
/// </remarks>
internal static class ShellFixture
{
    /// <summary>Paths that do not exist, deliberately: nothing here should touch a disk.</summary>
    private static LibrarySeed Seed => new(
        "Test show",
        "/library",
        [.. Enumerable.Range(0, 8).Select(index => $"/library/Music/track-{index}.flac")],
        [.. Enumerable.Range(0, 4).Select(index => $"/library/Videos/clip-{index}.mp4")]);

    public static HaCueProject Project() => LibrarySeeder.Build(Seed);

    /// <summary>The session every test dispatches onto.</summary>
    public static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ShellFixture).Assembly);

    /// <summary>Runs a body on the UI session with a fresh shell over a fresh project.</summary>
    public static Task WithShell(Action<ShellViewModel> body) =>
        Session.DispatchGuarded(() => body(new ShellViewModel(Project())));

    /// <summary>The same, for a body that needs to await.</summary>
    public static Task WithShellAsync(Func<ShellViewModel, Task> body) =>
        Session.DispatchAsync(() => body(new ShellViewModel(Project())));

    /// <summary>The first media cue in the Music list — the one the fixture calls "Walk-in bed".</summary>
    public static MediaCueNode Bed(HaCueProject project) =>
        project.CueLists.Single(list => list.Name == "Music").Flatten().OfType<MediaCueNode>().First();

    /// <summary>Selects a cue in the tree by id, the way a click would.</summary>
    public static void Select(CuesViewModel cues, Guid cueId) =>
        cues.SelectedCue = cues.AllRows.First(row => row.Id == cueId);
}
