using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class StatusTests
{
    [Fact]
    public void AProjectWithNoMediaAndNoDevicesToCheckStillReports()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.MediaPath = "";

        var report = ProjectStatus.Run(fixture.Project);

        Assert.Equal(0, report.ExitCode);
        Assert.NotEmpty(report.Checks);
    }

    [Fact]
    public void MissingMediaIsAnErrorAndNamesTheCue()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.MediaPath = "/media/usb0/interval.wav";
        var report = ProjectStatus.Run(fixture.Project, environment: new FakeEnvironment());

        var check = Assert.Single(report.Checks, c => c.Name == "Media files");
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("interval.wav", check.Issues[0].Message);
        Assert.Equal("cue", check.Issues[0].SubjectKind);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void PresentMediaPasses()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.MediaPath = "/media/ok.wav";
        var environment = new FakeEnvironment { Files = { "/media/ok.wav" } };

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        Assert.Equal(CheckOutcome.Passed, Assert.Single(report.Checks, c => c.Name == "Media files").Outcome);
    }

    /// <summary>
    /// The honesty property: a machine that could not enumerate devices says so rather than showing a
    /// green row nobody verified.
    /// </summary>
    [Fact]
    public void DevicesNobodyCouldEnumerateReportAsNotCheckedRatherThanOk()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.MediaPath = "";

        var report = ProjectStatus.Run(fixture.Project);

        var check = Assert.Single(report.Checks, c => c.Name == "Audio devices");
        Assert.Equal(CheckOutcome.NotChecked, check.Outcome);
        Assert.Contains("not enumerated", check.Detail);
        // And it does not fail the run: unchecked is not the same as broken.
        Assert.Equal(0, report.Errors);
    }

    [Fact]
    public void AnAbsentOptionalLineIsAWarning()
    {
        var fixture = new TestProject().WithFoldbackFed();
        var environment = new FakeEnvironment
        {
            Files = { "preshow-loop.wav" },
            AudioLines = { [fixture.Interface.Id] = DeviceAvailability.Present,
                           [fixture.Wedge.Id] = DeviceAvailability.Absent },
        };

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        var check = Assert.Single(report.Checks, c => c.Name == "Audio devices");
        Assert.Equal(CheckOutcome.Warning, check.Outcome);
        // An absent OPTIONAL line does not stop the show, so the run still exits clean.
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>Register item 25: the flag is inverted — required-and-absent is an error.</summary>
    [Fact]
    public void AnAbsentRequiredLineIsAnError()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Wedge.Required = true;
        var environment = new FakeEnvironment
        {
            AudioLines = { [fixture.Interface.Id] = DeviceAvailability.Present,
                           [fixture.Wedge.Id] = DeviceAvailability.Absent },
        };

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        var check = Assert.Single(report.Checks, c => c.Name == "Audio devices");
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("marked required", check.Issues[0].Message);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void AnAbsentLineSaysHowManyPatchCellsGoSilent()
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Project.AudioPatch.Cells.Add(new PatchCell
        {
            LogicalChannelId = fixture.MainL.Id, LineId = fixture.Wedge.Id, LineChannel = 0,
        });
        var environment = new FakeEnvironment
        {
            AudioLines = { [fixture.Interface.Id] = DeviceAvailability.Present,
                           [fixture.Wedge.Id] = DeviceAvailability.Absent },
        };

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        Assert.Contains("1 patch cell silent",
            Assert.Single(report.Checks, c => c.Name == "Audio devices").Issues[0].Message);
    }

    [Fact]
    public void AbsentVideoOutputsFollowTheSameRequiredRule()
    {
        var fixture = new TestProject().WithFoldbackFed();
        var lobby = new VideoOutputDefinition { Name = "Lobby TV", Required = true };
        fixture.Project.VideoOutputs.Add(lobby);
        var environment = new FakeEnvironment
        {
            VideoOutputs = { [lobby.Id] = DeviceAvailability.Absent },
        };

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        Assert.Equal(CheckOutcome.Failed,
            Assert.Single(report.Checks, c => c.Name == "Video outputs").Outcome);
    }

    [Fact]
    public void DocumentProblemsLandInTheCheckForTheirSubject()
    {
        var fixture = new TestProject();
        var lobby = new LogicalAudioChannel { Name = "Lobby" };
        fixture.Project.AudioPatch.LogicalChannels.Add(lobby);
        fixture.Track.Sends.Add(new CueAudioSend { SourceChannel = 0, LogicalChannelId = lobby.Id });

        var report = ProjectStatus.Run(fixture.Project);

        var check = Assert.Single(report.Checks, c => c.Name == "Logical outputs");
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains(check.Issues, issue => issue.Message.Contains("patched to nothing"));
    }

    [Fact]
    public void TheTextReportNamesEveryFailingCheck()
    {
        var fixture = new TestProject();
        fixture.Jump.TargetCueIds.Clear();

        var text = ProjectStatus.Run(fixture.Project).ToText();

        Assert.Contains("[FAIL] Cues", text);
        Assert.Contains("jump with no target", text);
    }

    [Fact]
    public void TheJsonReportCarriesTheOutcomesAsNames()
    {
        var json = ProjectStatus.Run(new TestProject().Project).ToJson();

        Assert.Contains("\"outcome\": \"", json);
        Assert.Contains("\"exitCode\"", json);
    }

    /// <summary>
    /// A report pasted from a comma-decimal machine must not read as a different number.
    /// </summary>
    [Fact]
    public void TheReportIsInvariantFormattedWhateverTheCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            var report = new ProjectStatusReport([], 0.4);

            Assert.Contains("0.4 s", report.Summary);
            Assert.DoesNotContain("0,4", report.Summary);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    private sealed class FakeEnvironment : IProjectEnvironment
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, DeviceAvailability> AudioLines { get; } = [];
        public Dictionary<Guid, DeviceAvailability> VideoOutputs { get; } = [];

        public bool MediaExists(string resolvedPath) => Files.Contains(resolvedPath);

        public DeviceAvailability AudioLine(AudioLineDefinition line) =>
            AudioLines.GetValueOrDefault(line.Id, DeviceAvailability.Present);

        public DeviceAvailability VideoOutput(VideoOutputDefinition output) =>
            VideoOutputs.GetValueOrDefault(output.Id, DeviceAvailability.Present);
    }
}
