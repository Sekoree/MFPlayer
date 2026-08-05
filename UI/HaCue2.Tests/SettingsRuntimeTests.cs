using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

public sealed class SettingsRuntimeTests
{
    [Fact]
    public Task DecimalSecondsProduceTheDocumentedDoubleGoWindow() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var shell = new ShellViewModel(
            ShellFixture.Project(), MachineFacts.Nothing,
            settings: new AppSettings { DoubleGoGuard = "0.25 s" });

        Assert.Equal(TimeSpan.FromMilliseconds(250), shell.Cues.DoubleGoGuard);
    });

    [Fact]
    public Task DrawerAndLayoutPreferencesAreConsumedAtOpen() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var app = new AppSettings
        {
            OpenDrawerOnLaunch = true,
            FlatActiveList = true,
            RememberInspectorTab = false,
        };
        var shell = new ShellViewModel(ShellFixture.Project(), MachineFacts.Nothing, settings: app);

        Assert.True(shell.IsOutputInfoOpen);
        Assert.True(shell.Cues.FlatActiveList);
        Assert.False(shell.Cues.Inspector.RememberTabs);
    });

    [Fact]
    public void NewProjectsInheritTheMachineStopFade()
    {
        var project = ProjectFiles.Create("Show", "", new AppSettings { StopFadeMs = 1_250 });

        Assert.Equal(1_250, project.Settings.StopFadeMs);
    }

    [Fact]
    public void DisabledChecksStayNeutralUntilTheOperatorRunsThem() => ShellFixture.Session.DispatchGuarded(() =>
    {
        var project = ShellFixture.Project();
        project.Settings.RunStatusChecksOnOpen = false;

        var shell = new ShellViewModel(project);

        Assert.All(shell.Status.Checks, check => Assert.Equal(HaCue2.Core.Validation.CheckOutcome.NotChecked, check.Outcome));
        Assert.Equal("○ outputs not checked", shell.OutputSummary);
        Assert.Contains("not checked", shell.Status.Summary, StringComparison.Ordinal);
    });

    [Fact]
    public void PpmAndVuUseDifferentRawReadingsAndPeakHoldExpires()
    {
        var presenter = new ProgramMeterPresenter();
        var app = new AppSettings { MeterBallistics = "PPM fast", PeakHoldMs = 1_000 };
        var start = DateTimeOffset.UnixEpoch;

        var loud = Assert.Single(presenter.Present([new ProgramMeter("L", .25, .8)], app, start));
        var held = Assert.Single(presenter.Present([new ProgramMeter("L", .2, .3)], app, start.AddMilliseconds(500)));
        var released = Assert.Single(presenter.Present([new ProgramMeter("L", .2, .3)], app, start.AddMilliseconds(1_100)));

        Assert.Equal(.8, loud.Level);
        Assert.Equal(.8, held.Peak);
        Assert.Equal(.3, released.Peak);

        app.MeterBallistics = "VU";
        var vu = Assert.Single(presenter.Present([new ProgramMeter("R", .25, .8)], app, start));
        Assert.Equal(.25, vu.Level);
    }

    [Fact]
    public void HotkeyProfilesProvideDefaultsAndPersistEdits()
    {
        var app = new AppSettings();
        Assert.Equal("Space", AppHotkeys.Gesture(app, AppHotkeys.Go));

        app.HotkeyBindings[AppHotkeys.Go] = "F5";
        Assert.True(AppHotkeys.Matches(app, AppHotkeys.Go, "F5"));

        AppHotkeys.Reset(app);
        app.HotkeyProfile = "Laptop";
        Assert.Equal("Ctrl+Up", AppHotkeys.Gesture(app, AppHotkeys.StandbyUp));
    }
}
