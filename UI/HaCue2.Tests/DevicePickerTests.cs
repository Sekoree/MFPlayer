using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using HaCue2.Views;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The local-audio device picker, as the operator actually meets it.
/// </summary>
/// <remarks>
/// Typing a device name was the problem this replaced: this box enumerates fourteen outputs across two
/// driver families with names like <c>HD-Audio Generic: HDMI 0 (hw:0,3)</c>. The view-model tests prove
/// the lists are BUILT; these prove they reach the control, which is a different failure and the one
/// that looks like a broken app.
/// </remarks>
public class DevicePickerTests
{
    private sealed class B : S.Media.Core.Audio.IAudioBackend
    {
        public string Name => "fake";
        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("0", "HDMI 0 (hw:0,3)", 8, 48_000, false, "ALSA"),
            new("1", "default", 128, 48_000, true, "ALSA"),
            new("2", "Scarlett 2i2 3rd Gen Pro", 2, 48_000, false, "JACK"),
        ];
        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateInputDevices() => [];
        public S.Media.Core.Audio.IAudioOutput CreateOutput(string? i, S.Media.Core.Audio.AudioFormat f, S.Media.Core.Audio.AudioBackendOptions? o = null) => throw new NotSupportedException();
        public S.Media.Core.Audio.IAudioSource CreateInput(string? i, S.Media.Core.Audio.AudioFormat f, S.Media.Core.Audio.AudioBackendOptions? o = null) => throw new NotSupportedException();
    }

    [Fact]
    public Task TheDeviceListReachesTheControlAndNotJustTheViewModel() => ShellFixture.WithShell(_ =>
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddAudioLine(journal, AudioLineKind.LocalAudio, new AudioDevices(new B()));

        var window = new PromptWindow(prompt);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();

        // The template builds every control kind and hides the unused ones, so there is a ComboBox per
        // FIELD, each bound to that field's option list - empty for a plain text field, and populated
        // for the driver and device pickers AND for the sample-rate suggestions, whose own control is
        // a flyout rather than this combo. Assert on the two that the picker fills rather than on how
        // many fields happen to carry options, which is a fact about the dialog's shape and not about
        // whether the device list arrived.
        var filled = combos
            .Where(box => box.ItemsSource?.Cast<object>().Any() == true)
            .ToList();

        Assert.Equal(["any", "ALSA", "JACK"], filled[0].ItemsSource!.Cast<string>());
        Assert.Equal(3, filled[1].ItemsSource!.Cast<object>().Count());

        // Opened on the machine's DEFAULT device, which is the row an operator most often wants.
        Assert.Equal(1, filled[1].SelectedIndex);

        window.Close();
    });

    [Fact]
    public Task AddingALineMakesItAppearInThePanesList() => ShellFixture.WithShell(shell =>
    {
        var audio = shell.Audio;
        var before = audio.Lines.Count;

        var prompt = Dialogs.AddAudioLine(
            audio.Journal, AudioLineKind.LocalAudio, new AudioDevices(new B()));
        prompt.Fields.Single(f => f.Label == "Name").Value = "Main output";
        prompt.Commit();
        audio.Refresh();

        Assert.Equal(before + 1, audio.Lines.Count);
        Assert.Contains(audio.Lines, row => row.Name == "Main output");
    });

    /// <summary>A 44.1 kHz-only interface, on a show that mixes at 48.</summary>
    private sealed class FortyFourOne : S.Media.Core.Audio.IAudioBackend
    {
        public string Name => "fake";
        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("0", "Cheap USB codec", 2, 44_100, true, "ALSA"),
            new("1", "Scarlett 2i2 3rd Gen Pro", 2, 48_000, false, "ALSA"),
        ];
        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateInputDevices() => [];
        public S.Media.Core.Audio.IAudioOutput CreateOutput(string? i, S.Media.Core.Audio.AudioFormat f, S.Media.Core.Audio.AudioBackendOptions? o = null) => throw new NotSupportedException();
        public S.Media.Core.Audio.IAudioSource CreateInput(string? i, S.Media.Core.Audio.AudioFormat f, S.Media.Core.Audio.AudioBackendOptions? o = null) => throw new NotSupportedException();
    }

    [Fact]
    public void ADeviceThatDoesNotRunAtTheMixRateFillsInItsOwn()
    {
        var journal = new ProjectJournal(ProjectFiles.Create("Show"));
        var prompt = Dialogs.AddAudioLine(
            journal, AudioLineKind.LocalAudio, new AudioDevices(new FortyFourOne()));

        var rate = prompt.Fields.Single(field => field.Label == "Sample rate");
        var device = prompt.Fields.Single(field => field.Label == "Device");

        // Opens already pointing at the default device, which here is the 44.1 one - no click needed.
        Assert.Equal(0, device.SelectedIndex);

        // Forcing a 44.1 interface to open at the show's 48 is exactly what this field exists to
        // prevent, so the dialog fills it in rather than leaving the operator to notice.
        Assert.Equal("44100", rate.Value);
        Assert.Contains("44,100", rate.Hint, StringComparison.Ordinal);

        prompt.Fields.Single(field => field.Label == "Name").Value = "Foldback";
        prompt.Commit();

        Assert.Equal(44_100, journal.Project.AudioLines.Single().SampleRate);
    }

    [Fact]
    public void ADeviceThatAgreesWithTheShowStoresNoRateAtAll()
    {
        var journal = new ProjectJournal(ProjectFiles.Create("Show"));
        var prompt = Dialogs.AddAudioLine(
            journal, AudioLineKind.LocalAudio, new AudioDevices(new FortyFourOne()));

        var rate = prompt.Fields.Single(field => field.Label == "Sample rate");
        var device = prompt.Fields.Single(field => field.Label == "Device");

        device.SelectedIndex = 1;

        Assert.Equal("", rate.Value);

        prompt.Fields.Single(field => field.Label == "Name").Value = "Main";
        prompt.Commit();

        // Null, not 48000: the two behave identically today, and null is the one that keeps following
        // the mix rate if the show is ever re-clocked.
        Assert.Null(journal.Project.AudioLines.Single().SampleRate);
    }

    [Fact]
    public void ANewProjectCannotPatchYetAndSaysSoRatherThanDoingNothing()
    {
        var project = ProjectFiles.Create("Show");
        var audio = new AudioViewModel(new ProjectJournal(project), new ShowRuntime());

        // A new project has Main L/R and NO lines on purpose. The patch button used to open nothing
        // and say nothing, which reads exactly like a broken app.
        Assert.True(audio.HasNoLines);
        Assert.False(audio.CanPatchToDevice);
        Assert.Contains("add one under DEVICES", audio.PatchHint, StringComparison.Ordinal);
        Assert.Null(audio.PatchSelectedToDevice());
    }

    [Fact]
    public void EveryPathPromptOffersABrowseButton()
    {
        // Typing a path is how a media root ends up one directory off and nothing resolves at the
        // venue. The view draws BROWSE… for exactly these two kinds.
        Assert.True(new PromptField { Label = "Media root", Kind = PromptFieldKind.Folder }.IsPath);
        Assert.True(new PromptField { Label = "Sidecar", Kind = PromptFieldKind.File }.IsPath);
        Assert.False(new PromptField { Label = "Name" }.IsPath);
    }

    [Fact]
    public Task RemovingALineAsksFirstAndThenTakesItsPatchWithIt() => ShellFixture.WithShell(shell =>
    {
        var audio = shell.Audio;
        var line = shell.Project.AudioLines[0];
        audio.SelectedLine = audio.Lines.First(row => row.Id == line.Id);

        var prompt = Dialogs.RemoveAudioLine(audio.Journal, audio.SelectedLine.Id);
        Assert.NotNull(prompt);

        // A question, not a form: no fields, a REMOVE verb, and the consequences counted in the hint
        // where the operator is actually looking.
        Assert.Empty(prompt!.Fields);
        Assert.Equal("REMOVE", prompt.Confirm);
        Assert.Contains(line.Name, prompt.Title, StringComparison.Ordinal);

        // Nothing has happened yet - opening the dialog must not be the edit.
        Assert.Contains(shell.Project.AudioLines, item => item.Id == line.Id);

        prompt.Commit();
        audio.Refresh();

        Assert.DoesNotContain(shell.Project.AudioLines, item => item.Id == line.Id);
        Assert.DoesNotContain(shell.Project.AudioPatch.Cells, cell => cell.LineId == line.Id);
        Assert.DoesNotContain(audio.Lines, row => row.Id == line.Id);
    });

    [Fact]
    public Task RemovingWithNothingSelectedOffersNoDialogRatherThanADeadOne() =>
        ShellFixture.WithShell(shell =>
            Assert.Null(Dialogs.RemoveAudioLine(shell.Audio.Journal, lineId: null)));

    // ── the video pane's lists ────────────────────────────────────────────────────────────────

    [Fact]
    public Task AddingACompositionMakesItAppear() => ShellFixture.WithShell(shell =>
    {
        var video = shell.Video;
        var before = video.Compositions.Count;

        var prompt = Dialogs.AddComposition(video.Journal);
        prompt.Fields.Single(f => f.Label == "Name").Value = "Cyc";
        prompt.Commit();
        video.Refresh();

        // The panes were built once in the constructor and never rebuilt, so an added composition
        // simply never appeared - the pane looked broken rather than empty.
        Assert.Equal(before + 1, video.Compositions.Count);
        Assert.Contains(video.Compositions, pane => pane.Name == "Cyc");
    });

    [Fact]
    public Task AddingAnOutputMakesItAppear() => ShellFixture.WithShell(shell =>
    {
        var video = shell.Video;
        var before = video.Outputs.Count;

        var prompt = Dialogs.AddVideoOutput(video.Journal, VideoOutputKind.Ndi, []);
        prompt.Fields.Single(f => f.Label == "Name").Value = "Stream feed";
        prompt.Commit();
        video.Refresh();

        Assert.Equal(before + 1, video.Outputs.Count);
        Assert.Contains(video.Outputs, row => row.Name == "Stream feed");
    });

    [Fact]
    public Task RenamingACompositionReachesItsPane() => ShellFixture.WithShell(shell =>
    {
        var video = shell.Video;
        var composition = shell.Project.Compositions[0];

        composition.Name = "Renamed";
        video.Refresh();

        // The pane header is a snapshot of the composition, so an edit that changes it has to rebuild
        // the pane - otherwise the canvas is labelled with a name the show no longer uses.
        Assert.Contains(video.Compositions, pane => pane.Name == "Renamed");
    });

    [Theory]
    [InlineData("23.976", 23.976)]
    [InlineData("47,95", 47.95)]
    [InlineData("30", 30)]
    public void ACompositionTakesAnyFrameRateNotJustThePresets(string typed, double expected)
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddComposition(journal);

        prompt.Fields.Single(f => f.Label == "Name").Value = "Wall";
        prompt.Fields.Single(f => f.Label == "Rate").Value = typed;
        prompt.Commit();

        // A dropdown taught the operator that the common rates were the only ones. A projector at
        // 23.976 or a wall at 47.95 is an ordinary thing to have to match - and a comma is what a
        // German keyboard types.
        Assert.Equal(expected, Assert.Single(journal.Project.Compositions).FramesPerSecond, 3);
    }

    [Fact]
    public void AScreenPresetPrefillsSizeAndExactRateButStaysTyped()
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddComposition(journal, screens:
        [
            new S.Media.Present.SDL3.SDL3DisplayInfo(0, "Projector", 1920, 1080, 59.94, 60000, 1001),
            new S.Media.Present.SDL3.SDL3DisplayInfo(1, "Wall", 2560, 1600, 165, 165000, 1000),
        ]);

        // Picking a screen fills size and rate from its ACTUAL desktop mode - the exact rational,
        // because a canvas at 60.000 against a 59.94 panel beats once every ~17 s and drops a frame
        // at each crossing, on a schedule nobody can attribute.
        var preset = prompt.Fields.Single(f => f.Label == "Match screen");
        Assert.Equal(["custom", "Projector · 1920×1080 @ 59.94 Hz", "Wall · 2560×1600 @ 165 Hz"], preset.Options);
        preset.SelectedIndex = 1;

        Assert.Equal("1920", prompt.Fields.Single(f => f.Label == "Width").Value);
        Assert.Equal("1080", prompt.Fields.Single(f => f.Label == "Height").Value);
        Assert.Equal("59.94", prompt.Fields.Single(f => f.Label == "Rate").Value);

        // Still typed underneath: the operator can overrule the prefill, exactly as before.
        prompt.Fields.Single(f => f.Label == "Rate").Value = "23.976";
        prompt.Fields.Single(f => f.Label == "Name").Value = "Cyc";
        prompt.Commit();

        var composition = Assert.Single(journal.Project.Compositions);
        Assert.Equal(1920, composition.Width);
        Assert.Equal(23.976, composition.FramesPerSecond, 3);
    }

    [Fact]
    public void AHeadlessMachineGetsNoPresetRow()
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddComposition(journal, screens: []);

        Assert.DoesNotContain(prompt.Fields, f => f.Label == "Match screen");
    }

    [Fact]
    public void ALocalOutputCanBeCreatedWindowedWithItsOwnSize()
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddVideoOutput(journal, VideoOutputKind.LocalScreen, ["Screen 1"]);

        prompt.Fields.Single(f => f.Label == "Name").Value = "Monitor";
        var presentation = prompt.Fields.Single(f => f.Label == "Presentation");
        presentation.SelectedIndex = presentation.Options.ToList().IndexOf("windowed");
        prompt.Fields.Single(f => f.Label == "Window size").Value = "960×540";
        prompt.Commit();

        // Fullscreen already existed on the model, defaulted to true, and was unreachable - so every
        // local output was created fullscreen with no windowed option at all.
        var output = Assert.Single(journal.Project.VideoOutputs);
        Assert.False(output.Fullscreen);
        Assert.Equal(960, output.WindowWidth);
        Assert.Equal(540, output.WindowHeight);
    }

    [Theory]
    [InlineData("960x540", 960, 540)]
    [InlineData("1280 720", 1280, 720)]
    [InlineData("", 0, 0)]
    [InlineData("960", 0, 0)]
    [InlineData("nonsense", 0, 0)]
    public void AWindowSizeIsReadHoweverItIsTyped(string typed, int width, int height)
    {
        // ×, x or a space, because all three are what somebody types. Anything else is zeros, which
        // means "the composition's own size" - the same as leaving it empty, so a half-typed value
        // opens at the canvas size rather than at something arbitrary.
        Assert.Equal((width, height), Dialogs.WindowSize(typed));
    }

    [Fact]
    public void AFullscreenLocalOutputIgnoresATypedWindowSize()
    {
        var journal = new ProjectJournal(new HaCueProject());
        var prompt = Dialogs.AddVideoOutput(journal, VideoOutputKind.LocalScreen, ["Screen 1"]);

        prompt.Fields.Single(f => f.Label == "Name").Value = "Projector";
        prompt.Fields.Single(f => f.Label == "Window size").Value = "960×540";
        prompt.Commit();

        // Left fullscreen: the size is stored anyway so switching to windowed later keeps it, but the
        // presentation is what decides, not the presence of a number.
        Assert.True(Assert.Single(journal.Project.VideoOutputs).Fullscreen);
    }
}

