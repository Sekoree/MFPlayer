using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Reloading a document without stopping unrelated playback (HaCue2 framework gap analysis §2.1).
/// <para>
/// Hosts recompile and reload the WHOLE merged document after any structural edit, so the historical
/// unconditional <c>DisposeGroupsAsync</c> meant every edit stopped every playing cue in every list, and
/// wiped every group's GO cursor. That made "editing never blocks playback" and per-list transport
/// positions mutually exclusive with the app's normal editing loop.
/// </para>
/// <para>
/// The retention rule is deliberately strict - a group survives only when EVERY voice it holds still maps
/// to a byte-identical binding - because the failure mode of being too eager is a show that keeps playing
/// something you just edited away.
/// </para>
/// </summary>
public sealed class NonDestructiveLoadTests
{
    private sealed class ReleaseLog
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _released =
            new(StringComparer.Ordinal);

        public bool IsReleased(string deviceId) => _released.ContainsKey(deviceId);

        public ClipAudioOutputLease? BuildLease(string deviceId, S.Media.Core.Audio.AudioFormat format) =>
            new(new SinkAudioOutput(format),
                DisposeOutputOnRuntimeDispose: false,
                Release: () => _released.TryAdd(deviceId, 0));
    }

    /// <summary>A cue on an explicit group, so a document can model two independent "lists".</summary>
    private static CueDefinition Cue(string id, int number, string groupId) =>
        new(id, number, id.ToUpperInvariant()) { GroupId = groupId };

    private static ShowClipBinding Clip(string cueId, string media = "fake://a") =>
        new(cueId, media) { AudioRoutes = [new ShowClipAudioRoute(DeviceId: $"dev-{cueId}")] };

    private static ShowDocument TwoLists(string listAMedia = "fake://a", string listBMedia = "fake://b") => new(
        Version: 1,
        Cues: [Cue("a1", 1, "listA"), Cue("b1", 2, "listB")],
        Clips: [Clip("a1", listAMedia), Clip("b1", listBMedia)],
        Compositions: [], Routes: []);

    private static ShowSession BuildSession(ReleaseLog releases) => new(
        FakeAudioDecoderProvider.Registry(chunks: 100_000),
        new RecordingAudioBackend(),
        audioOutputFactory: releases.BuildLease);

    [Fact]
    public async Task WithoutOptIn_AReloadStillStopsEverything()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));
        Assert.False(releases.IsReleased("dev-b1"));

        // Default is the historical full teardown - existing hosts are unaffected by this feature.
        await session.LoadDocumentAsync(TwoLists());

        Assert.True(releases.IsReleased("dev-b1"), "the default reload must still tear the show down");
    }

    [Fact]
    public async Task EditingOneList_LeavesTheOtherListPlaying()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // The edit changes list A only; list B's binding is untouched.
        await session.LoadDocumentAsync(
            TwoLists(listAMedia: "fake://a-edited"), preserveActiveGroups: true);

        // This is the whole point: B keeps playing through an edit to A.
        Assert.False(releases.IsReleased("dev-b1"), "editing list A stopped list B");

        // And it is B specifically that is still running. The assertion names the ACTIVE group rather
        // than counting snapshot rows: an idle DefaultGroup entry may also be present, created by the
        // background pre-roll on either reload path, so its presence is a timing race and not something
        // retention introduces.
        var active = Assert.Single(session.Snapshot(), s => s.IsActive);
        Assert.Equal("listB", active.GroupId);
    }

    [Fact]
    public async Task EditingTheListThatIsPlaying_StillStopsIt()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // B's own media changed - keeping the old voice alive would play content the document no longer
        // describes, so its group is torn down exactly as before.
        await session.LoadDocumentAsync(
            TwoLists(listBMedia: "fake://b-edited"), preserveActiveGroups: true);

        Assert.True(releases.IsReleased("dev-b1"), "a changed binding must not keep playing");
    }

    [Fact]
    public async Task ARemovedCue_StopsItsGroup()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // b1 is deleted outright: nothing in the new document describes the playing voice.
        var withoutB = new ShowDocument(
            Version: 1,
            Cues: [Cue("a1", 1, "listA")],
            Clips: [Clip("a1")],
            Compositions: [], Routes: []);
        await session.LoadDocumentAsync(withoutB, preserveActiveGroups: true);

        Assert.True(releases.IsReleased("dev-b1"), "a deleted cue's voice kept playing");
    }

    [Fact]
    public async Task AChangedRouteOnThePlayingCue_StopsItsGroup()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // Same media, different output routing. Retaining here would leave the voice on the old device
        // while the document says otherwise - the list members matter, not just the scalars, which is why
        // the comparison walks them element-wise rather than trusting record equality.
        var rerouted = new ShowDocument(
            Version: 1,
            Cues: [Cue("a1", 1, "listA"), Cue("b1", 2, "listB")],
            Clips:
            [
                Clip("a1"),
                new ShowClipBinding("b1", "fake://b")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "dev-b1-elsewhere")],
                },
            ],
            Compositions: [], Routes: []);
        await session.LoadDocumentAsync(rerouted, preserveActiveGroups: true);

        Assert.True(releases.IsReleased("dev-b1"), "a re-routed cue kept its old output");
    }

    [Fact]
    public async Task AnIdenticalReload_KeepsEverythingPlaying()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("a1"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        await session.LoadDocumentAsync(TwoLists(), preserveActiveGroups: true);

        Assert.False(releases.IsReleased("dev-a1"));
        Assert.False(releases.IsReleased("dev-b1"));
        Assert.Equal(2, session.Snapshot().Count(s => s.IsActive));
    }

    [Fact]
    public async Task DeeplyEqualNestedRoutingAndAutomation_IsRetainedByValue()
    {
        static ShowDocument Document() => new(
            Version: 1,
            Cues: [Cue("b1", 1, "listB")],
            Clips:
            [
                new ShowClipBinding("b1", "fake://b")
                {
                    AudioRoutes =
                    [
                        new ShowClipAudioRoute("dev-b1", [0, 1])
                        {
                            MatrixCells = [new ShowAudioMatrixCell(0, 0, 0.5f)],
                            MatrixOutputChannels = 2,
                        },
                    ],
                    VolumeEnvelope =
                    [
                        new ShowEnvelopePoint(TimeSpan.Zero, 0.5f),
                        new ShowEnvelopePoint(TimeSpan.FromSeconds(1), 1f, FadeCurve.SCurve),
                    ],
                    OpacityEnvelope = [new ShowEnvelopePoint(TimeSpan.Zero, 0.75f)],
                },
            ],
            Compositions: [], Routes: []);

        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Document());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        await session.LoadDocumentAsync(Document(), preserveActiveGroups: true);

        Assert.False(releases.IsReleased("dev-b1"),
            "separately deserialized but value-equal nested lists restarted the voice");
    }

    [Fact]
    public async Task ChangingInheritedGlobalRoute_StopsTheVoiceWithTheStaleOutput()
    {
        static ShowDocument Document(int[] matrix) => new(
            Version: 1,
            Cues: [Cue("b1", 1, "listB")],
            Clips: [new ShowClipBinding("b1", "fake://b")], // null routes = inherit show/group patch
            Compositions: [],
            Routes: [new OutputPatchRoute("b1", "line", ChannelMatrix: matrix)])
        {
            AudioOutputs = [new ShowAudioOutput("line", "dev-inherited", "listB")],
        };

        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(Document([0, 1]));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        await session.LoadDocumentAsync(Document([1, 0]), preserveActiveGroups: true);

        Assert.True(releases.IsReleased("dev-inherited"),
            "a global patch edit retained a voice on its old route");
    }

    [Fact]
    public async Task ARetainedGroup_KeepsItsGoCursor()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        // Two cues on one list so GO has somewhere to advance to.
        var document = new ShowDocument(
            Version: 1,
            Cues: [Cue("b1", 1, "listB"), Cue("b2", 2, "listB")],
            Clips: [Clip("b1"), Clip("b2")],
            Compositions: [], Routes: []);
        await session.LoadDocumentAsync(document);

        // GO fires b1 and advances the group's cursor past it.
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync("listB"));

        await session.LoadDocumentAsync(document, preserveActiveGroups: true);

        // The cursor survived, so the next GO continues at b2 rather than restarting the list. This is the
        // per-list playhead multi-list transport needs to survive an edit.
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync("listB"));
        Assert.False(releases.IsReleased("dev-b2"), "the second GO did not reach b2");
    }

    [Fact]
    public async Task ADiscardedGroup_LosesItsGoCursor_AndRestartsTheList()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        var document = new ShowDocument(
            Version: 1,
            Cues: [Cue("b1", 1, "listB"), Cue("b2", 2, "listB")],
            Clips: [Clip("b1"), Clip("b2")],
            Compositions: [], Routes: []);
        await session.LoadDocumentAsync(document);
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync("listB"));

        // Default reload: the group and its cursor are gone, so GO starts the list again.
        await session.LoadDocumentAsync(document);
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync("listB"));

        // b1 fired twice; b2 was never reached.
        Assert.False(releases.IsReleased("dev-b2"));
    }
}
