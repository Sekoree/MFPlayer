using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Authoring a trigger binding — Learn, conflicts, and removal.
/// </summary>
/// <remarks>
/// External input ran before this existed, but a <c>TriggerBinding</c> could not be constructed
/// anywhere in the app: the runtime worked and the authoring surface did not. These tests are about
/// the gesture that closes that — press LEARN, press a button, choose what it fires, BIND.
/// </remarks>
public class TriggerBindingTests
{
    private static ShellViewModel WithSource(ShellViewModel shell, out TriggerInputDefinition source)
    {
        source = new TriggerInputDefinition { Name = "APC mini", Kind = TriggerInputKind.MidiIn };
        shell.Project.TriggerInputs.Add(source);
        shell.Targets.Refresh();
        shell.Targets.SelectedSource = shell.Targets.Sources[0];
        return shell;
    }

    [Fact]
    public Task LearnCatchesTheFirstMessageAndStops() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out _);

        shell.Targets.BeginLearn();
        Assert.True(shell.Targets.IsLearning);

        shell.Targets.Observe("note 3 ch 1");

        // The FIRST message, not the last: a fader sends a stream, and a Learn that kept updating
        // would bind whatever the operator's hand did on the way back.
        Assert.False(shell.Targets.IsLearning);
        Assert.Equal("note 3 ch 1", shell.Targets.LearnCaught);

        shell.Targets.Observe("cc 7 ch 1");
        Assert.Equal("note 3 ch 1", shell.Targets.LearnCaught);
    });

    [Fact]
    public Task NothingIsCaughtWhileNotListening() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out _);

        shell.Targets.Observe("note 3 ch 1");

        // Otherwise every message that arrived would move the Learn target under the operator.
        Assert.Equal("", shell.Targets.LearnCaught);
        Assert.False(shell.Targets.CanBind);
    });

    [Fact]
    public Task BindingCreatesATriggerBindingOnTheSelectedSource() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);

        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");

        // Index 0..3 are the transport verbs; a cue starts after them.
        shell.Targets.LearnTargetIndex = 4;
        shell.Targets.Bind();

        var binding = Assert.Single(source.Bindings);
        Assert.Equal("note 3 ch 1", binding.Input);
        Assert.Equal(TriggerTarget.Cue, binding.Target);
        Assert.NotNull(binding.TargetCueId);
        Assert.NotNull(shell.Project.FindCue(binding.TargetCueId!.Value));
    });

    [Fact]
    public Task ATransportVerbCanBeBound() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);

        shell.Targets.BeginLearn();
        shell.Targets.Observe("/hacue/go");
        shell.Targets.LearnTargetIndex = 0;
        shell.Targets.Bind();

        var binding = Assert.Single(source.Bindings);
        Assert.Equal(TriggerTarget.Transport, binding.Target);
        Assert.Equal("go", binding.ParameterId);
    });

    [Fact]
    public Task ABindIsUndoable() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);

        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");
        shell.Targets.LearnTargetIndex = 4;
        shell.Targets.Bind();

        Assert.Single(source.Bindings);

        shell.Undo();
        Assert.Empty(source.Bindings);
    });

    [Fact]
    public Task AConflictIsReportedBeforeTheBind() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);
        source.Bindings.Add(new TriggerBinding
        {
            Input = "note 3 ch 1",
            Target = TriggerTarget.Transport,
            ParameterId = "go",
        });

        shell.Targets.Refresh();
        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");

        // Rebinding a note that already fires something is how a show loses a cue silently, so the
        // operator is told before they press the button rather than after.
        Assert.Contains("already fires", shell.Targets.LearnConflict, StringComparison.Ordinal);
    });

    [Fact]
    public Task AFreeInputSaysSo() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out _);

        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 9 ch 1");

        Assert.Contains("is free", shell.Targets.LearnConflict, StringComparison.Ordinal);
    });

    [Fact]
    public Task BindingOverAConflictReplacesRatherThanAdds() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);
        source.Bindings.Add(new TriggerBinding
        {
            Input = "note 3 ch 1",
            Target = TriggerTarget.Transport,
            ParameterId = "go",
        });

        shell.Targets.Refresh();
        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");
        shell.Targets.LearnTargetIndex = 4;
        shell.Targets.Bind();

        // Two bindings on one note both firing is almost never what somebody meant.
        var binding = Assert.Single(source.Bindings);
        Assert.Equal(TriggerTarget.Cue, binding.Target);
    });

    [Fact]
    public Task ReplacingAConflictIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);
        source.Bindings.Add(new TriggerBinding
        {
            Input = "note 3 ch 1",
            Target = TriggerTarget.Transport,
            ParameterId = "go",
        });

        shell.Targets.Refresh();
        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");
        shell.Targets.LearnTargetIndex = 4;
        shell.Targets.Bind();

        shell.Undo();

        // The remove and the add are one gesture, so one undo puts the original back rather than
        // leaving the source with nothing bound.
        var restored = Assert.Single(source.Bindings);
        Assert.Equal(TriggerTarget.Transport, restored.Target);
    });

    [Fact]
    public Task BindingIsRefusedWithNoSourceSelected() => ShellFixture.WithShell(shell =>
    {
        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");

        Assert.False(shell.Targets.CanBind);
    });

    [Fact]
    public Task ABindingCanBeRemoved() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);
        source.Bindings.Add(new TriggerBinding { Input = "note 3 ch 1" });
        shell.Targets.Refresh();

        shell.Targets.RemoveBinding(0);

        Assert.Empty(source.Bindings);
    });

    [Fact]
    public Task ANewBindingCarriesARepeatFilter() => ShellFixture.WithShell(shell =>
    {
        WithSource(shell, out var source);

        shell.Targets.BeginLearn();
        shell.Targets.Observe("note 3 ch 1");
        shell.Targets.LearnTargetIndex = 4;
        shell.Targets.Bind();

        // A hardware button bounces. Without a filter one press fires the cue several times, and the
        // operator would have to know to add one.
        Assert.True(source.Bindings[0].NoRepeatMs > 0);
    });
}
