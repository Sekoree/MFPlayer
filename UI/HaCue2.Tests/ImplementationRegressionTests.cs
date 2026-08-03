using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using S.Media.Core.Audio;
using Xunit;

namespace HaCue2.Tests;

public sealed class ImplementationRegressionTests
{
    [Fact]
    public void NewProjectsUseApplicationDefaultsAndStartWithAUsableStereoPatch()
    {
        var settings = new AppSettings
        {
            NewProjectMixRate = 96_000,
            NewProjectFadeInMs = 321,
            NewProjectFadeOutMs = 4_567,
            AutoRenumberDefault = false,
            StandbyFollowsClick = true,
        };
        var project = ProjectFiles.Create("Show", "/media", settings);

        Assert.Equal(96_000, project.AudioPatch.MixSampleRate);
        Assert.Equal(321, project.Settings.DefaultFadeInMs);
        Assert.Equal(4_567, project.Settings.DefaultFadeOutMs);
        Assert.False(project.Settings.AutoRenumberOnInsert);
        Assert.True(project.Settings.ClickMovesStandby);

        // LOGICAL outputs, and no audio line. Main L/R are what the show calls its destinations and
        // travel with it; a line is a device on ONE machine, so adopting whatever the authoring laptop
        // had would put that laptop's sound card into a document bound for a venue.
        Assert.Equal(["Main L", "Main R"], project.AudioPatch.LogicalChannels.Select(item => item.Name));
        Assert.Empty(project.AudioLines);
        Assert.Empty(project.AudioPatch.Cells);
        Assert.Null(project.AudioPatch.ClockMasterLineId);
    }

    [Fact]
    public void NewMediaCuesUseProjectTriggerAndFadeDefaults()
    {
        var project = ProjectFiles.Create("Show");
        project.Settings.NewCueTrigger = CueTrigger.Follow;
        project.Settings.DefaultFadeInMs = 250;
        project.Settings.DefaultFadeOutMs = 1_250;
        var cues = new CuesViewModel(new ProjectJournal(project), new ShowRuntime());

        var added = Assert.IsType<MediaCueNode>(cues.AddCue(CueKind.Media, "/media/sting.wav"));

        Assert.Equal(CueTrigger.Follow, added.Trigger);
        Assert.Equal(250, added.FadeInMs);
        Assert.Equal(1_250, added.FadeOutMs);
    }

    [Fact]
    public void CopyToRootImportsOutsideMediaAndStoresAPortablePath()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"hacue2-import-{Guid.NewGuid():N}");
        var root = Path.Combine(temporary, "root");
        var outside = Path.Combine(temporary, "outside.wav");
        Directory.CreateDirectory(temporary);
        File.WriteAllText(outside, "test");
        try
        {
            var project = ProjectFiles.Create("Show", root);
            project.Settings.OutsideMedia = OutsideMediaPolicy.CopyToRoot;
            var cues = new CuesViewModel(new ProjectJournal(project), new ShowRuntime());

            cues.AddMedia([outside]);

            var media = Assert.IsType<MediaCueNode>(Assert.Single(project.CueLists[0].Cues));
            Assert.Equal("outside.wav", media.MediaPath);
            Assert.True(File.Exists(outside));
            Assert.True(File.Exists(Path.Combine(root, "outside.wav")));
            Assert.Empty(cues.MediaImportProblem);
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void EnablingWarpCreatesARealEditableIdentityMesh()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition
        {
            Name = "Projector",
            CompositionId = composition.Id,
            Mapping = [new MappingSection { Name = "Full" }],
        };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        video.WarpIndex = 1;
        video.SelectedWarpPoint = 4;
        video.NudgeWarp(0.005, -0.005);

        Assert.Equal(3, output.Mapping[0].WarpGrid);
        Assert.Equal(18, output.Mapping[0].WarpOffsets.Count);
        Assert.Equal(0.005, output.Mapping[0].WarpOffsets[8], 6);
        Assert.Equal(-0.005, output.Mapping[0].WarpOffsets[9], 6);
        var section = Assert.Single(OutputMapping.Spec(output, 1920, 1080)!.Sections);
        Assert.Equal(9, section.MeshPoints!.Count);
    }

    [Fact]
    public void LauncherSettingsExposeTheSharedApplicationObject()
    {
        var settings = new AppSettings();
        var launcher = new LauncherViewModel(settings, MachineFacts.Nothing);

        Assert.Same(settings, launcher.Settings);
    }

    [Fact]
    public void ClickToStandbyDoesNotRecursivelyJournalSelectionRestores()
    {
        var first = new MediaCueNode { Number = "1", Label = "First", MediaPath = "first.wav" };
        var second = new MediaCueNode { Number = "2", Label = "Second", MediaPath = "second.wav" };
        var list = new CueList { Name = "Main", Cues = [first, second] };
        var project = new HaCueProject
        {
            Settings = new ProjectSettings { ClickMovesStandby = true },
            CueLists = [list],
        };
        var journal = new ProjectJournal(project);
        var cues = new CuesViewModel(journal, new ShowRuntime());

        cues.SelectedCue = cues.AllRows.Single(row => row.Id == second.Id);

        Assert.Equal(second.Id, list.StandbyCueId);
        Assert.True(journal.IsDirty);
    }

    private sealed class FakeBackend : IAudioBackend
    {
        public string Name => "fake";
        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
            [new("default", "Default Device", 8, 48_000, true)];
        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];
        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    // ── the local-audio device picker ─────────────────────────────────────────────────────────

    private sealed class PickerBackend : S.Media.Core.Audio.IAudioBackend
    {
        public string Name => "fake";

        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("0", "HD-Audio Generic: HDMI 0 (hw:0,3)", 8, 48_000, false, "ALSA"),
            new("1", "default", 128, 48_000, true, "ALSA"),
            new("2", "Scarlett 2i2 3rd Gen Pro", 2, 48_000, false, "JACK"),
        ];

        public IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> EnumerateInputDevices() => [];

        public S.Media.Core.Audio.IAudioOutput CreateOutput(
            string? id, S.Media.Core.Audio.AudioFormat format,
            S.Media.Core.Audio.AudioBackendOptions? options = null) => throw new NotSupportedException();

        public S.Media.Core.Audio.IAudioSource CreateInput(
            string? id, S.Media.Core.Audio.AudioFormat format,
            S.Media.Core.Audio.AudioBackendOptions? options = null) => throw new NotSupportedException();
    }

    [Fact]
    public void TheLocalAudioDialogPicksADeviceRatherThanAskingForItsName()
    {
        var journal = new ProjectJournal(new HaCueProject());
        var devices = new AudioDevices(new PickerBackend());

        var prompt = Dialogs.AddAudioLine(journal, AudioLineKind.LocalAudio, devices);
        var driver = prompt.Fields.Single(field => field.Label == "Driver");
        var device = prompt.Fields.Single(field => field.Label == "Device");
        var channels = prompt.Fields.Single(field => field.Label == "Channels");

        // "any" plus the families that were actually enumerated.
        Assert.Equal(["any", "ALSA", "JACK"], driver.Options);

        // It opens on the machine's DEFAULT device, which is the one an operator most often wants and
        // the only one they could otherwise have typed from memory.
        Assert.Equal(3, device.Options.Count);
        Assert.StartsWith("default · 128ch · default", device.Choice, StringComparison.Ordinal);

        // Picking a driver narrows the list — the difference between choosing from one device and
        // reading fifteen near-identical names to find the interface.
        driver.SelectedIndex = 2;
        Assert.Equal(["Scarlett 2i2 3rd Gen Pro · 2ch"], device.Options);

        // The channel count follows the device, because it is the number the patch is built against
        // and the one an operator would otherwise look up and get wrong.
        device.SelectedIndex = 0;
        Assert.Equal("2", channels.Value);

        prompt.Commit();

        // What travels in the document is the NAME, because that is what the hint matches at the
        // venue. The driver narrowed the list and is deliberately not stored.
        var line = Assert.Single(journal.Project.AudioLines);
        Assert.Equal("Scarlett 2i2 3rd Gen Pro", line.DeviceHint);
        Assert.Equal(AudioLineKind.LocalAudio, line.Kind);
        Assert.Equal(2, line.Channels);
    }

    [Fact]
    public void WithNoBackendToAskTheDialogFallsBackToATypedHint()
    {
        var journal = new ProjectJournal(new HaCueProject());

        // A preview, a headless capture, or a show authored on a laptop for a rig it has never seen.
        var prompt = Dialogs.AddAudioLine(journal, AudioLineKind.LocalAudio, devices: null);

        Assert.DoesNotContain(prompt.Fields, field => field.Label == "Driver");
        var device = prompt.Fields.Single(field => field.Label == "Device");
        Assert.Equal(PromptFieldKind.Text, device.Kind);
    }
}

