using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Per-list standby: each cue list is its own transport group, so each has its own GO cursor, and an edit
/// must not rewind it.
/// </summary>
/// <remarks>
/// The cursor used to live on the live <c>TransportGroup</c>, so it died whenever the group did - and a
/// reload disposes every group that is not actively sounding. A list you had GO'd partway through and then
/// stopped silently rewound to the top on the next edit, which is the app's normal path (debounced ~300 ms
/// after any structural change) rather than an edge case.
/// </remarks>
public sealed class StandbyCursorTests
{
    private const string Device = "standby-device";

    private static CueDefinition Cue(string id, int number, string? group = null) =>
        new(id, number, id.ToUpperInvariant()) { GroupId = group };

    private static ShowClipBinding Clip(string id) =>
        new(id, "tone://1") { AudioRoutes = [new ShowClipAudioRoute(Device, [0, 1])] };

    /// <summary>Two lists, each its own group, three cues apiece.</summary>
    private static ShowDocument TwoLists() => new(
        Version: 1,
        Cues:
        [
            Cue("a1", 1, "listA"), Cue("a2", 2, "listA"), Cue("a3", 3, "listA"),
            Cue("b1", 4, "listB"), Cue("b2", 5, "listB"), Cue("b3", 6, "listB"),
        ],
        Clips: [Clip("a1"), Clip("a2"), Clip("a3"), Clip("b1"), Clip("b2"), Clip("b3")],
        Compositions: [],
        Routes: []);

    private static ShowSession Session() => new(
        ToneAudioDecoderProvider.Registry(),
        new RecordingAudioBackend(),
        audioOutputFactory: (_, format) => new ClipAudioOutputLease(new DiscardingAudioOutput(format)));

    [Fact]
    public async Task AFreshListStandsByOnItsFirstCue()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());

        Assert.Equal("a1", (await session.GetStandbyCueAsync("listA"))?.Id);
        Assert.Equal("b1", (await session.GetStandbyCueAsync("listB"))?.Id);
    }

    [Fact]
    public async Task GoAdvancesOnlyItsOwnList()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());

        await session.GoAsync("listA");

        Assert.Equal("a2", (await session.GetStandbyCueAsync("listA"))?.Id);
        Assert.Equal("b1", (await session.GetStandbyCueAsync("listB"))?.Id);
    }

    [Fact]
    public async Task APreservingReloadKeepsEveryListsPosition()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.GoAsync("listA");
        await session.GoAsync("listA");
        await session.StopAllAsync();

        // The list is stopped, so its group is not retained - which is exactly the case that used to rewind.
        await session.LoadDocumentAsync(TwoLists(), preserveActiveGroups: true);

        Assert.Equal("a3", (await session.GetStandbyCueAsync("listA"))?.Id);
        Assert.Equal("b1", (await session.GetStandbyCueAsync("listB"))?.Id);
    }

    [Fact]
    public async Task ANonPreservingLoadStartsEveryListAtTheTop()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.GoAsync("listA");

        // No opt-in means "a different show", and a different show's lists start at the top.
        await session.LoadDocumentAsync(TwoLists());

        Assert.Equal("a1", (await session.GetStandbyCueAsync("listA"))?.Id);
    }

    [Fact]
    public async Task StandbyCanBeSetDirectly()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());

        Assert.True(await session.SetStandbyCueAsync("a3", "listA"));

        Assert.Equal("a3", (await session.GetStandbyCueAsync("listA"))?.Id);
    }

    [Fact]
    public async Task SettingStandbyThenGoing_FiresThatCue()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.SetStandbyCueAsync("a3", "listA");

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync("listA"));

        // Standby and GO must run the same selection - a readout that disagrees with what fires is worse
        // than no readout.
        Assert.Null(await session.GetStandbyCueAsync("listA"));
    }

    [Fact]
    public async Task StandbyCanBeRewound()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.GoAsync("listA");

        Assert.True(await session.SetStandbyCueAsync(null, "listA"));

        Assert.Equal("a1", (await session.GetStandbyCueAsync("listA"))?.Id);
    }

    [Fact]
    public async Task SettingStandbyToAnUnknownCue_LeavesTheCursorAlone()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.GoAsync("listA");

        Assert.False(await session.SetStandbyCueAsync("ghost", "listA"));

        Assert.Equal("a2", (await session.GetStandbyCueAsync("listA"))?.Id);
    }

    [Fact]
    public async Task SettingStandbyToACueFromAnotherList_IsRejected()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.GoAsync("listA");

        Assert.False(await session.SetStandbyCueAsync("b3", "listA"));

        Assert.Equal("a2", (await session.GetStandbyCueAsync("listA"))?.Id);
    }

    [Fact]
    public async Task AListRunOut_HasNoStandby()
    {
        await using var session = Session();
        await session.LoadDocumentAsync(TwoLists());
        await session.SetStandbyCueAsync("a3", "listA");
        await session.GoAsync("listA");

        Assert.Null(await session.GetStandbyCueAsync("listA"));
    }

    [Fact]
    public async Task ADisabledCueIsSkippedByStandby_JustAsGoSkipsIt()
    {
        await using var session = Session();
        var doc = TwoLists();
        await session.LoadDocumentAsync(doc with
        {
            Cues = [.. doc.Cues.Select(c => c.Id == "a1" ? c with { Enabled = false } : c)],
        });

        Assert.Equal("a2", (await session.GetStandbyCueAsync("listA"))?.Id);
    }
}
