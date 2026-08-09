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

    [Theory]
    [InlineData(PreparedSourceAvailability.Missing, CheckOutcome.Failed, "not in this machine")]
    [InlineData(PreparedSourceAvailability.Preparing, CheckOutcome.Failed, "downloading in the background")]
    [InlineData(PreparedSourceAvailability.Failed, CheckOutcome.Failed, "could not be downloaded")]
    [InlineData(PreparedSourceAvailability.Ready, CheckOutcome.Passed, "ready offline")]
    [InlineData(PreparedSourceAvailability.Unknown, CheckOutcome.NotChecked, "not checked")]
    public void YouTubeCacheReadinessIsARealPreflightCheck(
        PreparedSourceAvailability availability,
        CheckOutcome expected,
        string detail)
    {
        var fixture = new TestProject().WithFoldbackFed();
        fixture.Track.MediaPath = "youtube://dQw4w9WgXcQ?v=1080p%7Cavc1%7Cmp4&a=opus%7Cwebm%7Cen";
        var environment = new FakeEnvironment();
        environment.PreparedSources[fixture.Track.MediaPath] = availability;

        var check = Assert.Single(
            ProjectStatus.Run(fixture.Project, environment: environment).Checks,
            item => item.Name == "YouTube cache");

        Assert.Equal(expected, check.Outcome);
        Assert.Contains(detail, check.Issues.FirstOrDefault()?.Message ?? check.Detail);
        if (expected == CheckOutcome.Failed)
            Assert.Contains("Download missing", check.Fix);
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

    [Fact]
    public void AMachineThatCannotDecodeAnythingIsReportedAsAnError_NotAsAHealthyProject()
    {
        // The failure this row exists for: the decoder's natives do not load, so every probe comes back
        // empty and every cue row badges "offline" — while the files are all present, so the media check
        // passes and the status bar says "no issues" over a show that cannot play a note.
        var fixture = new TestProject().WithFoldbackFed();
        var environment = new FakeEnvironment
        {
            MediaDecodingUnavailableReason = "FFmpeg native libraries are not loadable.",
        };
        environment.Files.Add(fixture.Track.MediaPath);

        var report = ProjectStatus.Run(fixture.Project, environment: environment);

        var check = report.Checks.Single(c => c.Name == "Media decoding");
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("not loadable", check.Issues.Single().Message, StringComparison.Ordinal);
        Assert.NotEqual(0, report.ExitCode);
    }

    [Fact]
    public void AMachineThatCanDecodeSaysSoWithoutRaisingAnIssue()
    {
        var fixture = new TestProject().WithFoldbackFed();
        var environment = new FakeEnvironment();
        environment.Files.Add(fixture.Track.MediaPath);

        var check = ProjectStatus.Run(fixture.Project, environment: environment)
            .Checks.Single(c => c.Name == "Media decoding");

        Assert.Equal(CheckOutcome.Passed, check.Outcome);
        Assert.Empty(check.Issues);
    }

    private sealed class FakeEnvironment : IProjectEnvironment
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, DeviceAvailability> AudioLines { get; } = [];
        public Dictionary<Guid, DeviceAvailability> VideoOutputs { get; } = [];
        public Dictionary<string, PreparedSourceAvailability> PreparedSources { get; } =
            new(StringComparer.Ordinal);

        public bool MediaExists(string resolvedPath) => Files.Contains(resolvedPath);

        public DeviceAvailability AudioLine(AudioLineDefinition line) =>
            AudioLines.GetValueOrDefault(line.Id, DeviceAvailability.Present);

        public DeviceAvailability VideoOutput(VideoOutputDefinition output) =>
            VideoOutputs.GetValueOrDefault(output.Id, DeviceAvailability.Present);

        public PreparedSourceAvailability PreparedSource(string sourceUri) =>
            PreparedSources.GetValueOrDefault(sourceUri, PreparedSourceAvailability.Unknown);

        public string? MediaDecodingUnavailableReason { get; set; }
    }
}
