using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>The visualizer pane's HaPlay-parity fields (2026-08-14): the render override reads and
/// writes the "auto"/WxH form, and shuffle edits journal through the shared plumbing.</summary>
public sealed class VisualizerPaneParityTests
{
    private static VisualizerCueNode Selected(ShellViewModel shell)
    {
        var list = shell.Project.CueLists.First();
        var cue = new VisualizerCueNode { Number = "90", Label = "viz" };
        list.Cues.Add(cue);
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, cue.Id);
        return cue;
    }

    [Fact]
    public Task TheRenderSizeFieldSpeaksAutoAndTimesForm() => ShellFixture.WithShell(shell =>
    {
        var cue = Selected(shell);
        var pane = shell.Cues.Inspector.VisualizerPane;

        Assert.Equal("auto", pane.VisualizerRenderSizeValue);

        pane.VisualizerRenderSizeValue = "1280x720";
        Assert.Equal((1280, 720), (cue.RenderWidth, cue.RenderHeight));
        Assert.Equal("1280×720", pane.VisualizerRenderSizeValue);

        // Unparseable input keeps the value, like every other field.
        pane.VisualizerRenderSizeValue = "banana";
        Assert.Equal((1280, 720), (cue.RenderWidth, cue.RenderHeight));

        pane.VisualizerRenderSizeValue = "auto";
        Assert.Equal((0, 0), (cue.RenderWidth, cue.RenderHeight));
    });

    [Fact]
    public Task ShuffleAndBeatSensitivityEditTheCue() => ShellFixture.WithShell(shell =>
    {
        var cue = Selected(shell);
        var pane = shell.Cues.Inspector.VisualizerPane;

        Assert.True(pane.VisualizerShuffleValue);
        pane.VisualizerShuffleValue = false;
        Assert.False(cue.ShufflePresets);

        pane.VisualizerBeatSensitivityValue = "2.5";
        Assert.Equal(2.5, cue.BeatSensitivity);

        // Clamped to projectM's 0..5 range rather than refused.
        pane.VisualizerBeatSensitivityValue = "9";
        Assert.Equal(5, cue.BeatSensitivity);
    });

    [Fact]
    public Task TheSkipButtonIsDisabledWithoutARunningRenderer() => ShellFixture.WithShell(shell =>
    {
        _ = Selected(shell);

        // No engine in the fixture, so nothing can be running - the button must know that.
        Assert.False(shell.Cues.Inspector.VisualizerPane.CanSkipPreset);
    });
}
