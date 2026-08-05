using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The video output and composition panes edit the document.
/// </summary>
/// <remarks>
/// Both were drawn complete against literals — a composition picker pinned to <c>SelectedIndex="0"</c>,
/// a screen picker over three invented resolutions, a size of "1920×1080" no project could change.
/// An operator could not point an output at a composition at all, which is the first thing anybody does.
/// </remarks>
public class VideoOutputPaneTests
{
    private static (VideoViewModel Video, VideoOutputDefinition Output, CompositionDefinition Composition)
        WithOutput(ShellViewModel shell, VideoOutputKind kind = VideoOutputKind.LocalScreen)
    {
        var composition = new CompositionDefinition { Name = "Cyc", Width = 1280, Height = 720 };
        var output = new VideoOutputDefinition { Name = "Projector", Kind = kind, CompositionId = composition.Id };

        shell.Project.Compositions.Add(composition);
        shell.Project.VideoOutputs.Add(output);

        var video = new VideoViewModel(shell.Project, shell.Runtime, shell.Journal);
        video.SelectedOutput = video.Outputs.Single(row => row.Id == output.Id);

        return (video, output, composition);
    }

    /// <summary>
    /// An output already on a canvas can be MOVED to another, from the destination's own rail.
    /// </summary>
    /// <remarks>
    /// This used to drive a "Shows" picker on the output pane. Assignment lives on the composition now,
    /// so the picker went and the second, unreachable way to write the same field went with it — but
    /// the case it covered is real and belongs to the rail: an output already showing something is
    /// still OFFERED, saying where it would come from, because hiding it leaves an operator hunting for
    /// a projector that is simply pointed elsewhere with nothing on screen saying where.
    /// </remarks>
    [Fact]
    public Task AnOutputAlreadyOnACanvasCanBeMovedToAnother() => ShellFixture.WithShell(shell =>
    {
        var (video, output, from) = WithOutput(shell);
        var to = shell.Project.Compositions.First(item => item.Id != from.Id);

        video.SelectedCompositionId = to.Id;

        // Named with where it would come from, so a move never looks like a fresh assignment.
        Assert.Contains(
            video.AssignableOutputs,
            entry => entry.Contains(output.Name, StringComparison.Ordinal)
                     && entry.Contains(from.Name, StringComparison.Ordinal));

        video.AssignableIndex = video.AssignableOutputs.ToList()
            .FindIndex(entry => entry.Contains(output.Name, StringComparison.Ordinal));
        video.AssignSelectedOutput();

        Assert.Equal(to.Id, output.CompositionId);
    });

    [Fact]
    public Task ChoosingAScreenReachesTheDocumentAsAHint() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);
        video.SetScreens(["1 · 1920×1080", "2 · 1920×1080"]);

        video.OutputScreenIndex = 2;

        Assert.Equal("2", output.TargetHint);
    });

    [Fact]
    public Task AnywhereClearsTheScreenHint() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);
        video.SetScreens(["1 · 1920×1080"]);
        video.OutputScreenIndex = 1;

        video.OutputScreenIndex = 0;

        // Empty rather than "0": a hint that matches nothing is how every other absent hint is spelled.
        Assert.Equal("", output.TargetHint);
    });

    [Fact]
    public Task TheModeSegmentSetsFullscreen() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);

        video.OutputFullscreenIndex = 1;
        Assert.False(output.Fullscreen);

        video.OutputFullscreenIndex = 0;
        Assert.True(output.Fullscreen);
    });

    [Fact]
    public Task TheIdleFallbackReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);

        video.OutputIdleFallback = "/library/venue-logo.png";

        Assert.Equal("/library/venue-logo.png", output.IdleFallbackPath);
    });

    [Fact]
    public Task RequiredReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);

        video.OutputRequired = true;

        Assert.True(output.Required);
    });

    // ── the clean toggle keeps the warp ────────────────────────────────────────────────────────

    [Fact]
    public Task SwitchingToCleanKeepsTheAuthoredSections() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);
        output.Mapping.Add(new MappingSection { Name = "Left" });
        output.Mapping.Add(new MappingSection { Name = "Right" });

        video.OutputMappingIndex = 1;

        // The whole point: "show this output clean tonight" must not cost an hour of warping.
        Assert.False(output.MappingEnabled);
        Assert.Equal(2, output.Mapping.Count);
        Assert.False(output.IsMapped);

        video.OutputMappingIndex = 0;
        Assert.True(output.IsMapped);
    });

    [Fact]
    public Task ACleanOutputRendersUnwarped() => ShellFixture.WithShell(shell =>
    {
        var (video, output, composition) = WithOutput(shell);
        output.Mapping.Add(new MappingSection { Name = "Left", TargetWidth = 0.5 });

        Assert.NotNull(HaCue2.Engine.OutputMapping.Spec(output, composition.Width, composition.Height));

        video.OutputMappingIndex = 1;

        // Bypassed at the ENGINE, not merely in the list's label — otherwise "clean" would be a setting
        // that changed what the pane said and nothing about what hit the wall.
        Assert.Null(HaCue2.Engine.OutputMapping.Spec(output, composition.Width, composition.Height));
    });

    [Fact]
    public Task TheMappingNoteTellsTheTwoKindsOfCleanApart() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);

        Assert.Contains("no sections", video.MappingNote, StringComparison.Ordinal);

        output.Mapping.Add(new MappingSection());
        video.OutputMappingIndex = 1;
        Assert.Contains("bypassed", video.MappingNote, StringComparison.Ordinal);

        video.OutputMappingIndex = 0;
        Assert.Contains("in force", video.MappingNote, StringComparison.Ordinal);
    });

    // ── the composition pane ───────────────────────────────────────────────────────────────────

    [Fact]
    public Task EditingTheSizeReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        video.CompositionSize = "1920×1080";

        Assert.Equal(1920, composition.Width);
        Assert.Equal(1080, composition.Height);
    });

    [Fact]
    public Task ASizeTypedWithAnAsciiXIsAccepted() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        // The field renders "×", which is not on anybody's keyboard.
        video.CompositionSize = "800x600";

        Assert.Equal(800, composition.Width);
        Assert.Equal(600, composition.Height);
    });

    [Fact]
    public Task ANonsenseSizeIsRefusedRatherThanApplied() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        video.CompositionSize = "very large";

        // A canvas of 0×0 would take every placement in the show with it.
        Assert.Equal(1280, composition.Width);
        Assert.Equal(720, composition.Height);
    });

    [Fact]
    public Task ASizeIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        video.CompositionSize = "1920×1080";
        shell.Undo();

        // Both halves together: an undo that took the width back without the height would leave a
        // canvas nobody authored.
        Assert.Equal(1280, composition.Width);
        Assert.Equal(720, composition.Height);
    });

    [Fact]
    public Task EditingTheRateReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        video.CompositionRate = "29.97";

        Assert.Equal(29.97, composition.FramesPerSecond, 2);
    });

    [Fact]
    public Task AnImpossibleRateIsRefused() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);
        var authored = composition.FramesPerSecond;

        // Against whatever the canvas was actually authored at, not a literal: a refused edit leaves
        // the rate alone, which is the claim — the default it happens to start from is not.
        video.CompositionRate = "0";
        Assert.Equal(authored, composition.FramesPerSecond);

        video.CompositionRate = "1000";
        Assert.Equal(authored, composition.FramesPerSecond);
    });

    [Fact]
    public Task TheIdleImageReachesTheDocument() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        video.CompositionIdleImage = "/library/idle.png";

        Assert.Equal("/library/idle.png", composition.IdleImagePath);
    });

    [Fact]
    public Task TheCompositionPaneFollowsTheSelectedOutput() => ShellFixture.WithShell(shell =>
    {
        var (video, _, composition) = WithOutput(shell);

        // Selecting the projector and then editing a size should edit what the projector shows.
        Assert.Equal(composition.Name, video.CompositionHeader);
        Assert.Equal("1280×720", video.CompositionSize);
    });

    [Fact]
    public Task EveryOutputEditIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var (video, output, _) = WithOutput(shell);

        video.OutputFullscreenIndex = 1;
        shell.Undo();

        Assert.True(output.Fullscreen);
    });
}
