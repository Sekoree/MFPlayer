using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The per-kind inspector panes for the cues the transport resolves app-side.
/// </summary>
/// <remarks>
/// These cues EXECUTE — fade, jump, patch and action all have real behaviour behind them — but until
/// this pass their panes were literals, so the only working ones in existence were those the fixture
/// generator wrote. An operator could fire a fade cue and not set its duration. Each test below is
/// therefore about one question: does this field reach the document, and can it be undone.
/// </remarks>
public class ControlFlowPaneTests
{
    /// <summary>
    /// Selects a cue THROUGH THE TREE, the way a click does.
    /// </summary>
    /// <remarks>
    /// Not <c>Inspector.Show</c> directly. Every journaled edit raises <c>Journal.Changed</c>, which
    /// rebuilds the cue tree, which clears the grid's selection and re-announces it — and the shell
    /// restores the selection BY ID from the tree. A test that set the inspector's selection without
    /// the tree knowing lost it on its own first edit, so only the first assignment in each test stuck.
    /// Driving it the way the UI does is what makes these tests mean anything.
    /// </remarks>
    private static T Select<T>(ShellViewModel shell) where T : CueNode
    {
        var cue = shell.Project.AllCues().OfType<T>().First();
        ShellFixture.Select(shell.Cues, cue.Id);
        return cue;
    }

    /// <summary>Adds a cue to the first list and selects it through the tree.</summary>
    private static T Add<T>(ShellViewModel shell, T cue) where T : CueNode
    {
        shell.Project.CueLists[0].Cues.Add(cue);
        shell.Cues.Refresh();
        ShellFixture.Select(shell.Cues, cue.Id);
        return cue;
    }

    // ── fade ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AFadesDurationAndLevelReachTheDocument() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);

        shell.Cues.Inspector.FadeDurationValue = "5.5";
        shell.Cues.Inspector.FadeToLevelValue = "-6";

        Assert.Equal(5_500, fade.DurationMs);
        Assert.Equal(-6, fade.ToLevelDb, 3);
    });

    [Fact]
    public Task AFadeAcceptsTheWordForSilence() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);
        fade.ToLevelDb = 0;
        shell.Cues.Inspector.Reload();

        shell.Cues.Inspector.FadeToLevelValue = "-inf";

        // Typing the word is the commonest way to author a fade-out; refusing it would send the
        // operator to look up a number.
        Assert.Equal(GainRange.SilenceFloorDb, fade.ToLevelDb, 3);
        Assert.Equal("−inf", shell.Cues.Inspector.FadeToLevelValue);
    });

    [Fact]
    public Task FadeTargetsToggleThroughTheJournal() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);
        var targets = shell.Cues.Inspector.FadeTargets;

        Assert.NotEmpty(targets);
        Assert.All(targets, toggle => Assert.False(toggle.IsSelected));

        targets[0].IsSelected = true;
        Assert.Single(fade.TargetChannelIds);

        shell.Undo();
        Assert.Empty(((FadeCueNode)shell.Project.FindCue(fade.Id)!).TargetChannelIds);
    });

    [Fact]
    public Task AFadeWithNoTargetSaysSo() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);
        fade.FadeEverythingSounding = false;
        shell.Cues.Inspector.Reload();

        Assert.Contains("do nothing", shell.Cues.Inspector.FadeTargetHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task FadeFlagsRoundTrip() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);

        shell.Cues.Inspector.FadeStopsTargetsValue = false;
        Assert.False(fade.StopTargetsWhenComplete);

        shell.Cues.Inspector.FadeEverythingValue = false;
        Assert.False(fade.FadeEverythingSounding);
    });

    // ── jump ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AJumpTargetCanBeChosenAndCleared() => ShellFixture.WithShell(shell =>
    {
        var jump = Select<JumpCueNode>(shell);

        // The seeded jump already points somewhere; move it, then clear it.
        shell.Cues.Inspector.JumpTargetIndex = 2;
        Assert.Single(jump.TargetCueIds);

        var chosen = jump.TargetCueIds[0];
        Assert.NotNull(shell.Project.FindCue(chosen));

        shell.Cues.Inspector.JumpTargetIndex = 0;
        Assert.Empty(jump.TargetCueIds);
    });

    [Fact]
    public Task AJumpNeverOffersItselfAsATarget() => ShellFixture.WithShell(shell =>
    {
        var jump = Select<JumpCueNode>(shell);

        // A cue that jumps to itself is an infinite loop the chain bound would catch at run time; not
        // offering it is better than reporting it afterwards.
        var label = $"Q{jump.Number.Text} · {jump.Label}";
        Assert.DoesNotContain(label, shell.Cues.Inspector.JumpTargets);
    });

    [Fact]
    public Task ADeletedJumpTargetReadsAsNoneRatherThanTheWrongCue() => ShellFixture.WithShell(shell =>
    {
        var jump = Select<JumpCueNode>(shell);
        jump.TargetCueIds = [Guid.NewGuid()];
        shell.Cues.Inspector.Reload();

        // Pointing at whatever now occupies that position would silently retarget the jump.
        Assert.Equal(0, shell.Cues.Inspector.JumpTargetIndex);
        Assert.Contains("no longer exists", shell.Cues.Inspector.JumpHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task JumpFlagsRoundTrip() => ShellFixture.WithShell(shell =>
    {
        var jump = Select<JumpCueNode>(shell);

        shell.Cues.Inspector.JumpPickAtRandomValue = true;
        shell.Cues.Inspector.JumpFiresOnArrivalValue = true;
        shell.Cues.Inspector.JumpConditionIndex = (int)JumpCondition.WhileTriggerHeld;

        Assert.True(jump.PickAtRandom);
        Assert.True(jump.FireOnArrival);
        Assert.Equal(JumpCondition.WhileTriggerHeld, jump.Condition);
    });

    // ── patch ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task APatchCuesSnapshotAndFadeReachTheDocument() => ShellFixture.WithShell(shell =>
    {
        var patch = Select<PatchCueNode>(shell);

        shell.Cues.Inspector.PatchFadeValue = "2.5";
        Assert.Equal(2_500, patch.FadeMs);

        shell.Cues.Inspector.PatchSnapshotIndex = 0;
        Assert.Null(patch.SnapshotId);

        shell.Cues.Inspector.PatchSnapshotIndex = 1;
        Assert.Equal(shell.Project.PatchSnapshots[0].Id, patch.SnapshotId);
    });

    [Fact]
    public Task APatchCueThatRecallsNothingSaysSo() => ShellFixture.WithShell(shell =>
    {
        var patch = Select<PatchCueNode>(shell);
        patch.SnapshotId = null;
        patch.Levels.Clear();
        shell.Cues.Inspector.Reload();

        Assert.Contains("do nothing", shell.Cues.Inspector.PatchHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task PatchLevelChangesAreListedFromTheCueRatherThanAuthored() => ShellFixture.WithShell(shell =>
    {
        var patch = Select<PatchCueNode>(shell);
        Assert.False(shell.Cues.Inspector.HasPatchLevelChanges);

        var channel = shell.Project.AudioPatch.LogicalChannels[0];
        patch.Levels.Add(new PatchLevelChange { LogicalChannelId = channel.Id, GainDb = -6 });
        shell.Cues.Inspector.Reload();

        // The pane showed two fixed rows whatever the cue carried; now it shows what is there.
        var row = Assert.Single(shell.Cues.Inspector.PatchLevelChanges);
        Assert.Contains(channel.Name, row, StringComparison.Ordinal);
    });

    // ── action ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AnActionsAddressAndArgumentsReachTheDocument() => ShellFixture.WithShell(shell =>
    {
        var action = Add(shell, new ActionCueNode { Number = new CueNumber("99"), Label = "Cue lights" });

        shell.Cues.Inspector.ActionAddressValue = "/eos/cue/7.2/fire";
        shell.Cues.Inspector.ActionArgumentsValue = "1 2.5 go";

        Assert.Equal("/eos/cue/7.2/fire", action.Address);
        Assert.Equal("1 2.5 go", action.Arguments);
    });

    [Fact]
    public Task AnActionWithNoEndpointSaysSo() => ShellFixture.WithShell(shell =>
    {
        Add(shell, new ActionCueNode { Number = new CueNumber("99"), Label = "Cue lights" });

        Assert.Contains("no endpoint", shell.Cues.Inspector.ActionHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task AMidiEndpointIsCalledOutInTheEditor() => ShellFixture.WithShell(shell =>
    {
        var endpoint = new ActionEndpoint { Name = "Hog wing", Kind = EndpointKind.MidiOut };
        shell.Project.ActionEndpoints.Add(endpoint);

        Add(shell, new ActionCueNode
        {
            Number = new CueNumber("99"),
            Label = "Cue lights",
            EndpointId = endpoint.Id,
            Address = "/note",
        });

        // MIDI output exists now, so the hint is the PARSER's verdict rather than a refusal of the
        // whole protocol: "/note" is an OSC address and not a MIDI message, and saying which is what
        // lets the operator fix it while authoring instead of when the desk fails to respond.
        Assert.Contains("“/note” is not a MIDI message", shell.Cues.Inspector.ActionHint, StringComparison.Ordinal);
    });

    [Fact]
    public Task AValidMidiMessageIsDescribedBackInTheEditor() => ShellFixture.WithShell(shell =>
    {
        var endpoint = new ActionEndpoint { Name = "Hog wing", Kind = EndpointKind.MidiOut };
        shell.Project.ActionEndpoints.Add(endpoint);

        Add(shell, new ActionCueNode
        {
            Number = new CueNumber("99"),
            Label = "Cue lights",
            EndpointId = endpoint.Id,
            Address = "cc 1 7",
            Arguments = "100",
        });

        // Read back in the words a desk's manual uses, so the operator can check it against the desk
        // rather than against the syntax they just typed.
        Assert.Contains("CC 7 = 100 on ch 1", shell.Cues.Inspector.ActionHint, StringComparison.Ordinal);
    });

    // ── visualizer ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task VisualizerSettingsReachTheDocument() => ShellFixture.WithShell(shell =>
    {
        var visualizer = Add(shell, new VisualizerCueNode { Number = new CueNumber("98"), Label = "Viz" });

        shell.Cues.Inspector.VisualizerPresetPackValue = "packs/slow";
        shell.Cues.Inspector.VisualizerHoldValue = "30";
        shell.Cues.Inspector.VisualizerBlendValue = "4";
        shell.Cues.Inspector.VisualizerLocksPresetValue = true;

        Assert.Equal("packs/slow", visualizer.PresetPack);
        Assert.Equal(30_000, visualizer.HoldMs);
        Assert.Equal(4_000, visualizer.BlendMs);
        Assert.True(visualizer.LockPreset);
    });

    [Fact]
    public Task EveryControlFlowFieldIsUndoable() => ShellFixture.WithShell(shell =>
    {
        var fade = Select<FadeCueNode>(shell);
        var before = fade.DurationMs;

        shell.Cues.Inspector.FadeDurationValue = "9";
        Assert.Equal(9_000, fade.DurationMs);

        shell.Undo();

        // The property this whole panel is built on: no path to the document that undo cannot reverse.
        Assert.Equal(before, ((FadeCueNode)shell.Project.FindCue(fade.Id)!).DurationMs);
    });
}
