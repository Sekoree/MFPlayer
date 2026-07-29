using System.Collections.Concurrent;
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
