using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Asking, in advance, whether a reload would interrupt anything - <c>WouldPreservePlaybackAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// A host that reloads the whole merged document after every edit has only two blunt options without
/// this. Reload always, and any edit touching a playing cue's binding restarts it: on a group layering
/// two 1080p60 ProRes clips over eleven stems that is a pop, eleven re-opened files, and the picture
/// snapping back to its in-point - 300 ms after every drag. Or defer every reload while anything plays,
/// which is safe but leaves a cue label waiting on a fifty-minute track.
/// </para>
/// <para>
/// The forecast is the third answer, and its value depends entirely on it being the SAME rule the load
/// uses rather than an approximation. So these tests pin the prediction against what the load then
/// actually does, in both directions.
/// </para>
/// </remarks>
public sealed class PlaybackPreservationForecastTests
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

    private static CueDefinition Cue(string id, int number, string groupId, string? label = null) =>
        new(id, number, label ?? id.ToUpperInvariant()) { GroupId = groupId };

    private static ShowClipBinding Clip(string cueId, string media = "fake://a") =>
        new(cueId, media) { AudioRoutes = [new ShowClipAudioRoute(DeviceId: $"dev-{cueId}")] };

    private static ShowDocument TwoLists(
        string listAMedia = "fake://a",
        string listBMedia = "fake://b",
        string? listALabel = null) => new(
        Version: 1,
        Cues: [Cue("a1", 1, "listA", listALabel), Cue("b1", 2, "listB")],
        Clips: [Clip("a1", listAMedia), Clip("b1", listBMedia)],
        Compositions: [], Routes: []);

    private static ShowSession BuildSession(ReleaseLog releases) => new(
        FakeAudioDecoderProvider.Registry(chunks: 100_000),
        new RecordingAudioBackend(),
        audioOutputFactory: releases.BuildLease);

    [Fact]
    public async Task WithNothingPlaying_EveryReloadIsSafe()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());

        // Not "nothing changed" - a document that deletes the cue outright. There is no voice to
        // interrupt, so there is nothing to defer for.
        var gutted = new ShowDocument(
            Version: 1, Cues: [], Clips: [], Compositions: [], Routes: []);

        Assert.True(await session.WouldPreservePlaybackAsync(gutted));
    }

    [Fact]
    public async Task AnEditToAnIdleList_IsSafeWhileAnotherListPlays()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        var edited = TwoLists(listAMedia: "fake://a-edited");

        Assert.True(await session.WouldPreservePlaybackAsync(edited));

        // And the load agrees: B is still playing afterwards.
        await session.LoadDocumentAsync(edited, preserveMatchingCompositions: true, preserveActiveGroups: true);
        Assert.False(releases.IsReleased("dev-b1"));
    }

    [Fact]
    public async Task AnEditThatOnlyRenamesACue_IsSafeEvenOnThePlayingList()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // The overwhelmingly common edit: typing in a label. It is not part of the clip binding, so it
        // cannot disturb a voice - and the app must not hold it back as though it could.
        var renamed = TwoLists(listALabel: "Walk-in bed");

        Assert.True(await session.WouldPreservePlaybackAsync(renamed));
    }

    [Fact]
    public async Task AnEditToThePlayingCuesOwnBinding_IsNotSafe()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        var edited = TwoLists(listBMedia: "fake://b-edited");

        Assert.False(await session.WouldPreservePlaybackAsync(edited));

        // Which is the load's own answer: this reload really does stop it.
        await session.LoadDocumentAsync(edited, preserveMatchingCompositions: true, preserveActiveGroups: true);
        Assert.True(releases.IsReleased("dev-b1"));
    }

    [Fact]
    public async Task DeletingThePlayingCue_IsNotSafe()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        var withoutB = new ShowDocument(
            Version: 1,
            Cues: [Cue("a1", 1, "listA")],
            Clips: [Clip("a1")],
            Compositions: [], Routes: []);

        Assert.False(await session.WouldPreservePlaybackAsync(withoutB));
    }

    [Fact]
    public async Task TheForecastChangesNothing()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // Asked repeatedly, including about a document that would tear the show down. A prediction that
        // disturbed what it was predicting about would be worse than no prediction at all.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(await session.WouldPreservePlaybackAsync(TwoLists()));
            Assert.False(await session.WouldPreservePlaybackAsync(
                TwoLists(listBMedia: "fake://b-edited")));
        }

        Assert.False(releases.IsReleased("dev-b1"));
        var active = Assert.Single(session.Snapshot(), snapshot => snapshot.IsActive);
        Assert.Equal("listB", active.GroupId);
    }

    [Fact]
    public async Task ADocumentWithOmittedCollections_IsAnsweredRatherThanThrown()
    {
        var releases = new ReleaseLog();
        await using var session = BuildSession(releases);
        await session.LoadDocumentAsync(TwoLists());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b1"));

        // Source-generated deserialization leaves absent arrays null. The load normalizes them; so must
        // this, or a caller would read the throw as "unsafe" forever and never apply another edit.
        var sparse = new ShowDocument(Version: 1, Cues: null!, Clips: null!, Compositions: null!, Routes: null!);

        Assert.False(await session.WouldPreservePlaybackAsync(sparse));
    }
}
