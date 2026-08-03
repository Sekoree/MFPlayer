using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>The Audio view's project-side edits.</summary>
public class AudioViewModelTests
{
    [Fact]
    public Task TheMixRateReachesTheDocumentAndIsUndoable() => ShellFixture.WithShell(shell =>
    {
        // It was a ComboBox with SelectedIndex="1" and no binding — the rate on screen and the rate in
        // the file had never been the same value.
        shell.Audio.MixRateIndex = 2;

        Assert.Equal(96_000, shell.Project.AudioPatch.MixSampleRate);

        shell.Undo();
        Assert.Equal(48_000, shell.Project.AudioPatch.MixSampleRate);
    });

    [Fact]
    public Task OnlyLinesThatCanPaceTheBayAreOfferedAsClockMaster() => ShellFixture.WithShell(shell =>
    {
        shell.Project.AudioLines.Add(new AudioLineDefinition
        {
            Name = "44k recorder",
            Kind = AudioLineKind.FileRecord,
            SampleRate = 44_100,
        });

        shell.Audio.Refresh();

        // A resampled master would drift the show clock against itself, so a line that cannot run
        // natively at the mix rate is not offered rather than offered and refused.
        Assert.DoesNotContain("44k recorder", shell.Audio.ClockMasters);
        Assert.Equal("none · wall clock", shell.Audio.ClockMasters[0]);
    });

    [Fact]
    public Task RecallingASnapshotChangesThePatchAndMarksTheProjectDirty() => ShellFixture.WithShell(shell =>
    {
        shell.Audio.SelectedSnapshot = shell.Audio.Snapshots[0];
        shell.Audio.RecallSelected();

        var snapshot = shell.Project.PatchSnapshots[0];
        var stored = snapshot.Cells[0];
        var live = shell.Project.AudioPatch.Cells.First(cell =>
            cell.Matches(stored.LogicalChannelId, stored.LineId, stored.LineChannel));

        Assert.Equal(stored.GainDb, live.GainDb);

        // A recall is an operator action on the patch, not an undoable edit — but it DOES travel in
        // the file, so a document that reported itself clean afterwards would lose the change.
        Assert.True(shell.Journal.IsDirty);
        Assert.False(shell.Journal.CanUndo);
    });

    [Fact]
    public Task UpdatingASnapshotIsJournaled() => ShellFixture.WithShell(shell =>
    {
        var snapshot = shell.Project.PatchSnapshots[0];
        var before = snapshot.Cells[0].GainDb;

        // Move the live patch, then capture it into the snapshot.
        shell.Project.AudioPatch.Cells
            .First(cell => cell.LogicalChannelId == snapshot.Cells[0].LogicalChannelId).GainDb = -20;

        shell.Audio.SelectedSnapshot = shell.Audio.Snapshots[0];
        shell.Audio.UpdateSelected();

        Assert.Equal(-20, shell.Project.PatchSnapshots[0].Cells[0].GainDb);

        // Overwriting somebody's stored state without an undo is the most expensive mistake this pane
        // can make, so unlike recall this one IS journaled.
        shell.Undo();
        Assert.Equal(before, shell.Project.PatchSnapshots[0].Cells[0].GainDb);
    });

    [Fact]
    public Task ChangingTheMixRateAsksForARestartOnlyOnceTheEngineHasStarted() =>
        ShellFixture.WithShell(shell =>
        {
            // No engine, so there is nothing running that differs from the document yet.
            shell.Audio.MixRateIndex = 0;
            Assert.False(shell.Audio.NeedsAudioRestart);

            shell.Audio.NoteAudioStarted();
            shell.Audio.MixRateIndex = 2;

            // Now the bay is running at a rate the document no longer says. Register item 14: never a
            // silent rebuild — the operator is told and presses the button.
            Assert.True(shell.Audio.NeedsAudioRestart);
        });
}

/// <summary>The settings screen's two scopes, which behave deliberately differently.</summary>
public class SettingsViewModelTests
{
    [Fact]
    public Task ProjectSettingsAreJournaled() => ShellFixture.WithShell(shell =>
    {
        var settings = new SettingsViewModel(shell.Project, shell.Journal, new AppSettings());

        settings.ClickMovesStandby = true;
        Assert.True(shell.Project.Settings.ClickMovesStandby);

        // Register items 26 and 28: the project half is journaled and travels in the file.
        shell.Undo();
        Assert.False(shell.Project.Settings.ClickMovesStandby);
    });

    [Fact]
    public Task SeedingTheFieldsIsNotAnEdit() => ShellFixture.WithShell(shell =>
    {
        _ = new SettingsViewModel(shell.Project, shell.Journal, new AppSettings());

        // Opening Settings must not make a project dirty. Without the loading guard every seeded value
        // was written straight back as an "edit", so a project opened with a dozen undo steps nobody
        // made and a dirty flag on an untouched file.
        Assert.False(shell.Journal.IsDirty);
        Assert.False(shell.Journal.CanUndo);
    });

    [Fact]
    public Task ApplicationSettingsAreNotJournaled() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();
        var settings = new SettingsViewModel(shell.Project, shell.Journal, app);

        settings.SpaceRule = "never";

        // The machine half saves immediately and has no undo — that is what the scope split means.
        Assert.Equal("never", app.SpaceRule);
        Assert.False(shell.Journal.CanUndo);
    });

    [Fact]
    public Task ApplicationSettingsSeedFromTheStore() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings { Theme = "light", RemotePort = "9100" };
        var settings = new SettingsViewModel(shell.Project, shell.Journal, app);

        Assert.Equal("light", settings.Theme);
        Assert.Equal("9100", settings.RemotePort);
    });

    [Fact]
    public Task TheRemoteApiRowSaysProjectOnlyWhenTheProjectOverridesIt() => ShellFixture.WithShell(shell =>
    {
        var plain = new SettingsViewModel(shell.Project, shell.Journal, new AppSettings());
        Assert.Empty(plain.ApplicationPanes.Single(pane => pane.Name == "Remote API").Tally);

        shell.Project.Settings.RemoteApi = new RemoteApiOverride { Enabled = true };

        var overridden = new SettingsViewModel(shell.Project, shell.Journal, new AppSettings());
        Assert.Equal("project", overridden.ApplicationPanes.Single(pane => pane.Name == "Remote API").Tally);
    });
}

/// <summary>The launcher, which is the first thing anyone sees and used to be entirely invented.</summary>
public class LauncherViewModelTests
{
    [Fact]
    public Task AFirstRunHasNoRecents() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var launcher = new LauncherViewModel(new AppSettings(), MachineFacts.Nothing);

        // It used to list four fictional shows, any of which opened the sample.
        Assert.Empty(launcher.Recents);
        Assert.False(launcher.HasRecents);
        Assert.False(launcher.HasRecovery);
    });

    [Fact]
    public Task RecentsComeFromTheStoreNewestFirst() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var settings = new AppSettings();
        settings.NoteOpened("/shows/a.hacue2proj", "A", "3 cues", DateTimeOffset.Now.AddDays(-2));
        settings.NoteOpened("/shows/b.hacue2proj", "B", "9 cues", DateTimeOffset.Now);

        var launcher = new LauncherViewModel(settings, MachineFacts.Nothing);

        Assert.Equal(["B", "A"], launcher.Recents.Select(row => row.Name));
        Assert.True(launcher.Recents[0].IsCurrent);
    });

    [Fact]
    public Task ARecentWhoseFileIsGoneIsMarkedRatherThanHidden() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var settings = new AppSettings();
        settings.NoteOpened("/nowhere/missing.hacue2proj", "Gone", "", DateTimeOffset.Now);

        var launcher = new LauncherViewModel(settings, MachineFacts.Nothing);

        // Hiding it would leave the operator wondering where their show went.
        Assert.True(launcher.Recents[0].IsMissing);
    });

    [Fact]
    public Task OpeningAMissingRecentReportsRatherThanThrows() =>
        ShellFixture.Session.DispatchAsync(async () =>
        {
            var settings = new AppSettings();
            settings.NoteOpened("/nowhere/missing.hacue2proj", "Gone", "", DateTimeOffset.Now);

            var launcher = new LauncherViewModel(settings, MachineFacts.Nothing);
            var opened = false;
            launcher.ProjectOpened += (_, _) => opened = true;

            await launcher.OpenAsync(launcher.Recents[0]);

            Assert.False(opened);
            Assert.Contains("no longer", launcher.OpenFailure, StringComparison.Ordinal);
        });

    [Fact]
    public Task MachineChecksSayNotCheckedRatherThanInventingAnAnswer() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var launcher = new LauncherViewModel(new AppSettings(), MachineFacts.Nothing);

            // "not checked" is a different answer from "none found", and a launcher that claimed to
            // have verified NDI would be believed.
            Assert.All(
                launcher.MachineChecks.Where(check => check.Category != "audio"),
                check => Assert.Contains("not checked", check.Message, StringComparison.Ordinal));
        });
}
