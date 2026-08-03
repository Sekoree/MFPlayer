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
        // FIELD — four of them, two bound to the empty option list of a text field. The ones that
        // matter are the two the picker actually filled.
        var filled = combos
            .Where(box => box.ItemsSource?.Cast<object>().Any() == true)
            .ToList();

        Assert.Equal(2, filled.Count);
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
}
