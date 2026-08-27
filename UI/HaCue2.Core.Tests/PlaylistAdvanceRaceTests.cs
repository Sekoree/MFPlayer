using HaCue2.Core.Model;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The playlist's advance fires OUTSIDE the executor's state gate, so the outgoing item's natural
/// end can arrive - and be spent - while that fire is still in flight. These tests drive exactly
/// that interleaving through <see cref="FakeCueHost.PlayOverride"/> and pin the recovery: a failed
/// fire whose fallback edge is already gone must run the edge's continuation itself instead of
/// restoring a run nothing will ever advance again.
/// </summary>
public class PlaylistAdvanceRaceTests
{
    private static (CueExecutor Executor, FakeCueHost Host, GroupCueNode Group) Playlist(
        int children, int crossfadeMs)
    {
        var group = new GroupCueNode
        {
            Number = new CueNumber("1"),
            FireMode = GroupFireMode.Playlist,
            CrossfadeMs = crossfadeMs,
            LoopCount = 0, // endless - the wrap paths under test only exist with another pass to come
        };
        for (var index = 0; index < children; index++)
            group.Children.Add(new MediaCueNode
            {
                Number = new CueNumber($"1.{index + 1}"),
                Label = $"1.{index + 1}",
                MediaPath = $"1.{index + 1}.wav",
            });

        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [group] }],
        };
        var host = new FakeCueHost(project);
        return (new CueExecutor(host), host, group);
    }

    [Fact]
    public async Task AWrapFireFailureAfterTheClosersEndStillRunsTheNextPass()
    {
        var (executor, host, group) = Playlist(children: 2, crossfadeMs: 1_000);
        var a = group.Children[0].Id;
        var b = group.Children[1].Id;

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(a);

        // The wrap's fire fails, and the closer's natural end lands while it is in flight - the
        // interleaving that used to consume the wrap marker and then restore the completed run
        // over it, stranding the group with no advance and no end policy left to come.
        var wrapAttempted = false;
        host.PlayOverride = async cue =>
        {
            if (wrapAttempted)
                return true;
            wrapAttempted = true;
            await executor.OnNaturalEndAsync(b);
            return false;
        };

        await executor.OnApproachingEndAsync(b);

        // The recovery fired the next pass's opener itself, from cold (no crossfade - the closer
        // is gone), and the run is alive: the opener's own end advances it.
        Assert.Equal([a, b, a], host.Played);
        Assert.Null(host.Transitions[^1].Duration);
        await executor.OnNaturalEndAsync(a);
        Assert.Equal([a, b, a, b], host.Played);
    }

    [Fact]
    public async Task AWrapFireFailureWithTheCloserStillSoundingWaitsForItsEnd()
    {
        var (executor, host, group) = Playlist(children: 2, crossfadeMs: 1_000);
        var a = group.Children[0].Id;
        var b = group.Children[1].Id;

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(a);

        host.PlayFails = true;
        await executor.OnApproachingEndAsync(b);
        Assert.Equal([a, b], host.Played);

        // The closer kept sounding, so its natural end still owns the pass's continuation.
        host.PlayFails = false;
        await executor.OnNaturalEndAsync(b);
        Assert.Equal([a, b, a], host.Played);
    }

    [Fact]
    public async Task AnAdvanceFireFailureAfterTheOutgoingEndRetriesFromCold()
    {
        var (executor, host, group) = Playlist(children: 3, crossfadeMs: 1_000);
        var a = group.Children[0].Id;
        var b = group.Children[1].Id;
        var c = group.Children[2].Id;

        await executor.FireAsync(group.Id);

        // The crossfade advance to the second item fails while the first item's natural end lands
        // mid-flight. That end is absorbed as already-handled, so restoring the pre-advance run
        // would wait forever on an edge that was just spent.
        var advanceAttempted = false;
        host.PlayOverride = async cue =>
        {
            if (advanceAttempted)
                return true;
            advanceAttempted = true;
            await executor.OnNaturalEndAsync(a);
            return false;
        };

        await executor.OnApproachingEndAsync(a);

        Assert.Equal([a, b], host.Played);
        Assert.Null(host.Transitions[^1].Duration);
        await executor.OnNaturalEndAsync(b);
        Assert.Equal([a, b, c], host.Played);
    }

    [Fact]
    public async Task AnAdvanceFireFailureBeforeTheOutgoingEndIsRetriedByThatEnd()
    {
        var (executor, host, group) = Playlist(children: 3, crossfadeMs: 1_000);
        var a = group.Children[0].Id;
        var b = group.Children[1].Id;

        await executor.FireAsync(group.Id);

        host.PlayFails = true;
        await executor.OnApproachingEndAsync(a);
        Assert.Equal([a], host.Played);

        host.PlayFails = false;
        await executor.OnNaturalEndAsync(a);
        Assert.Equal([a, b], host.Played);
    }
}
