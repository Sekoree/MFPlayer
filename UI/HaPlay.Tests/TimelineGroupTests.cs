using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using HaPlay.Playback;
using HaPlay.ViewModels;
using HaPlay.Views.Controls;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Timeline fire mode (Phase A): trigger-plan mapping, persistence, group aggregate span,
/// live-playhead projection, and the timeline canvas's pure hit-test/snap geometry.</summary>
public sealed class TimelineGroupTests
{
    /// <summary>Runs <paramref name="action"/> on the headless UI session and OBSERVES the result.
    /// <c>Dispatch</c> hands back a Task; discarding it (the shape this helper used to have) threw every
    /// assertion failure inside the body away, so these tests passed no matter what the code under test
    /// did. Blocking here is safe - the body is synchronous and the xunit thread is not the session's
    /// dispatcher thread (the async sibling is <see cref="HeadlessDispatchExtensions.DispatchAsync"/>).</summary>
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(TimelineGroupTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    // ---- Trigger plan ----

    [Fact]
    public void BuildTriggerPlan_TimelineGroup_DelaysAreGroupPreWaitPlusStartPlusCuePreWait()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.Timeline.ToString();
        group.PreWaitMs = 100;

        vm.AddEmptyMediaCue();
        var late = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        late.TimelineStartMs = 2000;

        vm.SelectedCueNode = group;
        vm.AddActionCueCommand.Execute(null);
        var middle = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        middle.TimelineStartMs = 500;
        middle.PreWaitMs = 250;

        vm.SelectedCueNode = group;
        vm.AddEmptyMediaCue();
        var first = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        first.TimelineStartMs = 0;

        var plan = vm.BuildTriggerPlan(group);

        Assert.Equal([first.Id, middle.Id, late.Id], plan.Select(p => p.Cue.Id));
        Assert.Equal([100, 850, 2100], plan.Select(p => p.DelayMs));
        // Timeline lanes overlap: every step fires in its own runtime transport group.
        Assert.All(plan, p => Assert.True(p.Independent));
    }

    [Fact]
    public void BuildTriggerPlan_TimelineGroup_NestedGroupFlattensAtItsLaneStart()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.Timeline.ToString();

        vm.AddGroupCommand.Execute(null);
        var nested = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        Assert.Contains(nested, group.Children);
        nested.TimelineStartMs = 1000;
        nested.PreWaitMs = 50;

        vm.AddEmptyMediaCue();
        var inner1 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        inner1.PreWaitMs = 25;
        vm.SelectedCueNode = nested;
        vm.AddEmptyMediaCue();
        var inner2 = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        var plan = vm.BuildTriggerPlan(group);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, p => p.Cue.Id == inner1.Id && p.DelayMs == 1075);
        Assert.Contains(plan, p => p.Cue.Id == inner2.Id && p.DelayMs == 1050);
        Assert.Equal(plan.OrderBy(p => p.DelayMs).Select(p => p.Cue.Id), plan.Select(p => p.Cue.Id));
        Assert.All(plan, p => Assert.True(p.Independent)); // nested-lane flattening keeps the overlap flag
    }

    [Fact]
    public void BuildTriggerPlan_IndependentFlag_TracksOverlapModes()
    {
        // Overlap modes (FireAllSimultaneously, Timeline - covered above) mark every step Independent;
        // the sequential shared-group paths (FirstCueOnly, AutoContinue chains) never do.
        var sim = new CueGroupNode
        {
            Label = "Sim",
            FireMode = CueGroupFireMode.FireAllSimultaneously,
            Children = { new MediaCueNode { Label = "A" }, new MediaCueNode { Label = "B" } },
        };
        var firstOnly = new CueGroupNode
        {
            Label = "First",
            FireMode = CueGroupFireMode.FirstCueOnly,
            Children = { new MediaCueNode { Label = "C" }, new MediaCueNode { Label = "D" } },
        };
        var anchor = new MediaCueNode { Label = "Anchor" };
        var chained = new MediaCueNode { Label = "Chained", TriggerMode = CueTriggerMode.AutoContinue };

        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [sim, firstOnly, anchor, chained] }]);
        var nodes = vm.SelectedCueList!.Nodes;

        var simPlan = vm.BuildTriggerPlan(nodes[0]);
        Assert.Equal(2, simPlan.Count);
        Assert.All(simPlan, p => Assert.True(p.Independent));

        Assert.False(Assert.Single(vm.BuildTriggerPlan(nodes[1])).Independent);

        var chainPlan = vm.BuildTriggerPlan(nodes[2]); // anchor + its AutoContinue follower
        Assert.Equal(2, chainPlan.Count);
        Assert.All(chainPlan, p => Assert.False(p.Independent));
    }

    [Fact]
    public void BuildTriggerPlan_TimelineGroup_SkipsCommentChildren()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.Timeline.ToString();

        vm.AddCommentCueCommand.Execute(null);
        vm.SelectedCueNode = group;
        vm.AddEmptyMediaCue();
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);

        var plan = vm.BuildTriggerPlan(group);
        Assert.Equal(media.Id, Assert.Single(plan).Cue.Id);
    }

    [TimingFact] // real-time delay ordering + pause epoch shift; flaky on an oversubscribed CI VM
    public async Task TimelineGroup_PauseShiftsTheAuthoredStart()
    {
        var vm = new CuePlayerViewModel();
        vm.AddGroupCommand.Execute(null);
        var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        group.Extra = CueGroupFireMode.Timeline.ToString();

        vm.AddActionCueCommand.Execute(null);
        var opener = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        opener.TimelineStartMs = 0;

        vm.SelectedCueNode = group;
        vm.AddEmptyMediaCue();
        var delayed = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        delayed.TimelineStartMs = 200;

        vm.SelectedCueNode = group;
        vm.StandbySelectedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        await Task.Delay(40);
        Assert.Same(opener, vm.CurrentCueNode);

        // Pausing shifts the plan epoch: the 200 ms lane start must not elapse while paused.
        vm.PauseCommand.Execute(null);
        await Task.Delay(320);
        Assert.Same(opener, vm.CurrentCueNode);

        vm.GoCommand.Execute(null); // resume
        await Task.Delay(400);
        Assert.Same(delayed, vm.CurrentCueNode);
    }

    // ---- Persistence ----

    [Fact]
    public void TimelineStartMs_RoundTripsThroughCueListJson()
    {
        var list = new CueList
        {
            Nodes =
            {
                new CueGroupNode
                {
                    Label = "Timeline group",
                    FireMode = CueGroupFireMode.Timeline,
                    Children =
                    {
                        new MediaCueNode { Label = "Bed", TimelineStartMs = 12_345 },
                        new ActionCueNode { Label = "Lights", TimelineStartMs = 4_000 },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;

        var group = Assert.IsType<CueGroupNode>(loaded.Nodes[0]);
        Assert.Equal(CueGroupFireMode.Timeline, group.FireMode);
        Assert.Equal(12_345, Assert.IsType<MediaCueNode>(group.Children[0]).TimelineStartMs);
        Assert.Equal(4_000, Assert.IsType<ActionCueNode>(group.Children[1]).TimelineStartMs);
    }

    [Fact]
    public void TimelineStartMs_DefaultZero_IsNotWrittenAndLegacyFilesLoadUnchanged()
    {
        var list = new CueList { Nodes = { new MediaCueNode { Label = "Old-style" } } };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.DoesNotContain("timelineStartMs", json);

        var legacy = """{"nodes":[{"kind":"group","fireMode":1,"children":[{"kind":"media","label":"Old"}]}]}""";
        var loaded = JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!;
        var group = Assert.IsType<CueGroupNode>(loaded.Nodes[0]);
        Assert.Equal(CueGroupFireMode.FireAllSimultaneously, group.FireMode);
        Assert.Equal(0, group.TimelineStartMs);
        Assert.Equal(0, Assert.IsType<MediaCueNode>(group.Children[0]).TimelineStartMs);
    }

    [Fact]
    public void TimelineStartMs_RoundTripsThroughCueNodeViewModel()
    {
        var vm = new CuePlayerViewModel();
        vm.AddEmptyMediaCue();
        var media = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
        media.TimelineStartMs = 1234;

        var node = Assert.IsType<MediaCueNode>(Assert.Single(vm.BuildCueListsSnapshot()[0].Nodes));
        Assert.Equal(1234, node.TimelineStartMs);
        Assert.Equal(1234, CueNodeViewModel.FromModel(node).TimelineStartMs);
    }

    // ---- Aggregate span ----

    [Fact]
    public void TimelineGroup_RolledDuration_IsAuthoredSpan()
    {
        var group = new CueNodeViewModel(CueNodeKind.Group) { Extra = CueGroupFireMode.Timeline.ToString() };
        group.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 60_000 });
        group.Children.Add(new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 30_000, TimelineStartMs = 45_000 });
        group.Children.Add(new CueNodeViewModel(CueNodeKind.Comment) { TimelineStartMs = 500_000 });

        Assert.Equal(75_000, group.RolledDurationMs);
        Assert.Equal("01:15 · 2", group.DurationDisplay);

        // Pre-wait shifts a lane start exactly like the trigger plan does.
        group.Children[1].PreWaitMs = 5_000;
        Assert.Equal(80_000, group.RolledDurationMs);
    }

    [Fact]
    public void TimelineGroup_NowPlayingAggregate_ProjectsFurthestActiveChild()
    {
        DispatchUi(static () =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddGroupCommand.Execute(null);
            var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            group.Extra = CueGroupFireMode.Timeline.ToString();

            vm.AddEmptyMediaCue();
            var bed = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            bed.DurationMs = 60_000;

            vm.SelectedCueNode = group;
            vm.AddEmptyMediaCue();
            var voice = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            voice.DurationMs = 30_000;
            voice.TimelineStartMs = 45_000;

            vm.OnCueStarted(bed.Id);
            vm.OnCueStarted(voice.Id);

            var row = Assert.IsType<ActiveGroupViewModel>(Assert.Single(vm.NowPlayingRows));
            vm.OnCueProgress(new CuePlaybackProgress(bed.Id, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
            vm.OnCueProgress(new CuePlaybackProgress(voice.Id, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)));

            // Authored span 75 s; the furthest projection is the voice-over: 45 s + 10 s = 55 s.
            Assert.Equal(75_000, row.LongestDurationMs);
            Assert.Equal(55_000 * 100.0 / 75_000, row.ProgressPercent, 1);
        });
    }

    [Fact]
    public void TimelineEditor_Playhead_ProjectsActiveChildOntoGroupEpoch()
    {
        DispatchUi(static () =>
        {
            var vm = new CuePlayerViewModel();
            vm.AddGroupCommand.Execute(null);
            var group = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            group.Extra = CueGroupFireMode.Timeline.ToString();

            vm.AddEmptyMediaCue();
            var voice = Assert.IsType<CueNodeViewModel>(vm.SelectedCueNode);
            voice.DurationMs = 60_000;
            voice.TimelineStartMs = 45_000;

            using var editor = new TimelineEditorWindowViewModel(vm, group, startPlayheadTimer: false);
            editor.UpdatePlayhead();
            Assert.Equal(-1, editor.PlayheadMs);

            vm.OnCueStarted(voice.Id);
            vm.OnCueProgress(new CuePlaybackProgress(voice.Id, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)));
            editor.UpdatePlayhead();
            Assert.Equal(55_000, editor.PlayheadMs, 0);

            vm.OnCueEnded(voice.Id);
            editor.UpdatePlayhead();
            Assert.Equal(-1, editor.PlayheadMs);
        });
    }

    // ---- Canvas geometry (pure) ----

    private const double PxPerMs = 0.1; // 100 px per second

    [Fact]
    public void HitTestBlock_PicksBodyEdgesAndFadeHandles()
    {
        // Lane 0 block: 10 s start, 30 s long -> x 1000..4000, y 31..57 at 0.1 px/ms.
        var block = TimelineMath.BlockRect(0, 10_000, 30_000, PxPerMs);
        Assert.Equal(1000, block.X, 3);
        Assert.Equal(3000, block.Width, 3);

        Assert.Equal(TimelineHitKind.Block, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(2500, 45), trimmable: true));
        Assert.Equal(TimelineHitKind.LeftEdge, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(1004, 45), trimmable: true));
        Assert.Equal(TimelineHitKind.RightEdge, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(3996, 45), trimmable: true));
        Assert.Equal(TimelineHitKind.None, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(4500, 45), trimmable: true));
        Assert.Equal(TimelineHitKind.None, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(2500, 100), trimmable: true));

        // A 5 s fade-in puts its handle at x 1500 on the top edge.
        Assert.Equal(TimelineHitKind.FadeInHandle,
            TimelineMath.HitTestBlock(block, 5_000, 0, PxPerMs, new Point(1502, block.Y - 2), trimmable: true));
        Assert.Equal(TimelineHitKind.FadeOutHandle,
            TimelineMath.HitTestBlock(block, 0, 5_000, PxPerMs, new Point(3498, block.Y + 2), trimmable: true));

        // Zero-length fades park their handles on the corners and still beat the edge grips there.
        Assert.Equal(TimelineHitKind.FadeInHandle,
            TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(block.X, block.Y), trimmable: true));

        // Unprobed media (fallback width): body still drags, but no trims or fades.
        Assert.Equal(TimelineHitKind.Block, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(1004, 45), trimmable: false));
        Assert.Equal(TimelineHitKind.Block, TimelineMath.HitTestBlock(block, 0, 0, PxPerMs, new Point(block.X + 1, block.Y + 1), trimmable: false));
    }

    [Fact]
    public void MarkerHitTest_UsesDiamondExtent()
    {
        var center = TimelineMath.MarkerCenter(1, 4_000, PxPerMs);
        Assert.Equal(400, center.X, 3);

        Assert.True(TimelineMath.MarkerContains(center, new Point(center.X + 3, center.Y + 2)));
        Assert.False(TimelineMath.MarkerContains(center, new Point(center.X + 9, center.Y)));
        Assert.False(TimelineMath.MarkerContains(center, new Point(center.X + 5, center.Y + 5)));
    }

    [Fact]
    public void Snap_EdgeBeatsGrid_GridApplies_DisabledReturnsRaw()
    {
        // 8 px threshold at 0.1 px/ms = 80 ms capture range.
        Assert.Equal(1250, TimelineMath.Snap(1230, snapEnabled: true, gridMs: 1000, [1250], PxPerMs));
        Assert.Equal(1000, TimelineMath.Snap(1230, snapEnabled: true, gridMs: 1000, [], PxPerMs));
        Assert.Equal(1230, TimelineMath.Snap(1230, snapEnabled: false, gridMs: 1000, [1250], PxPerMs));
        Assert.Equal(0, TimelineMath.Snap(-50, snapEnabled: false, gridMs: 1000, [], PxPerMs));
        // Nearest of several edges wins.
        Assert.Equal(1220, TimelineMath.Snap(1230, snapEnabled: true, gridMs: 1000, [1250, 1220], PxPerMs));
        // 500 ms grid.
        Assert.Equal(1500, TimelineMath.Snap(1710, snapEnabled: true, gridMs: 500, [], PxPerMs));
    }

    [Fact]
    public void BlockDuration_FallsBackWhenUnknown_AndMarkersAreZeroLength()
    {
        var probed = new CueNodeViewModel(CueNodeKind.Media) { DurationMs = 60_000, StartOffsetMs = 10_000, EndOffsetMs = 5_000 };
        Assert.Equal(45_000, TimelineMath.BlockDurationMs(probed));
        Assert.True(TimelineMath.IsTrimmable(probed));

        var unprobed = new CueNodeViewModel(CueNodeKind.Media);
        Assert.Equal(TimelineMath.FallbackBlockDurationMs, TimelineMath.BlockDurationMs(unprobed));
        Assert.False(TimelineMath.IsTrimmable(unprobed));

        Assert.True(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Action)));
        Assert.True(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Jump)));
        Assert.True(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Visualizer)));
        Assert.True(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Fade)));
        Assert.True(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Comment)));
        Assert.False(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Media)));
        Assert.False(TimelineMath.IsMarker(new CueNodeViewModel(CueNodeKind.Group)));
        Assert.Equal(0, TimelineMath.BlockDurationMs(new CueNodeViewModel(CueNodeKind.Action)));
    }
}
