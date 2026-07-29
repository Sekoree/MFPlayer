using System.Collections.Concurrent;
using Avalonia.Headless;
using Avalonia.Input;
using HaPlay.Playback;
using HaPlay.Services;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Workstream A - the CROSS-LIST MERGED SESSION. Every loaded cue list maps into the one
/// <c>ShowSession</c> document, so schedules / triggers / the remote API can fire any cue in any
/// list, while the visible transport (GO, standby, the cue tree) still follows the SELECTED list
/// only. The first test is the regression contract for existing shows: a single-list project keeps
/// producing exactly the document it produced before, modulo the list-scoped runtime group ids.
/// </summary>
public sealed class CrossListSessionTests
{
    // ---- Mapping ----

    /// <summary>A single-list project must behave EXACTLY as before: same cues (order, numbers,
    /// labels, pre-waits), same clips, same compositions - the only difference is that runtime
    /// transport groups are now list-scoped (a bare group id becomes "{list}:{group}", and the
    /// ungrouped "no group at all" becomes the list's own default group).</summary>
    [Fact]
    public void SingleList_MergedDocument_MatchesTheSingleListDocument_ExceptGroupIdPrefix()
    {
        var listId = Guid.NewGuid();
        var list = BuildRichCueList(out var group, out var nested);

        var single = HaPlayShowMapper.ToShowDocument(list);
        var merged = HaPlayShowMapper.ToShowDocument([(listId, list)]);

        // Clips and compositions are byte-for-byte the same records (they carry no group identity).
        Assert.Equal(single.Clips, merged.Clips);
        Assert.Equal(single.Compositions, merged.Compositions);

        // Cues: identical apart from GroupId.
        Assert.Equal(single.Cues.Count, merged.Cues.Count);
        for (var i = 0; i < single.Cues.Count; i++)
        {
            var before = single.Cues[i];
            var after = merged.Cues[i];
            Assert.Equal(before with { GroupId = null }, after with { GroupId = null });
            Assert.Equal(
                before.GroupId is null
                    ? HaPlayShowMapper.RuntimeGroupId(listId)
                    : HaPlayShowMapper.RuntimeGroupId(listId, Guid.Parse(before.GroupId)),
                after.GroupId);
        }

        // And the concrete shape those rules produce: an ungrouped cue on the list's default group,
        // a grouped one (nested included - subgroups still collapse into the outermost) on the
        // list-scoped authored group.
        Assert.Equal(HaPlayShowMapper.RuntimeGroupId(listId), merged.Cues[0].GroupId);
        Assert.Equal(HaPlayShowMapper.RuntimeGroupId(listId, group.Id), merged.Cues[1].GroupId);
        Assert.Equal(HaPlayShowMapper.RuntimeGroupId(listId, group.Id), merged.Cues[2].GroupId);
        Assert.NotEqual(HaPlayShowMapper.RuntimeGroupId(listId, nested.Id), merged.Cues[2].GroupId);
    }

    [Fact]
    public void MergedDocument_ConcatenatesEveryList_WithoutGroupCollisions()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var aCue = new MediaCueNode { Label = "A1", Source = new FilePlaylistItem("/m/a1.wav") };
        var bCue = new MediaCueNode { Label = "B1", Source = new FilePlaylistItem("/m/b1.wav") };
        var aComp = new CueComposition { Name = "A canvas" };
        var bComp = new CueComposition { Name = "B canvas" };
        var a = new CueList { Name = "A", Nodes = { aCue }, Compositions = { aComp } };
        var b = new CueList { Name = "B", Nodes = { bCue }, Compositions = { bComp } };

        var doc = HaPlayShowMapper.ToShowDocument([(aId, a), (bId, b)]);

        // Every list's cues, clips and compositions live in the one document.
        Assert.Equal(new[] { "A1", "B1" }, doc.Cues.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { 1, 2 }, doc.Cues.Select(c => c.Number).ToArray()); // numbering continues
        Assert.Equal(2, doc.Clips.Count);
        Assert.Equal(new[] { "A canvas", "B canvas" }, doc.Compositions.Select(c => c.Name).ToArray());

        // The whole point of the list prefix: two lists' ungrouped cues must NOT share one transport
        // group, or firing B's cue would replace whatever A has playing.
        Assert.Equal(HaPlayShowMapper.RuntimeGroupId(aId), doc.Cues[0].GroupId);
        Assert.Equal(HaPlayShowMapper.RuntimeGroupId(bId), doc.Cues[1].GroupId);
        Assert.NotEqual(doc.Cues[0].GroupId, doc.Cues[1].GroupId);
    }

    [Fact]
    public void SingleListDocument_IsUnchanged_ForTheSidecarExportPath()
    {
        // The per-list sidecar/export mapping keeps the historical shape (no list prefix at all) -
        // it writes one document per list, so there is nothing to scope.
        var list = BuildRichCueList(out var group, out _);
        var doc = HaPlayShowMapper.ToShowDocument(list);

        Assert.Null(doc.Cues[0].GroupId);
        Assert.Equal(group.Id.ToString(), doc.Cues[1].GroupId);
    }

    // ---- Scheduler / trigger scope ----

    [Fact]
    public async Task Scheduler_FiresACueInANonSelectedList_WithoutTouchingTheVisibleTransport()
    {
        var scheduled = new MediaCueNode
        {
            Number = "1",
            Label = "Station ID",
            Source = new FilePlaylistItem("/m/ident.wav"),
            Schedule = new CueSchedule { Kind = CueScheduleKind.TimeOfDay, TimeOfDay = new TimeOnly(15, 0) },
        };
        var visible = new MediaCueNode
        {
            Number = "1",
            Label = "Main song",
            Source = new FilePlaylistItem("/m/song.wav"),
        };
        var (vm, fired, signal) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { visible } },
            new CueList { Name = "Automation", Nodes = { scheduled } });

        var clockNow = new DateTimeOffset(new DateTime(2026, 7, 29, 14, 59, 50), TimeSpan.FromHours(2));
        var scheduler = new CueSchedulerService(vm, () => clockNow);
        vm.SchedulesArmed = true;

        Assert.Equal("Main", vm.SelectedCueList!.Name);
        scheduler.Tick(); // not due yet
        Assert.Empty(fired);

        clockNow = new DateTimeOffset(new DateTime(2026, 7, 29, 15, 0, 1), TimeSpan.FromHours(2));
        scheduler.Tick();
        Assert.Equal(scheduled.Id, await NextAsync(fired, signal));

        // The visible transport is exactly where the operator left it.
        Assert.Equal("Main", vm.SelectedCueList!.Name);
        Assert.Null(vm.StandbyCueNode);
        Assert.Null(vm.CurrentCueNode);
        Assert.Single(vm.VisibleNodes);
        Assert.Equal("Main song", vm.VisibleNodes[0].Label);
    }

    [Fact]
    public async Task Triggers_SweepEveryLoadedList()
    {
        var triggered = new MediaCueNode
        {
            Number = "1",
            Label = "Sting",
            Source = new FilePlaylistItem("/m/sting.wav"),
            Triggers = [new CueTriggerBinding { Kind = CueTriggerKind.Hotkey, HotkeyGesture = "Ctrl+F5" }],
        };
        var (vm, fired, signal) = BuildPlayer(
            new CueList { Name = "Main" },
            new CueList { Name = "Stings", Nodes = { triggered } });
        var service = new CueTriggerService(vm, () => DateTimeOffset.UnixEpoch);
        vm.TriggersArmed = true;

        Assert.True(service.TryHandleHotkey(
            new KeyEventArgs { Key = Key.F5, KeyModifiers = KeyModifiers.Control }));
        Assert.Equal(triggered.Id, await NextAsync(fired, signal));
        Assert.Null(vm.StandbyCueNode);
        Assert.Null(vm.CurrentCueNode);
    }

    [Fact]
    public async Task ForeignFire_RunsTheChainOfItsOwnList()
    {
        // The Auto-Continue chain used to be resolved against the SELECTED list's fireable order, which
        // for a cue in another list found nothing - a cross-list fire would have played exactly one cue.
        var lead = new MediaCueNode
        {
            Label = "Lead",
            Source = new FilePlaylistItem("/m/lead.wav"),
            Triggers = [new CueTriggerBinding { Kind = CueTriggerKind.Hotkey, HotkeyGesture = "F7" }],
        };
        var chained = new MediaCueNode
        {
            Label = "Chained",
            Source = new FilePlaylistItem("/m/chained.wav"),
            TriggerMode = CueTriggerMode.AutoContinue,
        };
        var (vm, fired, signal) = BuildPlayer(
            new CueList { Name = "Main" },
            new CueList { Name = "Backup", Nodes = { lead, chained } });
        var service = new CueTriggerService(vm, () => DateTimeOffset.UnixEpoch);
        vm.TriggersArmed = true;

        Assert.True(service.TryHandleHotkey(new KeyEventArgs { Key = Key.F7 }));

        var first = await NextAsync(fired, signal);
        var second = await NextAsync(fired, signal);
        Assert.Equal(new[] { lead.Id, chained.Id }.Order(), new[] { first, second }.Order());
        Assert.Null(vm.CurrentCueNode);
        Assert.Null(vm.StandbyCueNode);
    }

    [Fact]
    public void RemoteReference_ResolvesAcrossLists_ButTheSelectedListWinsANumber()
    {
        var mine = new MediaCueNode { Number = "1", Label = "Mine", Source = new FilePlaylistItem("/m/a.wav") };
        var theirs = new MediaCueNode { Number = "1", Label = "Theirs", Source = new FilePlaylistItem("/m/b.wav") };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { mine } },
            new CueList { Name = "Backup", Nodes = { theirs } });

        // Numbers restart per list, so the visible list wins the ambiguous "1".
        Assert.Equal(mine.Id, vm.FindCueByReference("1")?.Id);
        // Guid ids are globally unique - they resolve in any loaded list.
        Assert.Equal(theirs.Id, vm.FindCueByReference(theirs.Id.ToString())?.Id);
    }

    // ---- Arm surfaces ----

    [Fact]
    public void ArmingSchedules_NoLongerWarnsAboutOtherLists_AndTheTooltipCountsThemAll()
    {
        var here = new MediaCueNode
        {
            Label = "Here",
            Source = new FilePlaylistItem("/m/a.wav"),
            Schedule = new CueSchedule { TimeOfDay = new TimeOnly(15, 0), Enabled = true },
        };
        var there = new MediaCueNode
        {
            Label = "There",
            Source = new FilePlaylistItem("/m/b.wav"),
            Schedule = new CueSchedule { TimeOfDay = new TimeOnly(16, 0), Enabled = true },
        };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { here } },
            new CueList { Name = "Automation", Nodes = { there } });

        vm.SchedulesArmed = true;

        Assert.Null(vm.StatusMessage); // the "other lists will NOT fire" warning is gone with its premise
        Assert.Contains("2", vm.SchedulesArmedTooltip);
        Assert.Contains(Resources.Strings.SchedulesArmedToggleTooltip, vm.SchedulesArmedTooltip);
    }

    [Fact]
    public void ArmingTriggers_TooltipCountsEnabledBindingsAcrossLists()
    {
        var there = new MediaCueNode
        {
            Label = "There",
            Source = new FilePlaylistItem("/m/b.wav"),
            Triggers = [new CueTriggerBinding { Kind = CueTriggerKind.Hotkey, HotkeyGesture = "F6" }],
        };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main" },
            new CueList { Name = "Stings", Nodes = { there } });

        vm.TriggersArmed = true;

        Assert.Null(vm.StatusMessage);
        Assert.Contains("1", vm.TriggersArmedTooltip);
    }

    // ---- Now Playing ----

    [Fact]
    public void NowPlaying_QualifiesRowsFiredFromAnotherList_AndDropsThePrefixOnSwitch()
    {
        var mine = new MediaCueNode { Number = "1", Label = "Mine", Source = new FilePlaylistItem("/m/a.wav") };
        var theirs = new MediaCueNode { Number = "1", Label = "Theirs", Source = new FilePlaylistItem("/m/b.wav") };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { mine } },
            new CueList { Name = "Backup", Nodes = { theirs } });

        vm.OnCueStarted(mine.Id);
        vm.OnCueStarted(theirs.Id);

        var mineRow = vm.ActiveCues.Single(a => a.CueId == mine.Id);
        var theirsRow = vm.ActiveCues.Single(a => a.CueId == theirs.Id);
        Assert.Equal("Mine", mineRow.CueLabel); // the selected list's own rows read exactly as before
        Assert.Contains("Backup", theirsRow.CueLabel);
        Assert.Contains("Theirs", theirsRow.CueLabel);

        // Switching to the other list re-stamps both rows.
        vm.SelectedCueList = vm.CueLists[1];
        Assert.Equal("Theirs", theirsRow.CueLabel);
        Assert.Contains("Main", mineRow.CueLabel);
    }

    /// <summary>A RENAME has to re-stamp the live rows too. A list switch did (nothing else observed the
    /// name), so a renamed list kept its old prefix on every Now-Playing row already on screen - and the
    /// prefix is the only thing telling the operator which list a row belongs to.</summary>
    [Fact]
    public void NowPlaying_RenamingAList_ReStampsItsLiveRows()
    {
        var mine = new MediaCueNode { Number = "1", Label = "Mine", Source = new FilePlaylistItem("/m/a.wav") };
        var theirs = new MediaCueNode { Number = "1", Label = "Theirs", Source = new FilePlaylistItem("/m/b.wav") };
        var theirGroup = new CueGroupNode { Label = "Stings", Children = { theirs } };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { mine } },
            new CueList { Name = "Backup", Nodes = { theirGroup } });

        vm.OnCueStarted(theirs.Id);
        var row = vm.ActiveCues.Single(a => a.CueId == theirs.Id);
        var groupRow = vm.NowPlayingRows.OfType<ActiveGroupViewModel>().Single();
        Assert.Contains("Backup", row.CueLabel);
        Assert.Contains("Backup", groupRow.GroupLabel);

        vm.CueLists[1].Name = "Understudy";

        Assert.Contains("Understudy", row.CueLabel);
        Assert.DoesNotContain("Backup", row.CueLabel);
        Assert.Contains("Understudy", groupRow.GroupLabel);
        // The selected list's own rows never gain a prefix, however it is renamed.
        vm.OnCueStarted(mine.Id);
        var mineRow = vm.ActiveCues.Single(a => a.CueId == mine.Id);
        vm.CueLists[0].Name = "Show A";
        Assert.Equal("Mine", mineRow.CueLabel);
    }

    // ---- The visible transport is never moved by another list ----

    /// <summary>
    /// The invariant, on the FAILURE path. A cross-list fire that fails must leave the visible
    /// transport exactly where the operator left it. It did not: the failure handler parked the failed
    /// cue in <c>StandbyCueNode</c> unconditionally, so a foreign node replaced the visible list's
    /// standby - and because the cue tree's row statuses only walk the SELECTED list, the standby dot
    /// just disappeared while the operator's next GO was silently re-aimed at another list's cue,
    /// through the visible transport path. It also cleared <c>IsTransportPaused</c>, un-pausing a
    /// transport that had nothing to do with the failure.
    /// </summary>
    [Fact]
    public async Task FailedCrossListFire_LeavesTheVisibleStandbyAndPauseAlone()
    {
        var mine = new MediaCueNode { Number = "1", Label = "Mine", Source = new FilePlaylistItem("/m/a.wav") };
        var theirs = new MediaCueNode { Number = "1", Label = "Theirs", Source = new FilePlaylistItem("/m/b.wav") };
        var theirGroup = new CueGroupNode { Label = "Stings", Children = { theirs } };
        var (vm, _, _) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { mine } },
            new CueList { Name = "Automation", Nodes = { theirGroup } });

        var visibleStandby = vm.CueLists[0].Nodes.Single();
        vm.StandbyCueNode = visibleStandby;
        vm.IsTransportPaused = true;

        // The grouped-media path reports failure synchronously (no dispatcher hop): the executor
        // "succeeds" with a detail but never starts the cue, which is how a cue with no bound output
        // fails in the host.
        vm.MediaCueIndependentExecutor = (_, _) => Task.FromResult<string?>("no output bound");

        var foreign = vm.CueLists[1].Nodes.Single().Children.Single();
        await vm.FireScheduledCueAsync(foreign);

        Assert.Same(visibleStandby, vm.StandbyCueNode);
        Assert.True(vm.IsTransportPaused);
        Assert.Null(vm.CurrentCueNode);
        // ... and the failure is reported against the list it actually happened in.
        Assert.Contains("Automation", vm.StatusMessage);
        Assert.Contains("Theirs", vm.StatusMessage);
    }

    /// <summary>The same handler must keep working for the SELECTED list - the standby affordance
    /// ("it didn't go, press GO to retry") is the point of it.</summary>
    [Fact]
    public async Task FailedSelectedListFire_StillParksTheCueInStandby()
    {
        var mine = new MediaCueNode { Number = "1", Label = "Mine", Source = new FilePlaylistItem("/m/a.wav") };
        var myGroup = new CueGroupNode { Label = "Bed", Children = { mine } };
        var (vm, _, _) = BuildPlayer(new CueList { Name = "Main", Nodes = { myGroup } });

        vm.IsTransportPaused = true;
        vm.MediaCueIndependentExecutor = (_, _) => Task.FromResult<string?>("no output bound");

        var cue = vm.CueLists[0].Nodes.Single().Children.Single();
        await vm.FireScheduledCueAsync(cue);

        Assert.Same(cue, vm.StandbyCueNode);
        Assert.False(vm.IsTransportPaused);
    }

    /// <summary>
    /// The operator's row click arms a one-shot "GO fires THIS row" override. A schedule firing in
    /// another list used to clear it (the reset ran ahead of the foreign-list branch), so the next GO
    /// silently fell back to the standby cue instead of the row the operator had just clicked - a
    /// visible-transport change caused by a list nobody was looking at.
    /// </summary>
    [Fact]
    public async Task CrossListScheduledFire_DoesNotDisarmTheOperatorsRowClick()
    {
        var first = new MediaCueNode { Number = "1", Label = "First", Source = new FilePlaylistItem("/m/1.wav") };
        var clicked = new MediaCueNode { Number = "2", Label = "Clicked", Source = new FilePlaylistItem("/m/2.wav") };
        var theirs = new MediaCueNode { Number = "1", Label = "Theirs", Source = new FilePlaylistItem("/m/b.wav") };
        var (vm, fired, signal) = BuildPlayer(
            new CueList { Name = "Main", Nodes = { first, clicked } },
            new CueList { Name = "Automation", Nodes = { theirs } });

        // The operator clicks row 2 - GO is now aimed at it rather than at the standby (row 1).
        vm.SelectedCueNode = vm.CueLists[0].Nodes[1];
        Assert.Equal(clicked.Id, vm.SelectedCueNode!.Id);

        await vm.FireScheduledCueAsync(vm.CueLists[1].Nodes.Single());
        Assert.Equal(theirs.Id, await NextAsync(fired, signal));

        await vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(clicked.Id, await NextAsync(fired, signal));
    }

    // ---- Instant cues (Action / Visualizer / Fade) in a foreign list ----

    /// <summary>An INSTANT cue (Action/Visualizer/Fade) has no media end machinery, so its Auto-Follow
    /// successor is fired by <c>AdvanceAutoFollowAfterInstantCueAsync</c>. That resolved the chain in the
    /// SELECTED list, which for a foreign cue contains no index for it at all - so a scheduled/triggered
    /// Action cue in an automation list simply dropped its chain, silently. (Continuing through
    /// <c>GoCore</c> would have been worse: it would have armed the VISIBLE standby with another list's
    /// cue, which is the one thing cross-list firing is defined not to do.)</summary>
    [Fact]
    public async Task ForeignInstantCue_RunsItsOwnListsAutoFollowChain_WithoutMovingTheVisibleTransport()
    {
        // The instant-cue chain runs inside ApplyCueExecutionResult on the UI thread, so the headless
        // session has to be pumping - hence the dispatched body rather than a bare [Fact].
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CrossListSessionTests).Assembly)
            .DispatchAsync(async () =>
            {
                var action = new ActionCueNode { Label = "House lights", AddressOrMessage = "/house/lights" };
                var chained = new MediaCueNode
                {
                    Label = "Bed",
                    Source = new FilePlaylistItem("/m/bed.wav"),
                    TriggerMode = CueTriggerMode.AutoFollow,
                };
                var visible = new MediaCueNode { Label = "Main song", Source = new FilePlaylistItem("/m/song.wav") };
                var (vm, fired, signal) = BuildPlayer(
                    new CueList { Name = "Main", Nodes = { visible } },
                    new CueList { Name = "Automation", Nodes = { action, chained } });
                vm.ActionCueExecutor = (_, _) => Task.FromResult<string?>(null);

                var visibleStandby = vm.StandbyCueNode;
                await vm.FireScheduledCueAsync(vm.CueLists[1].Nodes[0]);

                Assert.Equal(chained.Id, await NextAsync(fired, signal));
                // …and the visible transport never moved.
                Assert.Same(visibleStandby, vm.StandbyCueNode);
                Assert.Null(vm.CurrentCueNode);
            });
    }

    /// <summary>A Fade cue's explicit targets are authored links WITHIN one list (the Jump-cue rule).
    /// Resolving them against the SELECTED list found none of a foreign fade's targets, so a
    /// scheduled/triggered fade in another list reported "no targets" and ramped nothing.</summary>
    [Fact]
    public async Task ForeignFadeCue_ResolvesItsTargetsInItsOwnList()
    {
        // The fade path resolves its targets through Dispatcher.UIThread.InvokeAsync, which only runs
        // while the headless session is pumping - hence the dispatched body rather than a bare [Fact].
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CrossListSessionTests).Assembly)
            .DispatchAsync(async () =>
            {
                var bed = new MediaCueNode { Label = "Bed", Source = new FilePlaylistItem("/m/bed.wav") };
                var fade = new FadeCueNode
                {
                    Label = "Duck the bed",
                    TargetCueIds = [bed.Id],
                    TargetLevelDb = -12,
                    DurationMs = 500,
                };
                var visible = new MediaCueNode { Label = "Main song", Source = new FilePlaylistItem("/m/song.wav") };
                var (vm, _, _) = BuildPlayer(
                    new CueList { Name = "Main", Nodes = { visible } },
                    new CueList { Name = "Automation", Nodes = { bed, fade } });

                var fadeVm = vm.CueLists[1].Nodes[1];

                // The resolution itself, which is what the fire path awaits on the UI thread.
                Assert.Equal([bed.Id], vm.ResolveFadeCueTargetsOnUi(fadeVm));

                // …and end to end: the executor is reached with that target rather than the fade
                // being refused with "no targets".
                var ramped = new TaskCompletionSource<IReadOnlyList<Guid>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                vm.FadeCueExecutor = (_, targets, _) =>
                {
                    ramped.TrySetResult(targets);
                    return Task.FromResult<string?>(null);
                };

                await vm.FireScheduledCueAsync(fadeVm);
                var completed = await Task.WhenAny(ramped.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(ramped.Task, completed);
                Assert.Equal([bed.Id], await ramped.Task);
            });
    }

    /// <summary>A foreign list's PRE-WAIT is a scheduling delay, not playback: it must follow its own
    /// headless run, not the visible transport's pause. Gating it on <c>IsTransportPaused</c> meant that
    /// pausing whichever list the operator happened to be looking at froze every other list's scheduled
    /// pre-rolls - a long station-ID pre-wait in an automation list simply never came due.</summary>
    [Fact]
    public async Task ForeignPreWait_IsNotFrozenByTheVisibleTransportsPause()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CrossListSessionTests).Assembly)
            .DispatchAsync(async () =>
            {
                var visible = new MediaCueNode { Label = "Main song", Source = new FilePlaylistItem("/m/song.wav") };
                var scheduled = new MediaCueNode
                {
                    Label = "Station ID",
                    Source = new FilePlaylistItem("/m/ident.wav"),
                    PreWaitMs = 120,
                };
                var (vm, fired, signal) = BuildPlayer(
                    new CueList { Name = "Main", Nodes = { visible } },
                    new CueList { Name = "Automation", Nodes = { scheduled } });

                // The operator paused the list they are watching. Nothing about that concerns the
                // automation list's not-yet-started cue.
                vm.CurrentCueNode = vm.CueLists[0].Nodes[0];
                vm.IsTransportPaused = true;

                var run = vm.FireScheduledCueAsync(vm.CueLists[1].Nodes[0]);
                Assert.Equal(scheduled.Id, await NextAsync(fired, signal));
                Assert.True(vm.IsTransportPaused); // …and the foreign run never touched the visible pause
                await run;
            });
    }

    // ---- The merged document's rebuild + group tracking ----

    /// <summary>
    /// A pending edit is flushed by a full <c>LoadDocumentAsync</c>, which rebuilds the ONE merged
    /// document and tears down every clip running in it. The debounce tick and the pre-roll warm both
    /// defer while something is playing for exactly that reason; the fire-path flush did not. Before the
    /// cross-list merge only edits to the SELECTED list could dirty the graph, so the flush could never
    /// interrupt anything the operator had not just edited - now an edit to a list nobody is looking at
    /// stops the playing list's cue at the next fire.
    /// </summary>
    [Fact]
    public async Task PendingEdit_IsNotFlushedWhileACueIsPlaying_AndIsFlushedOnceItStops()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CrossListSessionTests).Assembly)
            .DispatchAsync(async () =>
            {
                var playing = new MediaCueNode { Label = "Main song", Source = new FilePlaylistItem("/m/a.wav") };
                var (vm, _, _) = BuildPlayer(
                    new CueList { Name = "Main", Nodes = { playing } },
                    new CueList { Name = "Automation" });
                var coordinator = new CueShowSessionCoordinator(
                    vm, new SoundboardWorkspaceViewModel(), new OutputManagementViewModel());

                // List A is playing; an edit lands in list C (any list dirties the merged graph now).
                vm.OnCueStarted(playing.Id);
                Assert.True(vm.HasActiveCues);
                coordinator.MarkCueShowGraphDirtyForTests();

                await coordinator.EnsureCueShowSessionCurrentForTestsAsync();

                Assert.Equal(0, coordinator.CueDocumentRebuildAttempts);
                Assert.True(coordinator.IsCueShowGraphDirtyForTests); // still pending, not lost

                // Once the cue really ends the same flush commits the edit.
                vm.OnCueEnded(playing.Id);
                Assert.False(vm.HasActiveCues);
                await coordinator.EnsureCueShowSessionCurrentForTestsAsync();

                Assert.Equal(1, coordinator.CueDocumentRebuildAttempts);
                Assert.False(coordinator.IsCueShowGraphDirtyForTests);
            });
    }

    /// <summary>
    /// The merged document scopes EVERY cue to a list-scoped runtime transport group, so
    /// <c>ShowSession.DefaultGroup</c> ("main") is a group no cue is ever on. Falling back to it pointed
    /// the per-group end monitor at a permanently idle group, which declared the cue ended after the ~3 s
    /// warmup grace while it was still audible - clearing Now-Playing and letting the next document
    /// rebuild through, which then tore the clip down. An unresolvable group must be an operator-visible
    /// error, never a substituted group.
    /// </summary>
    [Fact]
    public async Task CueWithNoGroupInTheDocument_IsReportedInsteadOfTrackedOnTheDefaultGroup()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CrossListSessionTests).Assembly)
            .DispatchAsync(() =>
            {
                var orphan = new MediaCueNode { Label = "Orphan", Source = new FilePlaylistItem("/m/a.wav") };
                var (vm, _, _) = BuildPlayer(new CueList { Name = "Main", Nodes = { orphan } });
                var coordinator = new CueShowSessionCoordinator(
                    vm, new SoundboardWorkspaceViewModel(), new OutputManagementViewModel());

                // Nothing has ever been loaded into the session, so no cue has a mapped group.
                coordinator.MarkCueShowCueStartedForTests(orphan.Id);

                Assert.Equal(0, coordinator.TrackedCueShowGroupCountForTests);
                Assert.Contains("Orphan", vm.StatusMessage ?? string.Empty);
                // The row still appears, so the operator can see it and stop it.
                Assert.Contains(orphan.Id, vm.ActiveCues.Select(a => a.CueId));

                // An explicitly supplied runtime group (the independent/simultaneous fire paths) is
                // still tracked exactly as before.
                coordinator.MarkCueShowCueStartedForTests(
                    orphan.Id, CueShowSessionCoordinator.BuildSimultaneousRuntimeGroup(orphan.Id));
                Assert.Equal(1, coordinator.TrackedCueShowGroupCountForTests);
                return Task.CompletedTask;
            });
    }

    // ---- Helpers ----

    private static CueList BuildRichCueList(out CueGroupNode group, out CueGroupNode nested)
    {
        var comp = new CueComposition { Name = "Main" };
        var intro = new MediaCueNode
        {
            Number = "1",
            Label = "Intro",
            PreWaitMs = 250,
            Source = new FilePlaylistItem("/m/intro.mp4"),
            FadeInMs = 300,
            VideoPlacements = [new CueVideoPlacement { CompositionId = comp.Id, LayerIndex = 1 }],
        };
        var inGroup = new MediaCueNode { Label = "In group", Source = new FilePlaylistItem("/m/a.wav") };
        var deep = new MediaCueNode { Label = "Deep", Source = new FilePlaylistItem("/m/b.wav") };
        nested = new CueGroupNode { Label = "Inner", Children = { deep } };
        group = new CueGroupNode { Label = "Outer", Children = { inGroup, nested } };
        return new CueList
        {
            Name = "Act 1",
            Compositions = { comp },
            Nodes = { intro, group },
        };
    }

    /// <summary>A cue player holding <paramref name="lists"/> with the FIRST one selected, wired to a
    /// media executor that records fires (and reports the cue started, like the real host).</summary>
    private static (CuePlayerViewModel Vm, ConcurrentQueue<Guid> Fired, SemaphoreSlim Signal) BuildPlayer(
        params CueList[] lists)
    {
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(lists);
        vm.IsCueEditMode = false; // the ctor default is edit mode ON - a show runs with it off

        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (m, _) =>
        {
            vm.OnCueStarted(m.Id);
            fired.Enqueue(m.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };

        return (vm, fired, signal);
    }

    private static async Task<Guid> NextAsync(ConcurrentQueue<Guid> fired, SemaphoreSlim signal)
    {
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a cue fire");
        Assert.True(fired.TryDequeue(out var id));
        return id;
    }
}
