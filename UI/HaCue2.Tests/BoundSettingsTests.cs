using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The settings and endpoint panes edit something.
/// </summary>
/// <remarks>
/// A sweep for hardcoded remains found eleven settings boxes rendering literals — including the fixture
/// name <c>PROJECT · MIDSUMMER-2026</c> in the nav — and an endpoint pane whose host and port were
/// <c>10.0.1.20</c> and <c>8000</c> in the markup. An operator could change a transport default and
/// watch nothing happen, or point an action cue at a lighting desk and have it go to a hardcoded address.
/// </remarks>
public class BoundSettingsTests
{
    private static SettingsViewModel Settings(ShellViewModel shell, AppSettings? app = null) =>
        new(shell.Project, shell.Journal, app ?? new AppSettings());

    // ── application scope ──────────────────────────────────────────────────────────────────────

    [Fact]
    public Task DurationBoxesReadTheStoredSetting() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell, new AppSettings { PeakHoldMs = 2_000, StopFadeMs = 500, PanicFadeMs = 120 });

        Assert.Equal("2 s", settings.PeakHold);
        Assert.Equal("0.5 s", settings.AppStopFade);
        Assert.Equal("0.12 s", settings.AppPanicFade);
    });

    [Fact]
    public Task EditingADurationReachesTheSettings() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();
        var settings = Settings(shell, app);

        settings.AppStopFade = "1.25 s";

        Assert.Equal(1_250, app.StopFadeMs);
    });

    [Fact]
    public Task ADurationCanBeTypedInMilliseconds() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();

        Settings(shell, app).AppPanicFade = "80 ms";

        Assert.Equal(80, app.PanicFadeMs);
    });

    [Fact]
    public Task NonsenseIsRefusedAndTheStoredValuePutBack() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings { StopFadeMs = 750 };
        var settings = Settings(shell, app);

        settings.AppStopFade = "quite fast";

        // A stop fade of 0 ms is a click on every stop in the show, and not what somebody who typed
        // "quite fast" meant.
        Assert.Equal(750, app.StopFadeMs);
        Assert.Equal("0.75 s", settings.AppStopFade);
    });

    [Fact]
    public Task TheMixRateAcceptsTheSpacingItRenders() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();
        var settings = Settings(shell, app);

        settings.NewProjectMixRate = "96 000 Hz";

        Assert.Equal(96_000, app.NewProjectMixRate);
        Assert.Equal("96,000 Hz", settings.NewProjectMixRate);
    });

    [Fact]
    public Task AnImpossibleMixRateIsRefused() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings { NewProjectMixRate = 48_000 };

        Settings(shell, app).NewProjectMixRate = "3 Hz";

        Assert.Equal(48_000, app.NewProjectMixRate);
    });

    // ── project scope ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task TheNavHeadingNamesThisProject() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Title = "Midsummer";

        // Was the fixture's own name, baked into the markup on every project anybody opened.
        Assert.Equal("PROJECT · MIDSUMMER", Settings(shell).ProjectScopeHeading);
    });

    [Fact]
    public Task TheProjectStopFadeIsJournaled() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell);

        settings.ProjectStopFade = "2 s";
        Assert.Equal(2_000, shell.Project.Settings.StopFadeMs);

        // It changes what every STOP in the show does — exactly the sort of edit to undo.
        shell.Undo();
        Assert.Equal(750, shell.Project.Settings.StopFadeMs);
    });

    [Fact]
    public Task ClearingThePanicBoxGivesTheMachinesValueBack() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell, new AppSettings { PanicFadeMs = 250 });

        settings.ProjectPanicFade = "0.15 s";
        Assert.Equal(150, shell.Project.Settings.PanicFadeMs);

        // Empty is a real value: it inherits rather than pinning whatever number was showing.
        settings.ProjectPanicFade = "";
        Assert.Null(shell.Project.Settings.PanicFadeMs);
    });

    [Fact]
    public Task ThePanicNoteSaysWhatIsInForce() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell, new AppSettings { PanicFadeMs = 250 });

        Assert.Contains("follows this machine", settings.PanicFadeNote, StringComparison.Ordinal);

        settings.ProjectPanicFade = "0.15 s";
        Assert.Contains("overrides", settings.PanicFadeNote, StringComparison.Ordinal);
    });

    [Fact]
    public Task ThePanicBoxAndTheOverrideLedgerAgree() => ShellFixture.WithShell(shell =>
    {
        var settings = Settings(shell);

        settings.ProjectPanicFade = "0.15 s";

        // Two surfaces over one field; a ledger that disagreed with the box would be worse than none.
        Assert.Single(settings.Overrides, row => row.Setting == "Panic fade");
    });

    [Fact]
    public Task TheMediaRootIsJournaled() => ShellFixture.WithShell(shell =>
    {
        Settings(shell).ProjectMediaRoot = "/library/midsummer";

        Assert.Equal("/library/midsummer", shell.Project.Settings.MediaRoot);
    });

    // ── the remote token ───────────────────────────────────────────────────────────────────────

    [Fact]
    public Task TheTokenIsNeverRenderedInFull() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();
        app.EnsureRemoteToken();

        var mask = Settings(shell, app).RemoteTokenMask;

        // It grants the ability to fire the show; a settings pane left open in a booth must not be a
        // way to read it off the screen.
        Assert.DoesNotContain(app.RemoteToken, mask, StringComparison.Ordinal);
    });

    [Fact]
    public Task RotatingMintsADifferentToken() => ShellFixture.WithShell(shell =>
    {
        var app = new AppSettings();
        var first = app.EnsureRemoteToken();

        Settings(shell, app).RotateRemoteToken();

        // The old one must stop working — that is the entire point of the button.
        Assert.NotEqual(first, app.RemoteToken);
        Assert.NotEmpty(app.RemoteToken);
    });

    // ── action endpoints ───────────────────────────────────────────────────────────────────────

    private static (TargetsViewModel Targets, ActionEndpoint Endpoint) WithEndpoint(ShellViewModel shell)
    {
        var endpoint = new ActionEndpoint { Name = "Eos", Host = "10.0.1.20", Port = 8000 };
        shell.Project.ActionEndpoints.Add(endpoint);

        var targets = new TargetsViewModel(shell.Project, shell.Runtime, shell.Journal);
        targets.SelectedEndpoint = targets.Endpoints.Single(row => row.Id == endpoint.Id);

        return (targets, endpoint);
    }

    [Fact]
    public Task AnEndpointsHostAndPortAreEditable() => ShellFixture.WithShell(shell =>
    {
        var (targets, endpoint) = WithEndpoint(shell);

        targets.EndpointHost = "192.168.1.50";
        targets.EndpointPort = "9000";

        Assert.Equal("192.168.1.50", endpoint.Host);
        Assert.Equal(9000, endpoint.Port);
    });

    [Fact]
    public Task AnImpossiblePortIsRefused() => ShellFixture.WithShell(shell =>
    {
        var (targets, endpoint) = WithEndpoint(shell);

        targets.EndpointPort = "70000";

        // Port 0 is not "unset" to a socket, it is "pick one for me" — an action cue quietly sending to
        // an arbitrary port is worse than one that reports a bad number.
        Assert.Equal(8000, endpoint.Port);
    });

    [Fact]
    public Task TheTestMessageFollowsTheSelectedEndpoint() => ShellFixture.WithShell(shell =>
    {
        var first = new ActionEndpoint { Name = "Eos", TestMessage = "/eos/ping" };
        var second = new ActionEndpoint { Name = "QLab", TestMessage = "/qlab/ping" };
        shell.Project.ActionEndpoints.Add(first);
        shell.Project.ActionEndpoints.Add(second);

        var targets = new TargetsViewModel(shell.Project, shell.Runtime, shell.Journal);

        targets.SelectedEndpoint = targets.Endpoints.Single(row => row.Id == second.Id);

        // It used to be loaded once from the FIRST endpoint and never written back, so the box showed
        // one desk's payload while SEND TEST sent another's.
        Assert.Equal("/qlab/ping", targets.TestMessage);
    });

    [Fact]
    public Task EditingTheTestMessageReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (targets, endpoint) = WithEndpoint(shell);

        targets.TestMessage = "/eos/cue/1/fire";

        // What SEND TEST sends is read from the document, so a box that never wrote back proved
        // nothing about the desk and lied about what it had proved.
        Assert.Equal("/eos/cue/1/fire", endpoint.TestMessage);
    });

    [Fact]
    public Task EndpointEditsAreUndoable() => ShellFixture.WithShell(shell =>
    {
        var (targets, endpoint) = WithEndpoint(shell);

        targets.EndpointHost = "192.168.1.50";
        shell.Undo();

        Assert.Equal("10.0.1.20", endpoint.Host);
    });
}
