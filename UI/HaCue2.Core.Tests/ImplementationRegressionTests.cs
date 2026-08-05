using HaCue2.Core.Compile;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using HaCue2.Engine;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class ImplementationRegressionTests
{
    [Fact]
    public void CompileContextResolvesMediaSubtitlesAndCheckedStreamSelections()
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Film",
            MediaPath = "film.mp4",
            Subtitles = [new SubtitleSelection { Path = "captions/show.srt" }],
        };
        var project = new HaCueProject
        {
            Settings = new ProjectSettings { MediaRoot = "media" },
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };
        var projectPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "shows", "show.hacue2proj");
        var document = ShowCompiler.Compile(project, new ShowCompileContext
        {
            ProjectPath = projectPath,
            Tracks = new Dictionary<Guid, ResolvedMediaTracks>
            {
                [cue.Id] = new(4, -1, [7]),
            },
        });

        // Path.GetFullPath, because that is what the compiler resolves media through — and on Windows a
        // path rooted at the separator alone is relative to the CURRENT DRIVE, so "\shows\media\film.mp4"
        // comes back as "D:\shows\media\film.mp4". Comparing against the unrooted spelling failed there
        // while passing on Linux, where the two are the same string.
        var clip = Assert.Single(document.Clips);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "shows", "media", "film.mp4")),
            clip.MediaPath);
        Assert.Equal(4, clip.AudioStreamIndex);
        Assert.Equal(-1, clip.VideoStreamIndex);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                Path.DirectorySeparatorChar.ToString(), "shows", "media", "captions", "show.srt")),
            Assert.Single(clip.GetSubtitleSelections()).Path);
        Assert.Equal(7, Assert.Single(clip.GetSubtitleSelections()).StreamIndex);
    }

    [Fact]
    public void TheCompilerLeavesWaitAndFollowSemanticsToTheExecutor()
    {
        var fixture = new TestProject();
        fixture.Track.PreWaitMs = 500;
        fixture.Track.PostWaitMs = 750;
        fixture.Track.Trigger = CueTrigger.Continue;

        var compiled = ShowCompiler.Compile(fixture.Project).Cues
            .Single(cue => cue.Id == fixture.Track.Id.ToString());

        Assert.Equal(TimeSpan.Zero, compiled.PreWait);
        Assert.Equal(TimeSpan.Zero, compiled.PostWait);
        Assert.False(compiled.AutoContinue);
    }

    [Fact]
    public void ACheckedMissingStreamDoesNotFallBackToAStaleIndex()
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Changed file",
            MediaPath = "changed.mov",
            AudioTrackIndex = 4,
            AudioTrackSignature = "audio-that-used-to-exist",
            VideoTrackIndex = 2,
            VideoTrackSignature = "video-that-used-to-exist",
        };
        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };

        var compiled = ShowCompiler.Compile(project, new ShowCompileContext
        {
            Tracks = new Dictionary<Guid, ResolvedMediaTracks>
            {
                [cue.Id] = new(null, null, []),
            },
        });

        var clip = Assert.Single(compiled.Clips);
        Assert.Null(clip.AudioStreamIndex);
        Assert.Null(clip.VideoStreamIndex);
    }

    [Fact]
    public void RuntimeSnapshotsAreDetachedFromTheEditableProject()
    {
        var fixture = new TestProject();
        var snapshot = ProjectSnapshot.Copy(fixture.Project);

        snapshot.Title = "runtime";
        ((MediaCueNode)snapshot.FindCue(fixture.Track.Id)!).Label = "runtime cue";
        snapshot.AudioPatch.Cells[0].GainDb = -20;

        Assert.Equal("midsummer-2026", fixture.Project.Title);
        Assert.Equal("Preshow bed", fixture.Track.Label);
        Assert.Equal(0, fixture.Project.AudioPatch.Cells[0].GainDb);
    }

    [Fact]
    public void SidecarSubtitlesParticipateInMediaOperations()
    {
        var fixture = new TestProject();
        fixture.Track.Subtitles.Add(new SubtitleSelection { Path = "captions.srt" });

        var reference = Assert.Single(
            MediaPaths.ReferencesIn(fixture.Project), item => item.SubjectKind == "subtitle");

        Assert.Equal(fixture.Track.Id.ToString(), reference.SubjectId);
        Assert.Equal(0, reference.Slot);
    }

    [Fact]
    public void YouTubeCaptionsAreMachineDerived_NotAStaleCachePathInTheProject()
    {
        var fixture = new TestProject();
        fixture.Track.MediaPath =
            "youtube://dQw4w9WgXcQ?v=1080p%7Cavc1%7Cmp4&a=opus%7Cwebm%7Cen&sub=en";
        fixture.Track.Subtitles = [new SubtitleSelection { Path = "/old-machine/cache/caption.ass" }];
        const string preparedHere = "/this-machine/cache/caption.ass";

        Assert.DoesNotContain(
            MediaPaths.ReferencesIn(fixture.Project),
            reference => reference.SubjectId == fixture.Track.Id.ToString() && reference.SubjectKind == "subtitle");

        var clip = Assert.Single(ShowCompiler.Compile(fixture.Project, new ShowCompileContext
        {
            PreparedSubtitlePaths = new Dictionary<Guid, string> { [fixture.Track.Id] = preparedHere },
        }).Clips);
        var subtitle = Assert.Single(clip.GetSubtitleSelections());

        Assert.Equal(preparedHere, subtitle.Path);
        Assert.Equal(-1, subtitle.StreamIndex);

        // A normal external sidecar remains a normal portable media reference when the YouTube URI
        // itself did not request generated captions.
        fixture.Track.MediaPath = "youtube://dQw4w9WgXcQ?v=1080p%7Cavc1%7Cmp4&a=opus%7Cwebm%7Cen";
        Assert.Contains(
            MediaPaths.ReferencesIn(fixture.Project),
            reference => reference.Path == "/old-machine/cache/caption.ass");
    }

    [Fact]
    public void ValidatorRejectsNonFiniteCurvesLanesAndMapping()
    {
        var fixture = new TestProject();
        fixture.Track.FadeInCurve.Points =
        [
            new FadeCurvePoint(0, 0),
            new FadeCurvePoint(1, double.NaN),
        ];
        fixture.Track.EffectLanes.Add(new EffectLane
        {
            Kind = EffectLaneKind.Volume,
            Points = [new LanePoint(0, 1), new LanePoint(double.PositiveInfinity, 0)],
        });
        fixture.Project.VideoOutputs.Add(new VideoOutputDefinition
        {
            Name = "Projector",
            CompositionId = fixture.Cyc.Id,
            Mapping = [new MappingSection { Name = "Bad", TargetWidth = double.NaN }],
        });

        var issues = ProjectValidator.Validate(fixture.Project);

        Assert.Contains(issues, issue => issue.Message.Contains("curve has a point outside", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("lane point outside", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("non-finite number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CountedAndExternalOnlyJumpsHonorTheirConditions()
    {
        var target = new MediaCueNode { Number = "1", Label = "target", MediaPath = "target.wav" };
        var counted = new JumpCueNode
        {
            Number = "2", Label = "counted", TargetCueIds = [target.Id],
            Condition = JumpCondition.CountThenContinue, JumpCount = 2,
        };
        var external = new JumpCueNode
        {
            Number = "3", Label = "external", TargetCueIds = [target.Id],
            Condition = JumpCondition.WhileTriggerHeld,
        };
        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [target, counted, external] }],
        };
        var host = new FakeCueHost(project);
        var executor = new CueExecutor(host);

        await executor.FireAsync(counted.Id);
        await executor.FireAsync(counted.Id);
        await executor.FireAsync(counted.Id);
        Assert.Equal(2, host.Played.Count(id => id == target.Id));

        await executor.FireAsync(external.Id);
        Assert.Equal(2, host.Played.Count(id => id == target.Id));
        host.IsExternalTriggerActive = true;
        await executor.FireAsync(external.Id);
        Assert.Equal(3, host.Played.Count(id => id == target.Id));
    }

    [Fact]
    public async Task RemoteDispatchUsesTheTransportSeamEndToEnd()
    {
        var cue = new MediaCueNode { Number = "1", Label = "Cue", MediaPath = "cue.wav" };
        var list = new CueList { Name = "Main", Cues = [cue] };
        var project = new HaCueProject { CueLists = [list] };
        var transport = new FakeRemoteTransport(cue.Id);
        var server = new RemoteApiServer(transport, () => project, "secret");

        var unauthorized = await server.HandleAsync("POST", $"/api/v1/cues/{cue.Id}/go", "wrong");
        var fired = await server.HandleAsync("POST", $"/api/v1/cues/{cue.Id}/go", "secret");
        var missingStop = await server.HandleAsync("POST", $"/api/v1/cues/{Guid.NewGuid()}/stop", "secret");
        var status = await server.HandleAsync("GET", "/api/v1/status", "secret");

        Assert.Equal(401, unauthorized.Status);
        Assert.Equal(200, fired.Status);
        Assert.Equal([cue.Id], transport.Fired);
        Assert.Equal(404, missingStop.Status);
        Assert.Equal(200, status.Status);
        Assert.Contains(cue.Id.ToString(), status.Body, StringComparison.Ordinal);
    }

    private sealed class FakeRemoteTransport(Guid sounding) : IRemoteApiTransport
    {
        public List<Guid> Fired { get; } = [];
        public Guid? Previewing => null;
        public bool IsPaused { get; private set; }
        public Task<ShowState> SnapshotAsync() => Task.FromResult(new ShowState(
            new HashSet<Guid> { sounding }, new Dictionary<Guid, Guid>(), [], IsPaused, []));
        public Task<Guid?> GoAsync(CueList list) => Task.FromResult<Guid?>(list.Cues.FirstOrDefault()?.Id);
        public Task<bool> FireAsync(Guid cueId) { Fired.Add(cueId); return Task.FromResult(true); }
        public Task StopCueAsync(Guid cueId) => Task.CompletedTask;
        public Task<bool> StandbyAsync(CueList list, Guid? cueId) => Task.FromResult(true);
        public Task StopAllAsync() => Task.CompletedTask;
        public Task PanicAsync() => Task.CompletedTask;
        public Task SetPausedAsync(bool paused) { IsPaused = paused; return Task.CompletedTask; }
    }
}
