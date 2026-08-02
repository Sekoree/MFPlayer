using S.Media.Core.Registry;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// The engine works without a cue list. A clip is a playable thing with an id; whether some cue fires it is
/// a question only a cue list asks.
/// </summary>
/// <remarks>
/// This is the engine/cue-semantics seam made observable. Before it, a document whose clips named no cue
/// failed validation and could not be played, which is why HaPlay's deck minted a synthetic cue per track
/// purely to get past the door.
/// </remarks>
public sealed class CueFreeCoreTests
{
    private const string Device = "cue-free-device";

    private static ShowDocument ClipsOnly(params string[] clipIds) => new(
        Version: 1,
        Cues: [],
        Clips: [.. clipIds.Select(id => new ShowClipBinding(id, "tone://1")
        {
            AudioRoutes = [new ShowClipAudioRoute(Device, [0, 1])],
        })],
        Compositions: [],
        Routes: []);

    [Fact]
    public void ADocumentWithClipsAndNoCues_Validates()
    {
        var errors = ShowDocumentValidator.Validate(ClipsOnly("a", "b"));

        // "a clip binds unknown cue" was a cue-layer rule living in the document validator.
        Assert.Empty(errors);
    }

    [Fact]
    public void DuplicateClipIds_AreStillRejected()
    {
        // The runtime keys clips by id, so this one really is a document error - relaxing the cue rule must
        // not relax this with it.
        var errors = ShowDocumentValidator.Validate(ClipsOnly("dup", "dup"));

        Assert.Contains(errors, e => e.Message.Contains("dup", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyClipId_IsRejected()
    {
        var errors = ShowDocumentValidator.Validate(ClipsOnly(""));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task ACueFreeDocumentLoads()
    {
        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));

        await session.LoadDocumentAsync(ClipsOnly("intro", "outro"));

        // No cues, so nothing is fireable BY CUE - and that is not an error state.
        Assert.Empty(await session.GetCueDefinitionsAsync());
    }

    [Fact]
    public async Task AClipPlaysByIdWithNoCueInvolved()
    {
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (_, format) => new ClipAudioOutputLease(new DiscardingAudioOutput(format)));
        await session.LoadDocumentAsync(ClipsOnly("intro"));

        var status = await session.PlayClipAsync("intro", "deck");

        Assert.Equal(CueExecutionStatus.Fired, status);
        Assert.Contains(await session.SnapshotAsync(), g => g.GroupId == "deck");
    }

    [Fact]
    public async Task AnUnknownClipId_ReportsNotReady_RatherThanThrowing()
    {
        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));
        await session.LoadDocumentAsync(ClipsOnly("intro"));

        // A host addressing a clip that a reload removed gets a status it can act on, the same way a cue
        // with no binding does - not an exception mid-show.
        Assert.Equal(CueExecutionStatus.NotReady, await session.PlayClipAsync("gone", "deck"));
    }

    [Fact]
    public async Task PlayingASecondClipOnAGroup_ReplacesTheFirst()
    {
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (_, format) => new ClipAudioOutputLease(new DiscardingAudioOutput(format)));
        await session.LoadDocumentAsync(ClipsOnly("intro", "outro"));

        Assert.Equal(CueExecutionStatus.Fired, await session.PlayClipAsync("intro", "deck"));
        Assert.Equal(CueExecutionStatus.Fired, await session.PlayClipAsync("outro", "deck"));

        // The group is the slot: the same transport contract a fired cue gets, reached without the cue layer.
        Assert.Single(await session.SnapshotAsync(), g => g.GroupId == "deck");
    }

    [Fact]
    public async Task OverlappingPlaysOnOneGroup_LeaveExactlyOneClipActive()
    {
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (_, format) => new ClipAudioOutputLease(new DiscardingAudioOutput(format)));
        await session.LoadDocumentAsync(ClipsOnly("first", "second"));

        // The cue path serialises through the fire-lock; the cue-free path has none, so two overlapping opens
        // on one group must still resolve to a single consistent voice rather than two actives or a torn
        // group. WHICH one wins is decided by open ticket (TransportGroup.OpenSequence) - deliberately not
        // asserted here, because with both calls started concurrently the order in which they reach the
        // dispatcher is not itself defined, so an assertion on it would be testing the scheduler.
        await Task.WhenAll(
            session.PlayClipAsync("first", "deck"),
            session.PlayClipAsync("second", "deck"));

        var active = Assert.Single(session.GetActiveClipPipelineMetrics(), m => m.GroupId == "deck");
        Assert.Contains(active.ClipId, (string[])["first", "second"]);
        Assert.Single(await session.SnapshotAsync(), g => g.GroupId == "deck");
    }

    [Fact]
    public async Task CuesAndCueFreeClipsCoexistInOneDocument()
    {
        await using var session = new ShowSession(
            ToneAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (_, format) => new ClipAudioOutputLease(new DiscardingAudioOutput(format)));

        // A cue names one clip; the other is reachable only by id. Both must work, because the cue layer is
        // additive over the core rather than a gate in front of it.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cued", 1, "Cued")],
            Clips:
            [
                new ShowClipBinding("cued", "tone://1")
                    { AudioRoutes = [new ShowClipAudioRoute(Device, [0, 1])] },
                new ShowClipBinding("loose", "tone://1")
                    { AudioRoutes = [new ShowClipAudioRoute(Device, [0, 1])] },
            ],
            Compositions: [],
            Routes: []);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cued"));
        Assert.Equal(CueExecutionStatus.Fired, await session.PlayClipAsync("loose", "deck"));
    }
}
