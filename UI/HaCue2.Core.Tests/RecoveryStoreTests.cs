using HaCue2.Core.Serialization;
using HaCue2.Machine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Autosave, and the question the launcher asks about it.
/// </summary>
/// <remarks>
/// <para>
/// The rule under test throughout is that recovery is an OFFER, never something that happened to the
/// operator's show while they were not looking: the project file is never written, and an autosave is
/// only offered when it holds work the file does not.
/// </para>
/// <para>
/// <b>Collected, so nothing runs beside it.</b> These tests redirect the machine-local root with an
/// environment variable, which is process-wide - a second class touching the same storage in parallel
/// would read a root this one had just repointed, and the failure would depend on timing.
/// </para>
/// </remarks>
[Collection(MachineStorage.Name)]
public class RecoveryStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "hacue2-recovery-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _shows;

    public RecoveryStoreTests()
    {
        Directory.CreateDirectory(_root);
        _shows = Path.Combine(_root, "shows");
        Directory.CreateDirectory(_shows);

        // The whole machine-local root, so nothing here touches a real profile.
        Environment.SetEnvironmentVariable(StoragePaths.RootVariable, Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StoragePaths.RootVariable, null);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private string ShowPath(string name) => Path.Combine(_shows, name + HaCueProjectFile.Extension);

    [Fact]
    public async Task AnAutosaveNewerThanItsFileIsOffered()
    {
        var fixture = new TestProject();
        var path = ShowPath("midsummer");
        await HaCueProjectFile.SaveAsync(fixture.Project, path);

        fixture.Project.Title = "midsummer";
        await RecoveryStore.SaveAsync(
            fixture.Project, path, edits: 3, keepCopies: 5, DateTimeOffset.Now.AddMinutes(5));

        var found = RecoveryStore.Scan();

        Assert.Single(found);
        Assert.Equal(path, found[0].OriginalPath);
        Assert.Equal(3, found[0].Edits);
        Assert.Contains("+3 edits", found[0].Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAutosaveOlderThanItsFileIsNotOffered()
    {
        var fixture = new TestProject();
        var path = ShowPath("saved-since");

        await RecoveryStore.SaveAsync(
            fixture.Project, path, edits: 2, keepCopies: 5, DateTimeOffset.Now.AddMinutes(-5));

        // The operator saved AFTER the autosave was written, so there is nothing outstanding. Offering
        // it anyway would invite them to overwrite good work with stale work.
        await HaCueProjectFile.SaveAsync(fixture.Project, path);

        Assert.Empty(RecoveryStore.Scan());
    }

    [Fact]
    public async Task AnAutosaveWhoseProjectFileIsGoneIsStillOffered()
    {
        var fixture = new TestProject();
        var path = ShowPath("deleted");

        await RecoveryStore.SaveAsync(
            fixture.Project, path, edits: 1, keepCopies: 5, DateTimeOffset.Now);

        // The file never existed on disk. This is the case recovery matters MOST in - the copy may be
        // all that is left of the show.
        Assert.Single(RecoveryStore.Scan());
    }

    [Fact]
    public async Task TheAutosaveIsALoadableProjectAndTheOriginalIsUntouched()
    {
        var fixture = new TestProject();
        var path = ShowPath("untouched");
        await HaCueProjectFile.SaveAsync(fixture.Project, path);
        var before = await File.ReadAllTextAsync(path);

        fixture.Project.Title = "edited since saving";
        await RecoveryStore.SaveAsync(
            fixture.Project, path, edits: 1, keepCopies: 5, DateTimeOffset.Now.AddMinutes(1));

        var candidate = Assert.Single(RecoveryStore.Scan());
        var recovered = await HaCueProjectFile.LoadAsync(candidate.CopyPath);

        Assert.Equal("edited since saving", recovered.Title);
        // The operator's own file is exactly what they last chose to write.
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OlderCopiesArePrunedToTheProjectsLimit()
    {
        var fixture = new TestProject();
        var path = ShowPath("rotating");

        for (var minute = 0; minute < 6; minute++)
        {
            await RecoveryStore.SaveAsync(
                fixture.Project, path, edits: minute, keepCopies: 2,
                DateTimeOffset.Now.AddMinutes(minute));
        }

        var directory = Path.GetDirectoryName(Assert.Single(RecoveryStore.Scan()).CopyPath)!;

        // A long rehearsal must not fill a disk with autosaves.
        Assert.Equal(2, Directory.GetFiles(directory, "*" + HaCueProjectFile.Extension).Length);
    }

    [Fact]
    public async Task DiscardingRemovesEverythingForThatProject()
    {
        var fixture = new TestProject();
        var path = ShowPath("discarded");

        await RecoveryStore.SaveAsync(fixture.Project, path, 1, 5, DateTimeOffset.Now);
        await RecoveryStore.SaveAsync(fixture.Project, path, 2, 5, DateTimeOffset.Now.AddMinutes(1));

        Assert.True(RecoveryStore.Discard(Assert.Single(RecoveryStore.Scan())));

        // Leaving an older copy behind would make the banner reappear at the next launch, which reads
        // as the app ignoring the answer it was just given.
        Assert.Empty(RecoveryStore.Scan());
    }

    [Fact]
    public async Task SavingTheProjectClearsItsAutosaves()
    {
        var fixture = new TestProject();
        var path = ShowPath("cleared");

        await RecoveryStore.SaveAsync(fixture.Project, path, 4, 5, DateTimeOffset.Now);
        Assert.Single(RecoveryStore.Scan());

        RecoveryStore.Clear(path, fixture.Project.Title);

        Assert.Empty(RecoveryStore.Scan());
    }

    [Fact]
    public void ScanningWithNoRecoveryDirectoryIsEmptyRatherThanAFailure() =>
        Assert.Empty(RecoveryStore.Scan());
}

/// <summary>
/// Groups every test that redirects <see cref="StoragePaths"/> with an environment variable.
/// </summary>
/// <remarks>
/// xunit runs test CLASSES in parallel but the tests within one collection in sequence, so naming a
/// collection is how a process-wide override is made safe. Any future suite that touches the
/// machine-local root belongs in this collection too.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MachineStorage
{
    public const string Name = "machine-storage";
}
