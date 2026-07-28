using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Fade cues (Ideas/CuePlayer-Enhancements.md §2): model/VM/JSON round-trip with ID-stable
/// targets, legacy-file default safety, number/range target parsing (the Jump-targets infra), the
/// authoring gesture, and target resolution into the host executor.</summary>
public sealed class FadeCueTests
{
    [Fact]
    public void FadeCueNode_RoundTrips_ThroughViewModelAndJson()
    {
        var targetA = Guid.NewGuid();
        var targetB = Guid.NewGuid();
        var node = new FadeCueNode
        {
            Number = "9",
            Label = "Fade band",
            TriggerMode = CueTriggerMode.AutoFollow,
            TargetCueIds = [targetA, targetB],
            TargetAllPlaying = false,
            TargetLevelDb = -10,
            DurationMs = 5000,
            Curve = CueFadeCurve.Exponential,
            StopWhenSilent = false,
            AlsoFadeVideoOpacity = false,
        };

        // VM round-trip preserves the fade payload.
        var vm = CueNodeViewModel.FromModel(node);
        Assert.Equal(CueNodeKind.Fade, vm.Kind);
        Assert.Equal([targetA, targetB], vm.FadeTargetIds);
        Assert.Equal(-10, vm.FadeTargetLevelDb);
        Assert.Equal(5000, vm.DurationMs);
        Assert.Equal(CueFadeCurve.Exponential, vm.FadeOutCurve);
        Assert.False(vm.FadeStopWhenSilent);
        Assert.False(vm.FadeAlsoFadeVideo);
        var back = Assert.IsType<FadeCueNode>(vm.ToModel());
        Assert.Equal(node.TargetCueIds, back.TargetCueIds);
        Assert.False(back.TargetAllPlaying);
        Assert.Equal(-10, back.TargetLevelDb);
        Assert.Equal(5000, back.DurationMs);
        Assert.Equal(CueFadeCurve.Exponential, back.Curve);
        Assert.False(back.StopWhenSilent);
        Assert.False(back.AlsoFadeVideoOpacity);

        // JSON round-trip through the polymorphic cue-node contract (project persistence).
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        Assert.Contains("\"fade\"", json); // the type discriminator
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<FadeCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal([targetA, targetB], reloaded.TargetCueIds);
        Assert.Equal(-10, reloaded.TargetLevelDb);
        Assert.Equal(5000, reloaded.DurationMs);
        Assert.Equal(CueFadeCurve.Exponential, reloaded.Curve);
        Assert.False(reloaded.StopWhenSilent);
        Assert.False(reloaded.AlsoFadeVideoOpacity);
    }

    [Fact]
    public void FadeCueNode_NegativeInfinityLevel_SurvivesJson()
    {
        // The doc sketch's "-inf = silence": named floating-point literals are explicitly allowed on
        // TargetLevelDb so such a value can round-trip instead of failing the save.
        var list = new CueList { Nodes = [new FadeCueNode { TargetLevelDb = double.NegativeInfinity }] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var fade = Assert.IsType<FadeCueNode>(Assert.Single(loaded.Nodes));
        Assert.True(double.IsNegativeInfinity(fade.TargetLevelDb));
    }

    [Fact]
    public void LegacyFadeJson_WithoutOptionalFields_LoadsDefaultSafe()
    {
        // A minimal "fade" node (as a minimal writer - or a future stripped-down file - would store it)
        // deserializes to the documented defaults: fade-to-silence over 3 s, linear, stop at silence,
        // video follows, explicit-target mode with no targets.
        const string json = """
            {
              "name": "Legacy",
              "nodes": [ { "kind": "fade", "number": "9", "label": "Old fade" } ]
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var fade = Assert.IsType<FadeCueNode>(Assert.Single(loaded.Nodes));
        Assert.Empty(fade.TargetCueIds);
        Assert.False(fade.TargetAllPlaying);
        Assert.Equal(FadeCueNode.SilenceLevelDb, fade.TargetLevelDb);
        Assert.Equal(3000, fade.DurationMs);
        Assert.Equal(CueFadeCurve.Linear, fade.Curve);
        Assert.True(fade.StopWhenSilent);
        Assert.True(fade.AlsoFadeVideoOpacity);
    }

    [Fact]
    public void LegacyCueList_WithoutFadeCues_LoadsUnchanged()
    {
        // Old files simply have no "fade" nodes - adding the kind must not disturb their round-trip.
        const string json = """
            {
              "name": "Old show",
              "nodes": [
                { "kind": "media", "number": "1", "label": "Song" },
                { "kind": "jump", "number": "2", "label": "Loop" }
              ]
            }
            """;
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.IsType<MediaCueNode>(loaded.Nodes[0]);
        Assert.IsType<JumpCueNode>(loaded.Nodes[1]);
        // The quoted form is the discriminator value; a bare "fade" would also match mediaCue "fadeInMs".
        Assert.DoesNotContain("\"fade\"", JsonSerializer.Serialize(loaded, CueListJsonContext.Default.CueList));
    }

    [Fact]
    public void FadeTargetsText_ResolvesNumbersAndRangesToIds_AndReportsUnknowns()
    {
        var one = new MediaCueNode { Number = "1" };
        var two = new MediaCueNode { Number = "2" };
        var three = new MediaCueNode { Number = "3" };
        var fade = new FadeCueNode { Number = "4" };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [one, two, three, fade] }]);
        var fadeVm = vm.SelectedCueList!.Nodes[^1];
        vm.SelectedCueNode = fadeVm;

        vm.SelectedFadeTargetsText = "1-2, 99";

        Assert.Equal([one.Id, two.Id], fadeVm.FadeTargetIds); // range expanded, stored as stable IDs
        Assert.Contains("99", vm.StatusMessage);              // unknown number reported
        Assert.Equal("1, 2", vm.SelectedFadeTargetsText);     // display round-trips back to numbers

        // Groups are valid fade targets (they expand to their media at fire time).
        var group = new CueGroupNode { Number = "10", Children = { new MediaCueNode { Number = "10.1" } } };
        vm.ApplyCueLists([new CueList { Nodes = [group, fade] }]);
        var fadeVm2 = vm.SelectedCueList!.Nodes[^1];
        vm.SelectedCueNode = fadeVm2;
        vm.SelectedFadeTargetsText = "10";
        Assert.Equal([group.Id], fadeVm2.FadeTargetIds);
    }

    [Fact]
    public void AddFadeCue_TargetsTheSelectedMediaCue_ElseDefaultsToAllPlaying()
    {
        var vm = new CuePlayerViewModel();
        vm.AddCueListCommand.Execute(null);
        var media = vm.AddEmptyMediaCue()!;
        vm.SelectedCueNode = media;

        vm.AddFadeCueCommand.Execute(null);
        var fade = vm.SelectedCueList!.Nodes[^1];
        Assert.Equal(CueNodeKind.Fade, fade.Kind);
        Assert.Equal([media.Id], fade.FadeTargetIds); // the "fade this out" gesture
        Assert.False(fade.FadeTargetAllPlaying);
        Assert.Equal(3000, fade.DurationMs);
        var model = Assert.IsType<FadeCueNode>(fade.ToModel());
        Assert.True(model.StopWhenSilent);
        Assert.True(model.AlsoFadeVideoOpacity);

        // Without a fade-able selection the new cue defaults to "fade everything playing".
        vm.SelectedCueNode = null;
        vm.AddFadeCueCommand.Execute(null);
        var allFade = vm.SelectedCueList!.Nodes[^1];
        Assert.Empty(allFade.FadeTargetIds);
        Assert.True(allFade.FadeTargetAllPlaying);
    }

    [Fact]
    public void TreeColumns_ShowFadeKindAndTargets()
    {
        var song = new MediaCueNode { Number = "1", Label = "Song" };
        var fade = new FadeCueNode { Number = "2", TargetCueIds = [song.Id], TargetLevelDb = -10 };
        var all = new FadeCueNode { Number = "3", TargetAllPlaying = true };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [song, fade, all] }]);

        var fadeVm = vm.SelectedCueList!.Nodes[1];
        var allVm = vm.SelectedCueList.Nodes[2];
        Assert.Equal(HaPlay.Resources.Strings.CueKindFadeLabel, fadeVm.KindLabel);
        Assert.Equal("Fade → #1 (-10 dB)", fadeVm.TargetDisplay);
        Assert.Equal("Fade → all playing (silence)", allVm.TargetDisplay);
    }

    [Fact]
    public async Task Go_InvokesFadeCueExecutor_WithExplicitTargetsExpandedThroughGroups()
    {
        var solo = new MediaCueNode { Number = "1" };
        var childA = new MediaCueNode { Number = "2.1" };
        var childB = new MediaCueNode { Number = "2.2" };
        var group = new CueGroupNode { Number = "2", Children = [childA, childB] };
        var fade = new FadeCueNode
        {
            Number = "3",
            TargetCueIds = [solo.Id, group.Id],
            TargetLevelDb = -20,
        };
        // Fade execution hops onto the UI dispatcher for target resolution, so the test body must run
        // inside the headless session (which pumps that dispatcher while the body awaits).
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FadeCueTests).Assembly);
        await session.DispatchAsync(async () =>
        {
            var vm = new CuePlayerViewModel();
            vm.ApplyCueLists([new CueList { Nodes = [solo, group, fade] }]);
            // GO dispatches cue execution to a worker (fire-and-forget, like the Jump/Action tests) -
            // observe the executor through a TaskCompletionSource instead of asserting right after GO.
            var executed = new TaskCompletionSource<(FadeCueNode Node, IReadOnlyList<Guid> Targets)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FadeCueExecutor = (node, targets, _) =>
            {
                executed.TrySetResult((node, targets));
                return Task.FromResult<string?>(null);
            };

            vm.SelectedCueNode = vm.SelectedCueList!.Nodes[^1];
            vm.StandbySelectedCommand.Execute(null);
            await vm.GoCommand.ExecuteAsync(null);

            var (executedNode, executedTargets) = await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(-20, executedNode.TargetLevelDb);
            Assert.Equal([solo.Id, childA.Id, childB.Id], executedTargets); // group expanded to its media
        });
    }

    [Fact]
    public async Task Go_FadeAllPlaying_ResolvesToActiveCues_AndIsANoOpWhenNothingPlays()
    {
        var song = new MediaCueNode { Number = "1" };
        var fade = new FadeCueNode { Number = "2", TargetAllPlaying = true };
        // Runs inside the headless session: the fade path hops onto the UI dispatcher, which only
        // pumps while a session-dispatched body awaits.
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FadeCueTests).Assembly);
        await session.DispatchAsync(async () =>
        {
            var vm = new CuePlayerViewModel();
            vm.ApplyCueLists([new CueList { Nodes = [song, fade] }]);
            var calls = new ConcurrentQueue<IReadOnlyList<Guid>>();
            var called = new TaskCompletionSource<IReadOnlyList<Guid>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FadeCueExecutor = (_, targets, _) =>
            {
                calls.Enqueue(targets);
                called.TrySetResult(targets);
                return Task.FromResult<string?>(null);
            };

            // Nothing playing: the fade is a benign no-op (no executor call, no failure status). Cue
            // execution is dispatched to a worker after GO, so give the no-op a moment to settle.
            var fadeVm = vm.SelectedCueList!.Nodes[^1];
            vm.SelectedCueNode = fadeVm;
            vm.StandbySelectedCommand.Execute(null);
            await vm.GoCommand.ExecuteAsync(null);
            await Task.Delay(250);
            Assert.Empty(calls);

            // With an active cue, "all playing" resolves to the live transport set.
            vm.OnCueStarted(song.Id);
            vm.SelectedCueNode = fadeVm;
            vm.StandbySelectedCommand.Execute(null);
            await vm.GoCommand.ExecuteAsync(null);
            var targets = await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([song.Id], targets);
            Assert.Single(calls);
        });
    }
}
