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
        var devices = new AudioDevices(new FakeBackend());

        var project = ProjectFiles.Create("Show", "/media", settings, new MachineFacts(devices));

        Assert.Equal(96_000, project.AudioPatch.MixSampleRate);
        Assert.Equal(321, project.Settings.DefaultFadeInMs);
        Assert.Equal(4_567, project.Settings.DefaultFadeOutMs);
        Assert.False(project.Settings.AutoRenumberOnInsert);
        Assert.True(project.Settings.ClickMovesStandby);
        var line = Assert.Single(project.AudioLines);
        Assert.Equal("Default Device", line.DeviceHint);
        Assert.True(line.Required);
        Assert.Equal(line.Id, project.AudioPatch.ClockMasterLineId);
        Assert.Equal(2, project.AudioPatch.Cells.Count);
        Assert.Equal([0, 1], project.AudioPatch.Cells.Select(cell => cell.LineChannel));
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
}
