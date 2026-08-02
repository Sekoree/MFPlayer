using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Compiling a project into the engine's document.
/// </summary>
/// <remarks>
/// The load-bearing test is <see cref="TheEngineAcceptsWhatTheCompilerProduces"/>: the engine's own
/// validator is the contract, and anything it rejects is a show that will not open. Everything else
/// here pins a mapping decision that a compile cannot check.
/// </remarks>
public sealed class ShowCompilerTests
{
    [Fact]
    public void TheEngineAcceptsWhatTheCompilerProduces()
    {
        var document = ShowCompiler.Compile(new TestProject().Project);

        // Errors only. A warning is for a status panel; an error means the session refuses to load.
        var errors = ShowDocumentValidator.Validate(document)
            .Where(issue => issue.Severity == ShowValidationSeverity.Error)
            .ToList();

        Assert.Empty(errors);
        ShowDocumentValidator.ThrowIfInvalid(document);
    }

    [Fact]
    public void ItRoundTripsThroughTheDocumentsOwnJson()
    {
        var document = ShowCompiler.Compile(new TestProject().Project);

        // The sidecar's real consumer is the C ABI host, so the shape has to survive the document's
        // own source-generated serializer rather than merely existing in memory.
        var reloaded = ShowDocument.FromJson(document.ToJson());

        Assert.Equal(document.Cues.Count, reloaded.Cues.Count);
        Assert.Equal(document.Clips.Count, reloaded.Clips.Count);
        Assert.Equal(document.Compositions.Count, reloaded.Compositions.Count);
    }

    [Fact]
    public void OnlyPlayableCuesReachTheDocument()
    {
        var fixture = new TestProject();
        var document = ShowCompiler.Compile(fixture.Project);
        var ids = document.Cues.Select(cue => cue.Id).ToHashSet();

        // Media is playable; a jump and a fade are decisions the transport layer makes, and putting
        // them in the document would give the engine cues it has no way to execute.
        Assert.Contains(fixture.Track.Id.ToString(), ids);
        Assert.DoesNotContain(fixture.Jump.Id.ToString(), ids);
        Assert.DoesNotContain(fixture.Fade.Id.ToString(), ids);
    }

    [Fact]
    public void CueNumbersAreDenseAndFollowListOrder()
    {
        var fixture = new TestProject();
        fixture.List.Cues.Insert(0, new CommentCueNode { Number = "0.5", Label = "note" });
        fixture.List.Cues.Add(new MediaCueNode { Number = "99", Label = "last", MediaPath = "b.wav" });

        var numbers = ShowCompiler.Compile(fixture.Project).Cues.Select(cue => cue.Number).ToList();

        // Dense from 1, in the order the tree shows — the engine's Number is a POSITION, not the
        // dotted number the operator calls. A comment contributes nothing, so it leaves no gap.
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    [Fact]
    public void ADisabledCueIsStillEmitted()
    {
        var fixture = new TestProject();
        fixture.Track.Enabled = false;

        var cue = ShowCompiler.Compile(fixture.Project).Cues
            .Single(candidate => candidate.Id == fixture.Track.Id.ToString());

        // Dropping it would renumber everything after it, so re-enabling mid-show would shift the
        // running order underneath the operator.
        Assert.False(cue.Enabled);
    }

    [Fact]
    public void ACuesSendsBecomeLogicalSendsWithTheirIds()
    {
        var fixture = new TestProject();
        var clip = ShowCompiler.Compile(fixture.Project).Clips
            .Single(candidate => candidate.ClipId == fixture.Track.Id.ToString());

        Assert.NotNull(clip.LogicalSends);
        Assert.All(clip.LogicalSends!, send => Assert.NotEqual(Guid.Empty.ToString(), send.LogicalChannelId));

        // Ids, not indices: a send has to survive somebody reordering the logical channels.
        var expected = fixture.Track.Sends.Select(send => send.LogicalChannelId.ToString()).ToHashSet();
        Assert.All(clip.LogicalSends!, send => Assert.Contains(send.LogicalChannelId, expected));
    }

    [Fact]
    public void AMutedSendIsSilentRatherThanAbsent()
    {
        var fixture = new TestProject();
        fixture.Track.Sends[0].Muted = true;

        var clip = ShowCompiler.Compile(fixture.Project).Clips
            .Single(candidate => candidate.ClipId == fixture.Track.Id.ToString());

        // Emitted at zero, so muting reads as a level the operator can see and undo rather than as a
        // route that vanished from the document.
        Assert.Equal(fixture.Track.Sends.Count, clip.LogicalSends!.Count);
        Assert.Equal(0f, clip.LogicalSends![0].Gain);
    }

    [Fact]
    public void TheCueLevelIsFoldedIntoItsSends()
    {
        var fixture = new TestProject();
        fixture.Track.LevelDb = -6;
        fixture.Track.Sends[0].GainDb = -6;

        var clip = ShowCompiler.Compile(fixture.Project).Clips
            .Single(candidate => candidate.ClipId == fixture.Track.Id.ToString());

        // −6 dB twice is −12 dB, because the two are gain stages in series and dB add.
        Assert.Equal(Math.Pow(10, -12 / 20d), clip.LogicalSends![0].Gain, 4);
    }

    [Fact]
    public void EachCueListIsItsOwnTransportGroup()
    {
        var fixture = new TestProject();
        var document = ShowCompiler.Compile(fixture.Project);

        var expected = ShowCompiler.GroupId(fixture.List);
        Assert.All(document.Cues, cue => Assert.StartsWith(expected[..8], cue.GroupId!, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyPatchedLinesBecomeAudioOutputs()
    {
        var fixture = new TestProject();
        var unused = new AudioLineDefinition { Name = "spare", DeviceHint = "nothing" };
        fixture.Project.AudioLines.Add(unused);

        var outputs = ShowCompiler.Compile(fixture.Project).AudioOutputs;

        // Opening a device to send it silence takes it from whatever else on the machine wants it.
        Assert.DoesNotContain(outputs, output => output.Id == unused.Id.ToString());
        Assert.NotEmpty(outputs);
    }

    [Fact]
    public void ACueWithNoMediaYetKeepsItsPlaceAndDoesNotBreakTheShow()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath = "";

        var document = ShowCompiler.Compile(fixture.Project);

        // The cue survives, so numbering and order stay stable while a show is being built...
        Assert.Contains(document.Cues, cue => cue.Id == fixture.Track.Id.ToString());
        // ...but no clip, because an empty path makes the engine refuse the WHOLE document — one
        // unfinished cue would stop the show loading in the middle of a rehearsal.
        Assert.DoesNotContain(document.Clips, clip => clip.ClipId == fixture.Track.Id.ToString());
        ShowDocumentValidator.ThrowIfInvalid(document);

        // It is reported by name at the project level instead, where it can be explained.
        Assert.Contains(
            Validation.ProjectValidator.Validate(fixture.Project),
            issue => issue.Message.Contains("no media file yet"));
    }

    [Fact]
    public void AnEmptyProjectCompilesToAnEmptyShow()
    {
        var document = ShowCompiler.Compile(new HaCueProject());

        Assert.Empty(document.Cues);
        Assert.Empty(document.Clips);
        ShowDocumentValidator.ThrowIfInvalid(document);
    }
}
