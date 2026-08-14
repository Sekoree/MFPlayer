using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The editor faults an operator hit on a real show, each pinned by the behaviour that was wrong.
/// </summary>
/// <remarks>
/// Every test here stands for something that LOOKED like it worked. A tab that snapped back, a preset
/// strip that was a caption, a multi-selection edit that landed on one cue - none of them threw, none
/// of them were visible in a screenshot, and all of them were found by using the app rather than by
/// reading it. That is what these are for.
/// </remarks>
public class EditorRegressionTests
{
    // ── the inspector's tab ───────────────────────────────────────────────────────────────────

    [Fact]
    public Task AnEditKeepsTheInspectorOnTheTabTheOperatorIsWorkingIn() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        // The operator walks to the Audio pane and edits a level, the way they would set eleven stems.
        shell.Cues.Inspector.SelectedTab = "AUDIO";
        shell.Cues.Inspector.LevelValue = "-6";

        // It used to land back on GENERAL - once per keystroke - because every edit re-ran the
        // opening-tab choice from scratch.
        Assert.Equal("AUDIO", shell.Cues.Inspector.SelectedTab);
        Assert.Equal(-6, bed.LevelDb);
    });

    [Fact]
    public Task AMultiEditKeepsTheInspectorOnTheTabTheOperatorIsWorkingIn()
        => ShellFixture.WithShell(shell =>
        {
            var list = shell.Project.CueLists[0];
            var left = new MediaCueNode { Number = "826", Label = "L", MediaPath = "/library/a.wav" };
            var right = new MediaCueNode { Number = "827", Label = "R", MediaPath = "/library/b.wav" };
            list.Cues.AddRange([left, right]);

            // Eleven stems selected, operator on the Audio pane adding sends. Applying an edit runs
            // the journal-changed refresh, which clears and then restores the tree selection - the
            // inspector sees exactly this Show sequence. With the tab memory only written for
            // SINGLE selections, the clear forgot "AUDIO" and the restore landed on GENERAL, once
            // per edit, with the operator mid-workflow.
            shell.Cues.Inspector.Show([left.Id, right.Id]);
            shell.Cues.Inspector.SelectedTab = "AUDIO";

            shell.Cues.Inspector.Show([]);
            shell.Cues.Inspector.Show([left.Id, right.Id]);

            Assert.Equal("AUDIO", shell.Cues.Inspector.SelectedTab);
        });

    [Fact]
    public Task ChangingTheSelectionStillChoosesATabRatherThanKeepingTheOldOne()
        => ShellFixture.WithShell(shell =>
        {
            var list = shell.Project.CueLists[0];
            var group = new GroupCueNode { Number = "800", Label = "Group" };
            list.Cues.Add(group);
            var bed = ShellFixture.Bed(shell.Project);

            shell.Cues.Inspector.Show([bed.Id]);
            shell.Cues.Inspector.SelectedTab = "AUDIO";

            // A group has no AUDIO tab at all, so keeping the open one is not even an option - but the
            // rule is about the SELECTION changing, not about the tab disappearing.
            shell.Cues.Inspector.Show([group.Id]);

            Assert.Contains(shell.Cues.Inspector.SelectedTab, shell.Cues.Inspector.Tabs);
            Assert.DoesNotContain("AUDIO", shell.Cues.Inspector.Tabs);
        });

    // ── the Video tab on a file with no video ─────────────────────────────────────────────────

    [Fact]
    public Task AFileWithNoVideoStreamHasNoVideoTab() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var stem = new MediaCueNode { Number = "810", Label = "Stem", MediaPath = "/library/stem.wav" };
        list.Cues.Add(stem);

        // What the probe says about a WAV: audio, and nothing a composition could show.
        shell.Cues.MediaFacts = _ => new MediaFacts
        {
            Duration = TimeSpan.FromMinutes(3),
            AudioTracks = [new MediaTrack(0, "stereo", "", null, 2, 0, 0, true, false, true)],
        };

        shell.Cues.Inspector.Show([stem.Id]);

        Assert.DoesNotContain("VIDEO", shell.Cues.Inspector.Tabs);
        // The rest of the media tabs are untouched - an audio cue still has fades and a waveform.
        Assert.Contains("AUDIO", shell.Cues.Inspector.Tabs);
        Assert.Contains("PREVIEW", shell.Cues.Inspector.Tabs);
    });

    [Fact]
    public Task AFileNobodyHasProbedKeepsItsVideoTab() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var unknown = new MediaCueNode { Number = "811", Label = "Unknown", MediaPath = "/library/x.mkv" };
        list.Cues.Add(unknown);

        shell.Cues.MediaFacts = _ => null;
        shell.Cues.Inspector.Show([unknown.Id]);

        // Hiding a tab because the answer has not arrived is the same failure as painting a cue red
        // before anybody looked at it.
        Assert.Contains("VIDEO", shell.Cues.Inspector.Tabs);
    });

    // ── effect lanes offered per kind ─────────────────────────────────────────────────────────

    [Fact]
    public Task AnAudioOnlyCueIsNotOfferedAnOpacityLane() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var stem = new MediaCueNode { Number = "820", Label = "Stem", MediaPath = "/library/stem.wav" };
        list.Cues.Add(stem);

        shell.Cues.MediaFacts = _ => new MediaFacts
        {
            Duration = TimeSpan.FromMinutes(3),
            AudioTracks = [new MediaTrack(0, "stereo", "", null, 2, 0, 0, true, false, true)],
        };

        shell.Cues.Inspector.Show([stem.Id]);

        Assert.True(shell.Cues.Inspector.Video.CanAddVolumeLane);
        Assert.False(shell.Cues.Inspector.Video.CanAddOpacityLane);
        // Outbound ramps need nothing from the media - they send over the cue's length.
        Assert.True(shell.Cues.Inspector.Video.CanAddOutboundLane);
    });

    [Fact]
    public Task AVisualizerOffersOpacityOnlyForAConcretePlacement() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var visualizer = new VisualizerCueNode { Number = "821", Label = "Viz" };
        list.Cues.Add(visualizer);

        shell.Cues.Inspector.Show([visualizer.Id]);

        Assert.False(shell.Cues.Inspector.Video.CanAddVolumeLane);
        Assert.False(shell.Cues.Inspector.Video.CanAddOpacityLane);

        visualizer.Placements.Add(new LayerPlacement
        {
            CompositionId = shell.Project.Compositions[0].Id,
            LayerIndex = 2,
        });
        shell.Cues.Inspector.Reload();
        Assert.True(shell.Cues.Inspector.Video.CanAddOpacityLane);
    });

    [Fact]
    public Task AddingALaneUsesTheKindTheRowNamesEvenWhenOneWasFilteredOut()
        => ShellFixture.WithShell(shell =>
        {
            var list = shell.Project.CueLists[0];
            var visualizer = new VisualizerCueNode
            {
                Number = "822",
                Label = "Viz",
                Placements =
                [
                    new LayerPlacement
                    {
                        CompositionId = shell.Project.Compositions[0].Id,
                        LayerIndex = 2,
                    },
                ],
            };
            list.Cues.Add(visualizer);

            shell.Cues.Inspector.Show([visualizer.Id]);

            // The command index is a UI property slot, not a position in the filtered list.
            shell.Cues.Inspector.Video.AddLane(AutomationPropertyIds.PlacementOpacity);

            Assert.Equal(
                [AutomationPropertyIds.PlacementOpacity],
                visualizer.AutomationTracks.Select(track => track.Target.PropertyId));

            // And the kind that does not apply is refused outright rather than silently added.
            shell.Cues.Inspector.Show([visualizer.Id]);
            shell.Cues.Inspector.Video.AddLane(AutomationPropertyIds.CueVolume);
            Assert.Single(visualizer.AutomationTracks);
        });

    [Fact]
    public Task ATextCardCanCarryTheLanesItsModelHolds() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var card = new TextCueNode { Number = "823", Label = "Card", Text = "Doors" };
        list.Cues.Add(card);

        shell.Cues.Inspector.Show([card.Id]);

        // TextCueNode has EffectLanes and is given the EFFECTS tab; the lane lookup simply omitted it,
        // so the pane read "this kind cannot carry automation" over a cue that could.
        Assert.True(shell.Cues.Inspector.Video.CanCarryLanes);
    });

    // ── multi-selection ───────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AKindPaneEditAppliesToEverySelectedCue() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var first = new FadeCueNode { Number = "830", Label = "Fade A", DurationMs = 1_000 };
        var second = new FadeCueNode { Number = "831", Label = "Fade B", DurationMs = 2_000 };
        list.Cues.AddRange([first, second]);

        shell.Cues.Inspector.Show([first.Id, second.Id]);
        shell.Cues.Inspector.FadePane.FadeDurationValue = "4 s";

        // It used to change the lead only, while the field showed the new value for both.
        Assert.Equal(4_000, first.DurationMs);
        Assert.Equal(4_000, second.DurationMs);
    });

    [Fact]
    public Task AMultiSelectionEditIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var first = new FadeCueNode { Number = "832", Label = "Fade A", DurationMs = 1_000 };
        var second = new FadeCueNode { Number = "833", Label = "Fade B", DurationMs = 2_000 };
        list.Cues.AddRange([first, second]);

        shell.Cues.Inspector.Show([first.Id, second.Id]);
        shell.Cues.Inspector.FadePane.FadeDurationValue = "4 s";

        Assert.True(shell.Journal.Undo());

        Assert.Equal(1_000, first.DurationMs);
        Assert.Equal(2_000, second.DurationMs);
    });

    [Fact]
    public Task TickingAFadeTargetAddsItToEachSelectedCueRatherThanCopyingTheLeadsSet()
        => ShellFixture.WithShell(shell =>
        {
            var channels = shell.Project.AudioPatch.LogicalChannels
                .OrderBy(channel => channel.SortOrder)
                .ToList();
            Assert.True(channels.Count >= 2);

            var list = shell.Project.CueLists[0];
            var first = new FadeCueNode { Number = "834", Label = "Fade A" };
            var second = new FadeCueNode { Number = "835", Label = "Fade B" };
            second.TargetChannelIds.Add(channels[1].Id);
            list.Cues.AddRange([first, second]);

            shell.Cues.Inspector.Show([first.Id, second.Id]);
            shell.Cues.Inspector.FadePane.FadeTargets
                .First(toggle => toggle.Name == channels[0].Name)
                .IsSelected = true;

            // The second cue KEEPS the target it already had and gains the ticked one. Copying the
            // lead's finished list across would have wiped it.
            Assert.Equal([channels[0].Id], first.TargetChannelIds);
            Assert.Contains(channels[0].Id, second.TargetChannelIds);
            Assert.Contains(channels[1].Id, second.TargetChannelIds);
        });

    [Fact]
    public Task ASendGestureRoutesEverySelectedCue() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var left = new MediaCueNode { Number = "840", Label = "L", MediaPath = "/library/a.wav" };
        var right = new MediaCueNode { Number = "841", Label = "R", MediaPath = "/library/b.wav" };
        list.Cues.AddRange([left, right]);

        shell.Cues.Inspector.Show([left.Id, right.Id]);
        shell.Cues.Inspector.Audio.ApplySendGesture(
            new HaCue2.Controls.MatrixGesture(0, 0, HaCue2.Controls.MatrixGestureKind.Toggle, 0));

        var channel = shell.Cues.Inspector.Audio.SendColumns[0].ChannelId;
        Assert.Contains(left.Sends, send => send.LogicalChannelId == channel);
        Assert.Contains(right.Sends, send => send.LogicalChannelId == channel);
    });

    // ── send presets ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task TheStereoPresetRoutesBothSourceChannelsAcrossTheSelection()
        => ShellFixture.WithShell(shell =>
        {
            var list = shell.Project.CueLists[0];
            var first = new MediaCueNode { Number = "850", Label = "A", MediaPath = "/library/a.wav" };
            var second = new MediaCueNode { Number = "851", Label = "B", MediaPath = "/library/b.wav" };
            list.Cues.AddRange([first, second]);

            shell.Cues.Inspector.Show([first.Id, second.Id]);
            Assert.True(shell.Cues.Inspector.Audio.HasSendPresetTarget);

            shell.Cues.Inspector.Audio.ApplySendPreset("stereo");

            foreach (var cue in new[] { first, second })
            {
                Assert.Equal(2, cue.Sends.Count);
                Assert.Contains(cue.Sends, send => send.SourceChannel == 0);
                Assert.Contains(cue.Sends, send => send.SourceChannel == 1);
                Assert.All(cue.Sends, send => Assert.Equal(0, send.GainDb));
                Assert.All(cue.Sends, send => Assert.False(send.Muted));
            }

            // The two sends land on DIFFERENT logical outputs - a preset that put both on one would be
            // the mono fault it exists to prevent.
            Assert.Equal(2, first.Sends.Select(send => send.LogicalChannelId).Distinct().Count());
        });

    [Fact]
    public Task TheMonoPresetPutsOneSourceChannelOnBothSides() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var cue = new MediaCueNode { Number = "852", Label = "Mono", MediaPath = "/library/m.wav" };
        list.Cues.Add(cue);

        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("monoL");

        Assert.Equal(2, cue.Sends.Count);
        Assert.All(cue.Sends, send => Assert.Equal(0, send.SourceChannel));
        Assert.Equal(2, cue.Sends.Select(send => send.LogicalChannelId).Distinct().Count());
    });

    [Fact]
    public Task TheSwapPresetExchangesTheTwoSourceChannels() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var cue = new MediaCueNode { Number = "853", Label = "Swap", MediaPath = "/library/s.wav" };
        list.Cues.Add(cue);

        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("stereo");
        var straight = cue.Sends
            .ToDictionary(send => send.SourceChannel, send => send.LogicalChannelId);

        // Re-stated because an applied edit restores the inspector's selection from the cue tree,
        // which this test never touched.
        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("swap");
        var swapped = cue.Sends
            .ToDictionary(send => send.SourceChannel, send => send.LogicalChannelId);

        Assert.Equal(straight[0], swapped[1]);
        Assert.Equal(straight[1], swapped[0]);
    });

    [Fact]
    public Task TheClearPresetRemovesEverySend() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var cue = new MediaCueNode { Number = "854", Label = "Clear", MediaPath = "/library/c.wav" };
        list.Cues.Add(cue);

        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("stereo");
        Assert.NotEmpty(cue.Sends);

        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("clear");

        Assert.Empty(cue.Sends);
    });

    [Fact]
    public Task ASendPresetIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var cue = new MediaCueNode { Number = "855", Label = "Undo", MediaPath = "/library/u.wav" };
        list.Cues.Add(cue);

        shell.Cues.Inspector.Show([cue.Id]);
        shell.Cues.Inspector.Audio.ApplySendPreset("stereo");

        Assert.True(shell.Journal.Undo());
        Assert.Empty(cue.Sends);
    });
}
