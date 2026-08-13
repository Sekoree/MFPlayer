using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The project override ledger (register item 26).
/// </summary>
/// <remarks>
/// Every row is DERIVED from the two scopes rather than authored, so the ledger cannot claim an
/// override the document does not hold - which is exactly what the sample version did, permanently
/// listing two on every project including ones that overrode nothing.
/// </remarks>
public class OverrideLedgerTests
{
    private static SettingsViewModel Settings(ShellViewModel shell, AppSettings? app = null) =>
        new(shell.Project, shell.Journal, app ?? new AppSettings());

    [Fact]
    public Task AProjectThatOverridesNothingHasAnEmptyLedger() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell);

        Assert.Empty(settings.Overrides);
        Assert.False(settings.HasOverrides);
        Assert.Contains("overrides nothing", settings.OverrideNote, StringComparison.Ordinal);
    });

    [Fact]
    public Task AnOverriddenPanicFadeAppearsBesideTheMachinesValue() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Settings.PanicFadeMs = 150;

        var settings = Settings(shell, new AppSettings { PanicFadeMs = 250 });
        var row = Assert.Single(settings.Overrides);

        // Both values, because a project override always WINS and must always be VISIBLE: somebody
        // reading the application pane has to see that the number in front of them is not in force.
        Assert.Equal("Panic fade", row.Setting);
        Assert.Equal("0.25 s", row.AppValue);
        Assert.Equal("0.15 s", row.ProjectValue);
    });

    [Fact]
    public Task AnOverriddenRemoteApiAppears() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Settings.RemoteApi = new RemoteApiOverride { Enabled = true, Port = 9001 };

        var row = Assert.Single(Settings(shell).Overrides);

        Assert.Equal("Remote API", row.Setting);
        Assert.Contains("9001", row.ProjectValue, StringComparison.Ordinal);
    });

    [Fact]
    public Task RevertingClearsTheOverrideAndIsUndoable() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Settings.PanicFadeMs = 150;

        var settings = Settings(shell);
        settings.RevertOverride("Panic fade");

        Assert.Null(shell.Project.Settings.PanicFadeMs);
        Assert.Empty(settings.Overrides);

        // Removing an override changes what the show DOES - a project that had pinned 150 ms and now
        // inherits 250 ms behaves differently, which is exactly the sort of change to be able to undo.
        shell.Undo();
        Assert.Equal(150, shell.Project.Settings.PanicFadeMs);
    });

    [Fact]
    public Task TheNavTallyCountsWhatIsActuallyOverridden() => ShellFixture.WithShell(shell =>
    {
        Assert.Empty(Settings(shell).ProjectPanes.Single(pane => pane.Name == "Overrides").Tally);

        shell.Project.Settings.PanicFadeMs = 150;
        Assert.Equal("1 active", Settings(shell).ProjectPanes.Single(pane => pane.Name == "Overrides").Tally);

        shell.Project.Settings.RemoteApi = new RemoteApiOverride();
        Assert.Equal("2 active", Settings(shell).ProjectPanes.Single(pane => pane.Name == "Overrides").Tally);
    });

    [Fact]
    public void AnUnsetOverrideInheritsTheMachineValue()
    {
        var settings = new ProjectSettings();

        // Nullable is what makes it an override rather than a copy. A plain int with a default would
        // pin whatever value the project was created with, and the ledger would have nothing to show.
        Assert.Null(settings.PanicFadeMs);
        Assert.Equal(400, settings.EffectivePanicFadeMs(400));

        settings.PanicFadeMs = 150;
        Assert.Equal(150, settings.EffectivePanicFadeMs(400));
    }

    [Fact]
    public Task AnOverrideTravelsInTheProjectFile() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Settings.PanicFadeMs = 150;

        var reloaded = HaCue2.Core.Serialization.HaCueProjectFile.Deserialize(
            HaCue2.Core.Serialization.HaCueProjectFile.Serialize(shell.Project));

        Assert.Equal(150, reloaded.Settings.PanicFadeMs);
    });

    [Fact]
    public Task AProjectWithNoOverrideStoresNull() => ShellFixture.WithShell(shell =>
    {
        var reloaded = HaCue2.Core.Serialization.HaCueProjectFile.Deserialize(
            HaCue2.Core.Serialization.HaCueProjectFile.Serialize(shell.Project));

        // Round-tripping must not invent an override. If it did, every saved project would start
        // defeating the machine's panic fade with a number nobody chose.
        Assert.Null(reloaded.Settings.PanicFadeMs);
    });
}
