using HaCue2.Machine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The machine-scope settings store and its recents list.
/// </summary>
/// <remarks>
/// Every test writes into a temp directory through the environment override, which is the reason that
/// override exists: a settings test that touched the developer's real profile would be one nobody
/// could run twice.
/// </remarks>
[Collection(MachineStorage.Name)]
public class AppSettingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hacue2-settings-" + Guid.NewGuid().ToString("N")[..8]);

    private string File => Path.Combine(_directory, "app-settings.json");

    public AppSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingSettingsAreDefaults()
    {
        var settings = AppSettingsStore.Load(File);

        Assert.Equal("booth dark", settings.Theme);
        Assert.Empty(settings.Recents);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var written = new AppSettings { Theme = "light", Density = "compact", RemotePort = "9001" };
        written.NoteOpened("/shows/a.hacue2proj", "A", "3 cues", DateTimeOffset.UnixEpoch);

        Assert.True(AppSettingsStore.Save(written, File));

        var read = AppSettingsStore.Load(File);

        Assert.Equal("light", read.Theme);
        Assert.Equal("compact", read.Density);
        Assert.Equal("9001", read.RemotePort);
        Assert.Single(read.Recents);
        Assert.Equal("A", read.Recents[0].Title);
    }

    [Fact]
    public void CorruptSettingsGiveDefaultsRatherThanThrowing()
    {
        System.IO.File.WriteAllText(File, "{ this is not json");

        // A preference file is a convenience. Failing to start over one would be the worst possible
        // trade, so an unreadable file reads as "no settings yet".
        var settings = AppSettingsStore.Load(File);

        Assert.Equal("booth dark", settings.Theme);
    }

    [Fact]
    public void ReopeningAProjectMovesItsRowRatherThanAddingASecond()
    {
        var settings = new AppSettings();

        settings.NoteOpened("/shows/a.hacue2proj", "A", "", DateTimeOffset.UnixEpoch);
        settings.NoteOpened("/shows/b.hacue2proj", "B", "", DateTimeOffset.UnixEpoch.AddHours(1));
        settings.NoteOpened("/shows/a.hacue2proj", "A", "", DateTimeOffset.UnixEpoch.AddHours(2));

        Assert.Equal(2, settings.Recents.Count);
        Assert.Equal("A", settings.Recents[0].Title);
        Assert.Equal("B", settings.Recents[1].Title);
    }

    [Fact]
    public void TheRecentsListIsBounded()
    {
        var settings = new AppSettings();

        for (var index = 0; index < AppSettings.MaxRecents + 6; index++)
            settings.NoteOpened($"/shows/{index}.hacue2proj", $"{index}", "", DateTimeOffset.UnixEpoch);

        Assert.Equal(AppSettings.MaxRecents, settings.Recents.Count);
        // Newest kept, oldest forgotten.
        Assert.Equal($"{AppSettings.MaxRecents + 5}", settings.Recents[0].Title);
    }

    [Fact]
    public void AProjectWithNoPathIsNotRecorded()
    {
        var settings = new AppSettings();

        // A show that has never been saved has nothing to reopen. A row pointing at "" would be a
        // permanent dead entry at the top of the launcher.
        settings.NoteOpened("", "Untitled", "", DateTimeOffset.UnixEpoch);

        Assert.Empty(settings.Recents);
    }

    [Fact]
    public void ForgettingRemovesTheRow()
    {
        var settings = new AppSettings();
        settings.NoteOpened("/shows/a.hacue2proj", "A", "", DateTimeOffset.UnixEpoch);

        settings.Forget("/shows/a.hacue2proj");

        Assert.Empty(settings.Recents);
    }
}
