using System.Collections.Concurrent;
using System.Text.Json;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Playlist / armed-list groups (Ideas/CuePlayer-Enhancements.md §3): options persistence
/// (incl. the STJ init-property gotcha), the shuffle bag (draw without replacement, pass-boundary
/// no-repeat), pass/loop/subset counting, GO-advance armed lists, natural-end auto-advance, end
/// behaviors, and session-state lifecycle (Stop clears, standby pre-roll agrees with GO).</summary>
public sealed class PlaylistGroupTests
{
    // ---- Persistence ----

    [Fact]
    public void PlaylistOptions_RoundTrip_ThroughViewModelAndJson()
    {
        var node = new CueGroupNode
        {
            Number = "1",
            Label = "Set list",
            FireMode = CueGroupFireMode.Playlist,
            Playlist = new CuePlaylistOptions
            {
                Shuffle = true,
                AvoidImmediateRepeat = false,
                LoopCount = 3,
                PlayCount = 2,
                ReshuffleEachPass = false,
                EndBehavior = CuePlaylistEndBehavior.AdvancePastGroup,
            },
        };

        // VM round-trip preserves the payload.
        var vm = CueNodeViewModel.FromModel(node);
        Assert.True(vm.IsPlaylistFireMode);
        Assert.True(vm.PlaylistShuffle);
        Assert.False(vm.PlaylistAvoidImmediateRepeat);
        Assert.Equal(3, vm.PlaylistLoopCount);
        Assert.Equal(2, vm.PlaylistPlayCount);
        Assert.False(vm.PlaylistReshuffleEachPass);
        Assert.Equal(CuePlaylistEndBehavior.AdvancePastGroup, vm.PlaylistEndBehavior);
        var back = Assert.IsType<CueGroupNode>(vm.ToModel());
        Assert.Equal(CueGroupFireMode.Playlist, back.FireMode);
        Assert.Equal(node.Playlist, back.Playlist);

        // JSON round-trip through the cue-list contract (project persistence).
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<CueGroupNode>(Assert.Single(loaded.Nodes));
        Assert.Equal(CueGroupFireMode.Playlist, reloaded.FireMode);
        Assert.Equal(node.Playlist, reloaded.Playlist);
    }

    [Fact]
    public void PlaylistOptions_LegacyAbsence_LoadsNull_AndEmptyObjectKeepsDefaults()
    {
        // Old files carry no "playlist" field at all - must load unchanged (null options).
        var legacy = """{"nodes":[{"kind":"group","fireMode":2,"children":[{"kind":"media","label":"Old"}]}]}""";
        var loaded = JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!;
        var group = Assert.IsType<CueGroupNode>(loaded.Nodes[0]);
        Assert.Equal(CueGroupFireMode.ArmedList, group.FireMode);
        Assert.Null(group.Playlist);

        // The STJ source-gen gotcha: a minimal "playlist":{} must keep the C# property initializers
        // (set, not init - see the CuePlaylistOptions doc note), NOT collapse to CLR defaults.
        var minimal = """{"nodes":[{"kind":"group","fireMode":4,"playlist":{}}]}""";
        var minimalLoaded = JsonSerializer.Deserialize(minimal, CueListJsonContext.Default.CueList)!;
        var minimalGroup = Assert.IsType<CueGroupNode>(minimalLoaded.Nodes[0]);
        Assert.Equal(CueGroupFireMode.Playlist, minimalGroup.FireMode);
        var options = Assert.IsType<CuePlaylistOptions>(minimalGroup.Playlist);
        Assert.False(options.Shuffle);
        Assert.True(options.AvoidImmediateRepeat);
        Assert.Equal(1, options.LoopCount);
        Assert.Null(options.PlayCount);
        Assert.True(options.ReshuffleEachPass);
        Assert.Equal(CuePlaylistEndBehavior.Stop, options.EndBehavior);
    }

    [Fact]
    public void NonPlaylistGroup_WithUntouchedOptions_WritesNoPlaylistField()
    {
        var vm = CueNodeViewModel.FromModel(new CueGroupNode { Label = "Plain" });
        var back = Assert.IsType<CueGroupNode>(vm.ToModel());
        Assert.Null(back.Playlist);

        var json = JsonSerializer.Serialize(
            new CueList { Nodes = [back] }, CueListJsonContext.Default.CueList);
        Assert.DoesNotContain("playlist", json);
    }

    // ---- Runtime harness ----

    private sealed record Harness(
        CuePlayerViewModel Vm,
        CueNodeViewModel GroupVm,
        List<Guid> ChildIds,
        ConcurrentQueue<Guid> Fired,
        SemaphoreSlim FireSignal);

    private static Harness BuildPlaylistVm(
        int childCount,
        CueGroupFireMode mode,
        CuePlaylistOptions options,
        IReadOnlyList<CueNode>? after = null,
        CueTriggerMode childTrigger = CueTriggerMode.Manual)
    {
        var children = Enumerable.Range(1, childCount)
            .Select(i => new MediaCueNode
            {
                Number = $"1.{i}",
                Label = $"Song {i}",
                TriggerMode = childTrigger,
                Source = new FilePlaylistItem($"/tmp/song{i}.wav"),
            })
            .ToArray();
        var group = new CueGroupNode
        {
            Number = "1",
            Label = "Playlist",
            FireMode = mode,
            Playlist = options,
            Children = [.. children],
        };
        var nodes = new List<CueNode> { group };
        if (after is not null)
            nodes.AddRange(after);

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = nodes }]);

        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (cue, _) =>
        {
            vm.OnCueStarted(cue.Id);
            fired.Enqueue(cue.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };

        return new Harness(
            vm,
            vm.SelectedCueList!.Nodes[0],
            children.Select(c => c.Id).ToList(),
            fired,
            signal);
    }

    private static async Task<Guid> NextFiredAsync(Harness h)
    {
        Assert.True(await h.FireSignal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a cue fire");
        Assert.True(h.Fired.TryDequeue(out var id));
        return id;
    }

    private static async Task AssertNoFurtherFiresAsync(Harness h, int graceMs = 250)
    {
        await Task.Delay(graceMs);
        Assert.Empty(h.Fired);
    }

    // ---- Playlist mode: natural-end auto-advance ----

    [Fact]
    public async Task Playlist_Sequential_AdvancesInOrder_ThenStops()
    {
        var h = BuildPlaylistVm(3, CueGroupFireMode.Playlist, new CuePlaylistOptions());

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var sequence = new List<Guid> { await NextFiredAsync(h) };

        // Standby stays ON the group while the run continues.
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode);
        Assert.Equal("item 1/3 · pass 1/1", h.Vm.BuildPlaylistStatus(h.GroupVm));

        for (var i = 0; i < 2; i++)
        {
            await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
            sequence.Add(await NextFiredAsync(h));
        }

        Assert.Equal(h.ChildIds, sequence);

        // Final natural end: default EndBehavior Stop - nothing further fires, the finished run is
        // over (standing the group by again re-arms a FRESH run for pre-roll, so the observable
        // "run over" signal is the empty status), and a fresh GO restarts at child 1.
        await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
        await AssertNoFurtherFiresAsync(h);
        Assert.Null(h.Vm.CurrentCueNode);
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode);
        Assert.Null(h.Vm.BuildPlaylistStatus(h.GroupVm));

        await h.Vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(h.ChildIds[0], await NextFiredAsync(h));
    }

    [Fact]
    public async Task Playlist_Sequential_IgnoresAvoidImmediateRepeat_KeepsAuthoredOrderAcrossPasses()
    {
        // AvoidImmediateRepeat is a SHUFFLE pass-boundary guard: a sequential playlist's order is
        // authored, and the guard must never rearrange it at a pass boundary.
        var h = BuildPlaylistVm(3, CueGroupFireMode.Playlist, new CuePlaylistOptions
        {
            AvoidImmediateRepeat = true,
            LoopCount = 2,
        });

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var sequence = new List<Guid> { await NextFiredAsync(h) };
        for (var i = 0; i < 5; i++)
        {
            await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
            sequence.Add(await NextFiredAsync(h));
        }

        // Both passes play the children in document order.
        Assert.Equal(h.ChildIds.Concat(h.ChildIds).ToList(), sequence);

        await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
        await AssertNoFurtherFiresAsync(h);
    }

    [Fact]
    public async Task Playlist_Sequential_PlayCountOne_ReplaysTheAuthoredOpenerEveryPass()
    {
        // The discriminating shape: sequential + PlayCount 1 means every pass replays child #1 -
        // an immediate repeat BY DESIGN. The old pass-boundary swap applied to sequential runs too
        // and replaced the authored opener with a random other child on the second pass.
        var h = BuildPlaylistVm(3, CueGroupFireMode.Playlist, new CuePlaylistOptions
        {
            AvoidImmediateRepeat = true,
            LoopCount = 2,
            PlayCount = 1,
        });

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        var second = await NextFiredAsync(h);

        Assert.Equal(h.ChildIds[0], first);
        Assert.Equal(h.ChildIds[0], second); // pass 2 replays the authored opener, not a swap

        await h.Vm.OnMediaCueNaturallyEndedAsync(second);
        await AssertNoFurtherFiresAsync(h);
    }

    [Fact]
    public async Task Playlist_ShuffleBag_PlaysEveryChildOncePerPass_NoRepeatAcrossPassBoundary()
    {
        var h = BuildPlaylistVm(4, CueGroupFireMode.Playlist, new CuePlaylistOptions
        {
            Shuffle = true,
            LoopCount = 2,
        });
        h.Vm.PlaylistRandom = new Random(42); // deterministic bag via the internal seed hook

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var sequence = new List<Guid> { await NextFiredAsync(h) };
        for (var i = 0; i < 7; i++)
        {
            await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
            sequence.Add(await NextFiredAsync(h));
        }

        // Bag semantics: each pass is every child exactly once (draw without replacement).
        Assert.Equal(8, sequence.Count);
        Assert.Equal(h.ChildIds.ToHashSet(), sequence.Take(4).ToHashSet());
        Assert.Equal(h.ChildIds.ToHashSet(), sequence.Skip(4).ToHashSet());
        // AvoidImmediateRepeat (default on) guards the reshuffled pass boundary.
        Assert.NotEqual(sequence[3], sequence[4]);

        await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
        await AssertNoFurtherFiresAsync(h);
        Assert.Null(h.Vm.BuildPlaylistStatus(h.GroupVm)); // finished run is over (no playing item)
    }

    [Fact]
    public async Task Playlist_InfiniteLoop_KeepsAdvancing()
    {
        var h = BuildPlaylistVm(2, CueGroupFireMode.Playlist, new CuePlaylistOptions { LoopCount = 0 });

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var sequence = new List<Guid> { await NextFiredAsync(h) };

        // Capped iteration guard for the infinite run: 6 advances must keep cycling 1,2,1,2,…
        for (var i = 0; i < 6; i++)
        {
            await h.Vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
            sequence.Add(await NextFiredAsync(h));
        }

        Assert.Equal(
            Enumerable.Range(0, 7).Select(i => h.ChildIds[i % 2]).ToList(),
            sequence);
        Assert.True(h.Vm.HasActivePlaylistRun(h.GroupVm.Id));
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode);
        Assert.Equal("item 1/2 · pass 4", h.Vm.BuildPlaylistStatus(h.GroupVm));
    }

    [Fact]
    public async Task Playlist_PlayCountSubset_PlaysOnlyThatManyPerPass()
    {
        var h = BuildPlaylistVm(5, CueGroupFireMode.Playlist, new CuePlaylistOptions { PlayCount = 2 });

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        var second = await NextFiredAsync(h);

        Assert.Equal(h.ChildIds[0], first);
        Assert.Equal(h.ChildIds[1], second);

        // The pass (and with LoopCount 1 the whole run) ends after the 2-item subset.
        await h.Vm.OnMediaCueNaturallyEndedAsync(second);
        await AssertNoFurtherFiresAsync(h);
        Assert.Null(h.Vm.BuildPlaylistStatus(h.GroupVm));
    }

    // ---- End behaviors ----

    [Fact]
    public async Task Playlist_AdvancePastGroup_AutoFollowsTheCueAfterTheGroup()
    {
        var afterCue = new ActionCueNode { Number = "2", Label = "After", TriggerMode = CueTriggerMode.AutoFollow };
        var h = BuildPlaylistVm(2, CueGroupFireMode.Playlist,
            new CuePlaylistOptions { EndBehavior = CuePlaylistEndBehavior.AdvancePastGroup },
            after: [afterCue]);
        var actionFired = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Vm.ActionCueExecutor = (cue, _) =>
        {
            actionFired.TrySetResult(cue.Id);
            return Task.FromResult<string?>(null);
        };

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        var second = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(second);

        // The run ended: standby moved past the group and the AutoFollow cue after it fired.
        Assert.Equal(afterCue.Id, await actionFired.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(h.Vm.HasActivePlaylistRun(h.GroupVm.Id));
    }

    [Fact]
    public async Task Playlist_StopEndBehavior_DoesNotAdvance_EvenWithAutoFollowAfterTheGroup()
    {
        var afterCue = new ActionCueNode { Number = "2", Label = "After", TriggerMode = CueTriggerMode.AutoFollow };
        var h = BuildPlaylistVm(2, CueGroupFireMode.Playlist,
            new CuePlaylistOptions { EndBehavior = CuePlaylistEndBehavior.Stop },
            after: [afterCue]);
        var actionFires = 0;
        h.Vm.ActionCueExecutor = (_, _) =>
        {
            Interlocked.Increment(ref actionFires);
            return Task.FromResult<string?>(null);
        };

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        var second = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(second);

        await AssertNoFurtherFiresAsync(h);
        Assert.Equal(0, actionFires);
        Assert.Null(h.Vm.CurrentCueNode);
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode);
    }

    // ---- Armed list: GO advances, no auto-advance ----

    [Fact]
    public async Task ArmedList_GOAdvances_AndWrapsOnInfiniteLoopCount()
    {
        var h = BuildPlaylistVm(2, CueGroupFireMode.ArmedList, new CuePlaylistOptions { LoopCount = 0 });

        h.Vm.StandbyCueFromView(h.GroupVm);
        var sequence = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            await h.Vm.GoCommand.ExecuteAsync(null);
            sequence.Add(await NextFiredAsync(h));
            Assert.Same(h.GroupVm, h.Vm.StandbyCueNode); // infinite: GO keeps targeting the group
        }

        Assert.Equal([h.ChildIds[0], h.ChildIds[1], h.ChildIds[0], h.ChildIds[1]], sequence);
    }

    [Fact]
    public async Task ArmedList_NaturalEnd_DoesNotAutoAdvance_EvenWithAutoFollowSibling()
    {
        var h = BuildPlaylistVm(2, CueGroupFireMode.ArmedList,
            new CuePlaylistOptions { LoopCount = 0 },
            childTrigger: CueTriggerMode.AutoFollow); // would chain without the armed suppression

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        Assert.Equal(h.ChildIds[0], first);

        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        await AssertNoFurtherFiresAsync(h);
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode); // the next GO (not the end) advances
    }

    [Fact]
    public async Task ArmedList_FinishedPass_MovesStandbyPastTheGroup()
    {
        var afterCue = new ActionCueNode { Number = "2", Label = "After" };
        var h = BuildPlaylistVm(2, CueGroupFireMode.ArmedList,
            new CuePlaylistOptions { LoopCount = 1 },
            after: [afterCue]);

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(h.ChildIds[0], await NextFiredAsync(h));
        Assert.Same(h.GroupVm, h.Vm.StandbyCueNode);

        await h.Vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(h.ChildIds[1], await NextFiredAsync(h));

        // Single pass exhausted: the next GO targets the cue after the group.
        Assert.Equal(afterCue.Id, h.Vm.StandbyCueNode!.Id);
    }

    // ---- Nested playlist items ----

    [Fact]
    public async Task Playlist_NestedPlaylistItem_PlaysThrough_AndTheOuterRunAdvancesPastIt()
    {
        // An outer playlist whose item is ITSELF a playlist group: firing the outer pick must also
        // consume the inner run's pick (regression: the inner CurrentItemId stayed null, natural-end
        // routing swallowed the inner item's end and both runs stalled), and the inner run's
        // completion must advance the OUTER run past the nested group instead of stalling on it.
        MediaCueNode Media(string number, string label) => new()
        {
            Number = number,
            Label = label,
            Source = new FilePlaylistItem($"/tmp/{label}.wav"),
        };

        var m1 = Media("1.1", "opener");
        var n1 = Media("1.2.1", "inner-a");
        var n2 = Media("1.2.2", "inner-b");
        var nested = new CueGroupNode
        {
            Number = "1.2",
            Label = "Inner set",
            FireMode = CueGroupFireMode.Playlist,
            Playlist = new CuePlaylistOptions { LoopCount = 1 },
            Children = [n1, n2],
        };
        var m2 = Media("1.3", "closer");
        var outer = new CueGroupNode
        {
            Number = "1",
            Label = "Outer set",
            FireMode = CueGroupFireMode.Playlist,
            Playlist = new CuePlaylistOptions { LoopCount = 1 },
            Children = [m1, nested, m2],
        };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [outer] }]);
        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (cue, _) =>
        {
            vm.OnCueStarted(cue.Id);
            fired.Enqueue(cue.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };
        async Task<Guid> NextAsync()
        {
            Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a cue fire");
            Assert.True(fired.TryDequeue(out var id));
            return id;
        }

        var outerVm = vm.SelectedCueList!.Nodes[0];
        vm.StandbyCueFromView(outerVm);
        await vm.GoCommand.ExecuteAsync(null);
        var sequence = new List<Guid> { await NextAsync() };
        for (var i = 0; i < 3; i++)
        {
            await vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
            sequence.Add(await NextAsync());
        }

        // Inner items played in place of the nested group, then the outer run advanced past it.
        Assert.Equal([m1.Id, n1.Id, n2.Id, m2.Id], sequence);

        // Final natural end finishes the outer run: nothing further fires. Standing the group by
        // again re-arms a FRESH outer run for pre-roll (the sequential test's contract), so the
        // observable "run over" signals are the empty status and the absent inner run.
        await vm.OnMediaCueNaturallyEndedAsync(sequence[^1]);
        await Task.Delay(250);
        Assert.Empty(fired);
        Assert.Null(vm.BuildPlaylistStatus(outerVm));
        Assert.False(vm.HasActivePlaylistRun(nested.Id));
    }

    // ---- Trigger-plan Independent flag ----

    [Fact]
    public void BuildTriggerPlan_PlaylistMediaPick_IsShared_ButANestedTimelinePickKeepsIndependence()
    {
        // A plain media pick plays alone through the authored shared group - not Independent…
        var h = BuildPlaylistVm(2, CueGroupFireMode.Playlist, new CuePlaylistOptions());
        var mediaStep = Assert.Single(h.Vm.BuildTriggerPlan(h.GroupVm));
        Assert.False(mediaStep.Independent);

        // …while a pick that is a nested Timeline group propagates its lanes' overlap independence
        // through the playlist branch.
        var laneA = new MediaCueNode { Label = "Lane A", Source = new FilePlaylistItem("/tmp/a.wav") };
        var laneB = new MediaCueNode
        {
            Label = "Lane B",
            Source = new FilePlaylistItem("/tmp/b.wav"),
            TimelineStartMs = 500,
        };
        var timeline = new CueGroupNode
        {
            Label = "Bed + voice",
            FireMode = CueGroupFireMode.Timeline,
            Children = [laneA, laneB],
        };
        var outer = new CueGroupNode
        {
            Label = "Set",
            FireMode = CueGroupFireMode.Playlist,
            Playlist = new CuePlaylistOptions(),
            Children = [timeline],
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [outer] }]);

        var plan = vm.BuildTriggerPlan(vm.SelectedCueList!.Nodes[0]);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.True(p.Independent));
    }

    // ---- Session-state lifecycle ----

    [Fact]
    public async Task Stop_ClearsRunState_AndAFreshGoStartsOver()
    {
        var h = BuildPlaylistVm(3, CueGroupFireMode.Playlist, new CuePlaylistOptions { LoopCount = 0 });

        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        var first = await NextFiredAsync(h);
        await h.Vm.OnMediaCueNaturallyEndedAsync(first);
        Assert.Equal(h.ChildIds[1], await NextFiredAsync(h));
        Assert.True(h.Vm.HasActivePlaylistRun(h.GroupVm.Id));

        h.Vm.StopCommand.Execute(null);
        Assert.False(h.Vm.HasActivePlaylistRun(h.GroupVm.Id));

        // Fresh run after Stop: sequential playback restarts at child 1.
        h.Vm.StandbyCueFromView(h.GroupVm);
        await h.Vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(h.ChildIds[0], await NextFiredAsync(h));
    }

    [Fact]
    public async Task StandbyPreRoll_AgreesWithGo_OnTheShuffledNextPick()
    {
        var h = BuildPlaylistVm(4, CueGroupFireMode.Playlist, new CuePlaylistOptions { Shuffle = true });
        h.Vm.PlaylistRandom = new Random(7);

        // Standing by the group commits the shuffle draw: pre-roll warms the armed pick…
        h.Vm.StandbyCueFromView(h.GroupVm);
        var preRollTargets = h.Vm.GetPreparedMediaCueTargets();
        Assert.NotEmpty(preRollTargets);

        // …and GO fires exactly that pick.
        await h.Vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(preRollTargets[0].Id, await NextFiredAsync(h));
    }
}
