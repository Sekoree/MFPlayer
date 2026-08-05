using System.Diagnostics;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using Avalonia.Input;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class KeyboardControlTests
{
    [Fact]
    public void FirstEscapeStopsAndSecondEscapePanics()
    {
        var now = Stopwatch.GetTimestamp();
        var sequence = new EmergencyKeySequence(
            TimeSpan.FromMilliseconds(700),
            () => now);

        Assert.Equal(EscapeAction.Stop, sequence.Press());
        now += Stopwatch.Frequency / 2;
        Assert.Equal(EscapeAction.Panic, sequence.Press());
        now += Stopwatch.Frequency / 10;
        Assert.Equal(EscapeAction.Stop, sequence.Press());
    }

    [Fact]
    public void AnExpiredSecondEscapeStartsANewStopSequence()
    {
        var now = Stopwatch.GetTimestamp();
        var sequence = new EmergencyKeySequence(
            TimeSpan.FromMilliseconds(700),
            () => now);

        Assert.Equal(EscapeAction.Stop, sequence.Press());
        now += Stopwatch.Frequency;
        Assert.Equal(EscapeAction.Stop, sequence.Press());
    }

    [Fact]
    public void AvaloniaKeysUseTheSameCanonicalGestureTextAsBindings()
    {
        Assert.Equal("Space", KeyboardGestureText.Format(Key.Space, KeyModifiers.None));
        Assert.Equal("Ctrl+Shift+P", KeyboardGestureText.Format(
            Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("Esc", KeyboardGestureText.Format(Key.Escape, KeyModifiers.None));
    }

    [Fact]
    public async Task KeyboardBindingsFireWithoutOpeningADevice()
    {
        var cue = new CommentCueNode { Number = "1", Label = "Marker" };
        var input = new TriggerInputDefinition
        {
            Name = "Hotkeys",
            Kind = TriggerInputKind.Keyboard,
            Bindings = [new TriggerBinding { Input = "Ctrl+K", TargetCueId = cue.Id }],
        };
        var project = new HaCueProject
        {
            TriggerInputs = [input],
            CueLists = [new CueList { Name = "Act", Cues = [cue] }],
        };
        await using var triggers = new TriggerInputs(project);
        var fired = new List<TriggerAction>();
        triggers.Triggered += fired.Add;

        Assert.True(triggers.FeedKeyboard("Control + K", isTyping: false));

        Assert.Equal(cue.Id, Assert.Single(fired).CueId);
    }

    [Fact]
    public async Task KeyboardBindingsYieldToTypingUnlessExplicitlyGlobal()
    {
        var local = new TriggerBinding
        {
            Input = "F8", Target = TriggerTarget.Transport, ParameterId = "go",
        };
        var global = new TriggerBinding
        {
            Input = "F9", Target = TriggerTarget.Transport, ParameterId = "panic", AllowWhileTyping = true,
        };
        var project = new HaCueProject
        {
            TriggerInputs =
            [
                new TriggerInputDefinition
                {
                    Name = "Hotkeys", Kind = TriggerInputKind.Keyboard, Bindings = [local, global],
                },
            ],
        };
        await using var triggers = new TriggerInputs(project);
        var fired = new List<TriggerAction>();
        triggers.Triggered += fired.Add;

        Assert.False(triggers.FeedKeyboard("F8", isTyping: true));
        Assert.True(triggers.FeedKeyboard("F9", isTyping: true));

        Assert.Equal("panic", Assert.Single(fired).ParameterId);
    }
}
