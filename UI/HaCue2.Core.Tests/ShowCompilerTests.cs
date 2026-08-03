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
    public void EveryCueIsAddressableButOnlyPlayableOnesGetClips()
    {
        var fixture = new TestProject();
        var document = ShowCompiler.Compile(fixture.Project);
        var cues = document.Cues.Select(cue => cue.Id).ToHashSet();
        var clips = document.Clips.Select(clip => clip.ClipId).ToHashSet();

        // EVERY cue gets a CueDefinition, because the session owns the GO cursor and refuses to stand
        // on an id it does not know: a jump absent from the document could never be made standby, and
        // GO would step straight over it as though it were not in the show.
        Assert.Contains(fixture.Track.Id.ToString(), cues);
        Assert.Contains(fixture.Jump.Id.ToString(), cues);
        Assert.Contains(fixture.Fade.Id.ToString(), cues);

        // Only media has anything to PLAY. A jump and a fade are decisions the transport layer makes,
        // and a clip for either would give the engine something it has no way to open.
        Assert.Contains(fixture.Track.Id.ToString(), clips);
        Assert.DoesNotContain(fixture.Jump.Id.ToString(), clips);
        Assert.DoesNotContain(fixture.Fade.Id.ToString(), clips);
    }

    [Fact]
    public void CueNumbersAreDenseAndFollowListOrder()
    {
        var fixture = new TestProject();
        fixture.List.Cues.Insert(0, new CommentCueNode { Number = "0.5", Label = "note" });
        fixture.List.Cues.Add(new MediaCueNode { Number = "99", Label = "last", MediaPath = "b.wav" });

        var numbers = ShowCompiler.Compile(fixture.Project).Cues.Select(cue => cue.Number).ToList();

        // Dense from 1, in the order the tree shows — the engine's Number is a POSITION, not the
        // dotted number the operator calls. Every cue counts, including the comment: the cursor can
        // stand on one, so it occupies a position like anything else.
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

    [Fact]
    public void AGroupIsItsOwnCueSoTheCursorCanStandOnIt()
    {
        var fixture = new TestProject();
        var group = new GroupCueNode { Number = "4", Label = "Storm", FireMode = GroupFireMode.AllTogether };
        group.Children.Add(new MediaCueNode { Number = "4.1", Label = "Rain", MediaPath = "rain.wav" });
        fixture.List.Cues.Add(group);

        var document = ShowCompiler.Compile(fixture.Project);

        // Both the group and its child. The group carries no clip — what firing it MEANS is the fire
        // mode's business and only the app can resolve that — but it has to be addressable, or standby
        // could never sit on it and GO would silently skip the whole group.
        Assert.Contains(document.Cues, cue => cue.Id == group.Id.ToString());
        Assert.DoesNotContain(document.Clips, clip => clip.ClipId == group.Id.ToString());
        Assert.Contains(document.Clips, clip => clip.ClipId == group.Children[0].Id.ToString());
        ShowDocumentValidator.ThrowIfInvalid(document);
    }

    [Fact]
    public void AnEffectLaneOnAnUntrimmedCueCompilesOnceTheFileHasBeenProbed()
    {
        var fixture = new TestProject();
        fixture.Track.EffectLanes.Add(new EffectLane
        {
            Kind = EffectLaneKind.Volume,
            Points = [new LanePoint(0, 1), new LanePoint(1, 0)],
        });

        // TrimOutMs is zero on every untrimmed cue, so keying the lane's length off the trim window
        // alone silently dropped it from the commonest case in the app.
        var blind = ShowCompiler.Compile(fixture.Project);
        Assert.Null(Clip(blind, fixture.Track).VolumeEnvelope);

        var probed = ShowCompiler.Compile(
            fixture.Project,
            new Dictionary<Guid, TimeSpan> { [fixture.Track.Id] = TimeSpan.FromSeconds(30) });

        var envelope = Clip(probed, fixture.Track).VolumeEnvelope;
        Assert.NotNull(envelope);
        Assert.Equal(TimeSpan.Zero, envelope[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(30), envelope[^1].Time);
    }

    [Fact]
    public void AnOutPointBecomesAnEndOffsetCountedBackFromTheFilesEnd()
    {
        var fixture = new TestProject();
        fixture.Track.TrimInMs = 2_000;
        fixture.Track.TrimOutMs = 20_000;

        var durations = new Dictionary<Guid, TimeSpan> { [fixture.Track.Id] = TimeSpan.FromSeconds(30) };
        var clip = Clip(ShowCompiler.Compile(fixture.Project, durations), fixture.Track);

        Assert.Equal(TimeSpan.FromSeconds(2), clip.StartOffset);
        // The document counts the out-point back from the END: 30 s long, out at 20 s, so 10 s remain.
        Assert.Equal(TimeSpan.FromSeconds(10), clip.EndOffset);
    }

    [Fact]
    public void AnOutPointIsNotGuessedBeforeTheFileHasBeenProbed()
    {
        var fixture = new TestProject();
        fixture.Track.TrimOutMs = 20_000;

        // Without a length there is no honest conversion, and a guessed one would cut the cue
        // somewhere nobody chose. Zero is "play through", which is the safe reading.
        Assert.Equal(TimeSpan.Zero, Clip(ShowCompiler.Compile(fixture.Project), fixture.Track).EndOffset);
    }

    private static ShowClipBinding Clip(ShowDocument document, CueNode cue) =>
        document.Clips.Single(clip => clip.ClipId == cue.Id.ToString());

    [Fact]
    public void EveryChildOfATimelineGroupGetsItsOwnTransportSoTheyLayer()
    {
        var bed = new MediaCueNode { Number = "1.1", Label = "Bed", MediaPath = "bed.wav" };
        var stab = new MediaCueNode
        {
            Number = "1.2", Label = "Stab", MediaPath = "stab.wav", TimelineOffsetMs = 5_000,
        };

        var group = new GroupCueNode
        {
            Number = "1", Label = "Opening", FireMode = GroupFireMode.Timeline,
            Children = [bed, stab],
        };

        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [group] }] };
        var document = ShowCompiler.Compile(project);

        var bedGroup = document.Cues.First(cue => cue.Id == bed.Id.ToString()).GroupId;
        var stabGroup = document.Cues.First(cue => cue.Id == stab.Id.ToString()).GroupId;

        // A session group holds ONE active voice: firing a second cue into it RELEASES the first. That
        // is right for a playlist and fatal for a timeline — a stab at five seconds would hard-cut the
        // bed underneath it. One transport each is what lets them overlap.
        Assert.NotEqual(bedGroup, stabGroup);
        Assert.Contains(group.Id.ToString("N"), bedGroup, StringComparison.Ordinal);
        Assert.Contains(group.Id.ToString("N"), stabGroup, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaylistsChildrenStillShareOneTransportSoTheyReplaceEachOther()
    {
        var first = new MediaCueNode { Number = "1.1", Label = "Track 1", MediaPath = "a.wav" };
        var second = new MediaCueNode { Number = "1.2", Label = "Track 2", MediaPath = "b.wav" };

        var group = new GroupCueNode
        {
            Number = "1", Label = "Interval", FireMode = GroupFireMode.Playlist,
            Children = [first, second],
        };

        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [group] }] };
        var document = ShowCompiler.Compile(project);

        // The whole point of a playlist is that items REPLACE one another, which is exactly what one
        // shared transport does. Splitting these would leave a playlist playing all of itself at once.
        Assert.Equal(
            document.Cues.First(cue => cue.Id == first.Id.ToString()).GroupId,
            document.Cues.First(cue => cue.Id == second.Id.ToString()).GroupId);
    }

    [Fact]
    public void AGroupNESTEDInATimelineKeepsItsOwnChildrenTogether()
    {
        var inner = new MediaCueNode { Number = "1.1.1", Label = "A", MediaPath = "a.wav" };
        var alsoInner = new MediaCueNode { Number = "1.1.2", Label = "B", MediaPath = "b.wav" };

        var nested = new GroupCueNode
        {
            Number = "1.1", Label = "Interval", FireMode = GroupFireMode.Playlist,
            Children = [inner, alsoInner],
        };

        var bed = new MediaCueNode { Number = "1.2", Label = "Bed", MediaPath = "bed.wav" };

        var timeline = new GroupCueNode
        {
            Number = "1", Label = "Opening", FireMode = GroupFireMode.Timeline,
            Children = [nested, bed],
        };

        var project = new HaCueProject { CueLists = [new CueList { Name = "Act 1", Cues = [timeline] }] };
        var document = ShowCompiler.Compile(project);

        string Group(CueNode cue) =>
            document.Cues.First(item => item.Id == cue.Id.ToString()).GroupId ?? "";

        // The nested playlist is one LAYER of the timeline, so it gets a transport of its own — and
        // its own children share it, because within the playlist they still replace each other.
        Assert.NotEqual(Group(nested), Group(bed));
        Assert.Equal(Group(inner), Group(alsoInner));
        Assert.NotEqual(Group(inner), Group(bed));
    }
}
