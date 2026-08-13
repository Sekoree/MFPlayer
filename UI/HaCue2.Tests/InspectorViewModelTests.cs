using HaCue2.Core.Model;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The cue editor's fields.
/// </summary>
/// <remarks>
/// Every one of these was a hardcoded literal in the view until this pass, so the tests are as much
/// about "does the field reach the document at all" as about the parsing. The parsing matters too: the
/// display writes "4.0 s" and a parser that only took bare numbers would refuse to read back the value
/// it had just written, which is the commonest way a field appears not to work.
/// </remarks>
public class InspectorViewModelTests
{
    [Fact]
    public Task EndTargetsAndSelectiveVisualizerFeedsReachTheDocument() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists[0];
        var source = new MediaCueNode { Number = "901", Label = "Source", MediaPath = "source.mov" };
        var target = new MediaCueNode { Number = "902", Label = "Target", MediaPath = "target.mov" };
        var visualizer = new VisualizerCueNode { Number = "903", Label = "Viz" };
        list.Cues.AddRange([source, target, visualizer]);

        shell.Cues.Inspector.Show([source.Id]);
        var targetOption = shell.Cues.Inspector.EndTargetOptions
            .Select((text, index) => (text, index))
            .Single(item => item.text.Contains("902", StringComparison.Ordinal)).index;
        shell.Cues.Inspector.EndTargetIndex = targetOption;
        shell.Cues.Inspector.Show([source.Id]);
        shell.Cues.Inspector.SendToVisualizerValue = true;

        shell.Cues.Inspector.Show([visualizer.Id]);
        shell.Cues.Inspector.VisualizerFeedAllValue = false;
        shell.Cues.Inspector.Show([visualizer.Id]);
        shell.Cues.Inspector.VisualizerFeedCueNumbers = "901";

        Assert.Equal(target.Id, source.EndTargetCueId);
        Assert.True(source.SendToVisualizer);
        Assert.False(visualizer.FeedAll);
        Assert.Equal([source.Id], visualizer.FeedCueIds);
    });

    [Fact]
    public Task PreAndPostWaitRoundTrip() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        shell.Cues.Inspector.PreWaitValue = "2.5 s";
        shell.Cues.Inspector.PostWaitValue = "1.0";

        Assert.Equal(2_500, bed.PreWaitMs);
        Assert.Equal(1_000, bed.PostWaitMs);

        // Read back in the form the field renders, which is what the operator sees next.
        Assert.Equal("2.5", shell.Cues.Inspector.PreWaitValue);
    });

    [Fact]
    public Task ANegativeDurationIsRefusedRatherThanClamped() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);
        bed.PreWaitMs = 3_000;

        shell.Cues.Inspector.PreWaitValue = "-2";

        // The field keeps the old value rather than silently becoming zero.
        Assert.Equal(3_000, bed.PreWaitMs);
    });

    [Fact]
    public Task FadeOutReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        shell.Cues.Inspector.FadeOutValue = "6";

        // It was a literal "4.0 s" in the view - the picker beside it edited a real curve while the
        // duration next to it edited nothing.
        Assert.Equal(6_000, bed.FadeOutMs);
    });

    [Fact]
    public Task TheTriggerModeReachesTheDocumentForEveryKind() => ShellFixture.WithShell(shell =>
    {
        var jump = shell.Project.AllCues().OfType<JumpCueNode>().First();
        ShellFixture.Select(shell.Cues, jump.Id);

        shell.Cues.Inspector.TriggerIndex = (int)CueTrigger.Continue;

        // On the BASE cue type, so it works on control-flow cues too: "wait, then tell the desk" is an
        // ordinary thing to author onto an action cue, and the transport honours it there.
        Assert.Equal(CueTrigger.Continue, jump.Trigger);
    });

    [Fact]
    public Task AnUntrimmedOutPointReadsAsEndRatherThanZero() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        // Zero means "play through" in the model; showing that as "0.0" would read as an out-point at
        // the very start - a cue that plays nothing.
        Assert.Equal("end", shell.Cues.Inspector.TrimOutValue);
    });

    [Fact]
    public Task FiniteMediaCuesPutTrimControlsInGeneralWithoutAClipTab() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        Assert.DoesNotContain("CLIP", shell.Cues.Inspector.Tabs);
        Assert.Equal("GENERAL", shell.Cues.Inspector.SelectedTab);
        Assert.True(shell.Cues.Inspector.IsGeneralPane);
        Assert.True(shell.Cues.Inspector.CanTrimMedia);

        shell.Cues.Inspector.TrimInValue = "1.5";

        Assert.Equal(1_500, bed.TrimInMs);
    });

    [Fact]
    public Task TypingEndClearsTheOutPoint() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);
        bed.TrimOutMs = 12_000;

        shell.Cues.Inspector.TrimOutValue = "end";

        Assert.Equal(0, bed.TrimOutMs);
    });

    [Fact]
    public Task LoopRoundTrips() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        var before = bed.Loop;
        shell.Cues.Inspector.LoopValue = !before;

        Assert.Equal(!before, bed.Loop);
    });

    [Fact]
    public Task EveryFieldEditIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var bed = ShellFixture.Bed(shell.Project);
        ShellFixture.Select(shell.Cues, bed.Id);

        var before = bed.Label;
        shell.Cues.Inspector.LabelValue = "Renamed";
        Assert.Equal("Renamed", bed.Label);

        shell.Undo();

        // "A surface that mutates directly is a surface whose undo silently does nothing."
        Assert.Equal(before, shell.Project.FindCue(bed.Id)!.Label);
    });

    [Fact]
    public Task AMultiSelectionShowsMixedValuesAsADashAndOnlyWritesWhenTouched() =>
        ShellFixture.WithShell(shell =>
        {
            var list = shell.Project.CueLists.Single(item => item.Name == "Video");
            var cues = list.Flatten().OfType<MediaCueNode>().Take(2).ToList();
            cues[0].Label = "one";
            cues[1].Label = "two";

            shell.Cues.Inspector.Show([cues[0].Id, cues[1].Id]);

            Assert.Equal("-", shell.Cues.Inspector.LabelValue);
            // Untouched, so both keep their own value. Showing the lead cue's instead would invite an
            // edit that silently overwrote the other with something nobody read.
            Assert.Equal("one", cues[0].Label);
            Assert.Equal("two", cues[1].Label);
        });

    [Fact]
    public Task AMultiSelectionEditIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var list = shell.Project.CueLists.Single(item => item.Name == "Video");
        var cues = list.Flatten().OfType<MediaCueNode>().Take(2).ToList();

        shell.Cues.Inspector.Show([cues[0].Id, cues[1].Id]);
        shell.Cues.Inspector.LabelValue = "both";

        Assert.Equal("both", cues[0].Label);
        Assert.Equal("both", cues[1].Label);

        shell.Undo();

        Assert.NotEqual("both", ((MediaCueNode)shell.Project.FindCue(cues[0].Id)!).Label);
        Assert.NotEqual("both", ((MediaCueNode)shell.Project.FindCue(cues[1].Id)!).Label);
    });

    [Fact]
    public Task TheTabSetFollowsTheCueKind() => ShellFixture.WithShell(shell =>
    {
        var patch = shell.Project.AllCues().OfType<PatchCueNode>().First();
        shell.Cues.Inspector.Show([patch.Id]);

        Assert.Contains("PATCH", shell.Cues.Inspector.Tabs);
        Assert.DoesNotContain("AUDIO", shell.Cues.Inspector.Tabs);
    });
}
