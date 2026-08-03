using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Recordings, checked at the get-in rather than at arm time.
/// </summary>
/// <remarks>
/// The moment an operator arms is the moment they have least room to fix anything. A pattern that
/// cannot be written is a five-second edit hours beforehand and a lost recording on the night, so the
/// check belongs in Project status where the rest of the "will tonight work" questions are answered.
/// </remarks>
public class RecordingStatusTests
{
    private static StatusCheck Recordings(HaCueProject project) =>
        ProjectStatus.Run(project, environment: new NoEnvironment()).Checks.Single(check => check.Name == "Recordings");

    private static HaCueProject WithOutput(VideoOutputKind kind, RecordTarget? target, bool required = false) =>
        new()
        {
            VideoOutputs =
            [
                new VideoOutputDefinition { Name = "Capture", Kind = kind, Record = target, Required = required },
            ],
        };

    [Fact]
    public void AShowWithNoRecordersPasses()
    {
        Assert.Equal(CheckOutcome.Passed, Recordings(new HaCueProject()).Outcome);
    }

    [Fact]
    public void AWritablePatternPasses()
    {
        var project = WithOutput(VideoOutputKind.Record, new RecordTarget { Pattern = "show-{date}.mkv" });

        Assert.Equal(CheckOutcome.Passed, Recordings(project).Outcome);
    }

    [Fact]
    public void AnUnwritableExtensionIsReportedWithItsAlternative()
    {
        var project = WithOutput(VideoOutputKind.Record, new RecordTarget { Pattern = "show.flac" });

        var check = Recordings(project);

        Assert.Equal(CheckOutcome.Warning, check.Outcome);
        Assert.Contains(".mkv", check.Issues.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredTargetThatCannotWriteIsAnError()
    {
        // Register item 25: a rig whose whole purpose is capturing the performance says so, and then a
        // recording that will not arm is a reason to stop rather than a note to read later.
        var project = WithOutput(VideoOutputKind.Record, new RecordTarget { Pattern = "show.flac" }, required: true);

        Assert.Equal(CheckOutcome.Failed, Recordings(project).Outcome);
    }

    [Fact]
    public void ATargetWithNowhereToWriteIsReported()
    {
        Assert.Equal(CheckOutcome.Warning, Recordings(WithOutput(VideoOutputKind.Record, null)).Outcome);
    }

    [Fact]
    public void AStreamWithNoUrlIsReported()
    {
        var project = WithOutput(VideoOutputKind.Stream, new RecordTarget());

        Assert.Contains("URL", Recordings(project).Issues.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamIsNotJudgedOnItsPattern()
    {
        // A stream's container follows its protocol — RTMP takes FLV whatever the pattern says — so the
        // extension is not a thing to fail it on.
        var project = WithOutput(
            VideoOutputKind.Stream, new RecordTarget { Url = "rtmp://example/app/key", Pattern = "ignored.flac" });

        Assert.Equal(CheckOutcome.Passed, Recordings(project).Outcome);
    }

    [Fact]
    public void AnAudioLineIsJudgedOnAudioRules()
    {
        // .mka carries no picture, which is exactly right for a record LINE and wrong for an output.
        var project = new HaCueProject
        {
            AudioLines =
            [
                new AudioLineDefinition
                {
                    Name = "Archive",
                    Kind = AudioLineKind.FileRecord,
                    Record = new RecordTarget { Pattern = "archive-{date}.mka" },
                },
            ],
        };

        Assert.Equal(CheckOutcome.Passed, Recordings(project).Outcome);
    }

    [Fact]
    public void OrdinaryLinesAndScreensAreNotRecordingTargets()
    {
        var project = new HaCueProject
        {
            AudioLines = [new AudioLineDefinition { Name = "Main", Kind = AudioLineKind.LocalAudio }],
            VideoOutputs = [new VideoOutputDefinition { Name = "Projector", Kind = VideoOutputKind.LocalScreen }],
        };

        Assert.Equal(CheckOutcome.Passed, Recordings(project).Outcome);
        Assert.Contains("none", Recordings(project).Detail, StringComparison.Ordinal);
    }

    /// <summary>A machine that answers nothing, so only the document's own rules are under test.</summary>
    private sealed class NoEnvironment : IProjectEnvironment
    {
        public DeviceAvailability AudioLine(AudioLineDefinition line) => DeviceAvailability.Unknown;

        public DeviceAvailability VideoOutput(VideoOutputDefinition output) => DeviceAvailability.Unknown;

        public bool MediaExists(string resolvedPath) => true;
    }
}
