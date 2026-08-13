using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The audition rig (register item 15) and the preview it feeds.
/// </summary>
/// <remarks>
/// The load-bearing property throughout is that audition is MONITORING: it never reaches the program
/// mix, never appears in the Active list, and is therefore always safe to press - which is why it sits
/// on every cue's context menu rather than behind a mode.
/// </remarks>
public class AuditionTests
{
    [Fact]
    public Task TheRigIsOneObjectSharedByBothViews() => ShellFixture.WithShell(shell =>
    {
        // Register item 15: a rig is a single thing. Two view-models over it would drift the moment
        // either was edited - set a surface in Video, find it unset in Audio.
        Assert.Same(shell.Audio.Audition, shell.Video.Audition);
    });

    [Fact]
    public Task TheRigListsLinesRatherThanDevices() => ShellFixture.WithShell(shell =>
    {
        var audition = shell.Audio.Audition;

        // D8: the rig is an output like any other, so it names one of the project's own lines. A raw
        // device would make the audition path the one output that behaved differently from the rest.
        Assert.Equal("default monitor line", audition.Devices[0]);
        Assert.Contains(audition.Devices, entry => entry.Contains("Default out", StringComparison.Ordinal));
    });

    [Fact]
    public Task ChoosingALineIsJournaledAndReportsItsWidth() => ShellFixture.WithShell(shell =>
    {
        var audition = shell.Audio.Audition;
        var line = shell.Project.AudioLines[0];

        audition.DeviceIndex = 1;

        Assert.Equal(line.Id, shell.Project.Audition.AudioLineId);
        // The width comes FROM the line - never assumed stereo, which is the whole of D8.
        Assert.Contains($"{line.Channels} channel", audition.Width, StringComparison.Ordinal);

        shell.Undo();
        Assert.Null(shell.Project.Audition.AudioLineId);
    });

    [Fact]
    public Task AnUnconfiguredRigFollowsTheBaysDefault() => ShellFixture.WithShell(shell =>
    {
        // Not a placeholder: the default monitor terminal is the first line the bay opened, which on a
        // one-interface rig is the right answer and is why audition works before anyone configures it.
        Assert.Null(shell.Project.Audition.AudioLineId);
        Assert.Contains("default monitor line", shell.Audio.Audition.Width, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheLevelAndDuckRoundTrip() => ShellFixture.WithShell(shell =>
    {
        var audition = shell.Audio.Audition;

        audition.Level = "-3.0 dB";
        Assert.Equal(-3, shell.Project.Audition.LevelDb, 3);

        audition.DuckWhenProgramSounds = false;
        Assert.False(shell.Project.Audition.DuckWhenProgramSounds);
    });

    [Fact]
    public Task TheSurfaceDefaultsToAudioOnly() => ShellFixture.WithShell(shell =>
    {
        // A video surface costs a window, and most cues are audio. An operator who never previews a
        // video cue should never see one appear.
        Assert.Equal(AuditionSurface.None, shell.Project.Audition.Surface);
        Assert.Contains("audio only", shell.Audio.Audition.Size, StringComparison.Ordinal);
    });

    [Fact]
    public Task ChoosingAWindowSurfaceSizesItToTheLargestComposition() => ShellFixture.WithShell(shell =>
    {
        shell.Audio.Audition.SurfaceIndex = (int)AuditionSurface.Window;

        Assert.Equal(AuditionSurface.Window, shell.Project.Audition.Surface);

        var composition = shell.Project.Compositions[0];

        // The monitor should not be smaller than the thing it is monitoring.
        Assert.Contains(
            $"{composition.Width}×{composition.Height}",
            shell.Audio.Audition.Size,
            StringComparison.Ordinal);
    });

    [Fact]
    public Task PreviewDoesNothingWithoutAnEngine() => ShellFixture.WithShell(shell =>
    {
        ShellFixture.Select(shell.Cues, ShellFixture.Bed(shell.Project).Id);

        // No session, so nothing to audition through - and pressing it must be a no-op rather than a
        // crash, because the editor is fully usable on a laptop with no rig at all.
        shell.Cues.PreviewSelected();

        Assert.False(shell.Cues.IsPreviewing);
        Assert.Equal("", shell.Cues.PreviewHint);
    });

    [Fact]
    public Task AnIntactRigPassesTheStatusCheck() => ShellFixture.WithShell(shell =>
    {
        shell.Audio.Audition.DeviceIndex = 1;

        var check = Assert.Single(
            ProjectStatus.Run(shell.Project).Checks,
            item => item.Name == "Audition rig");

        Assert.Equal(CheckOutcome.Passed, check.Outcome);
        // The row reports the WIDTH, so an operator reading it learns what auditioning will sound like.
        Assert.Contains("ch", check.Detail, StringComparison.Ordinal);
    });

    [Fact]
    public Task ARigNamingAMissingLineWarnsRatherThanFails() => ShellFixture.WithShell(shell =>
    {
        shell.Project.Audition.AudioLineId = Guid.NewGuid();

        var check = Assert.Single(
            ProjectStatus.Run(shell.Project).Checks,
            item => item.Name == "Audition rig");

        // A warning, never an error: preview falls back to the default monitor terminal and the
        // audience hears nothing different either way. This must not be what blocks a get-in.
        Assert.Equal(CheckOutcome.Warning, check.Outcome);
        Assert.Contains("falls back", check.Issues[0].Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheRigTravelsInTheProjectFile() => ShellFixture.WithShell(shell =>
    {
        shell.Audio.Audition.DeviceIndex = 1;
        shell.Audio.Audition.SurfaceIndex = (int)AuditionSurface.Window;
        shell.Audio.Audition.Level = "-3";

        var reloaded = HaCue2.Core.Serialization.HaCueProjectFile.Deserialize(
            HaCue2.Core.Serialization.HaCueProjectFile.Serialize(shell.Project));

        // Register item 14's rule for every other line applies here too: the rig travels with the show
        // and goes absent elsewhere, rather than being a machine preference.
        Assert.Equal(shell.Project.Audition.AudioLineId, reloaded.Audition.AudioLineId);
        Assert.Equal(AuditionSurface.Window, reloaded.Audition.Surface);
        Assert.Equal(-3, reloaded.Audition.LevelDb, 3);
    });
}
