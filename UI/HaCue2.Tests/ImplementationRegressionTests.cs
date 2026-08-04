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

        video.MeshEnabled = true;
        video.SelectedWarpPoint = 4;
        video.NudgeWarp(0.005, -0.005);

        Assert.Equal(3, output.Mapping[0].MeshColumns);
        Assert.Equal(3, output.Mapping[0].MeshRows);
        Assert.Equal(18, output.Mapping[0].WarpOffsets.Count);
        Assert.Equal(0.005, output.Mapping[0].WarpOffsets[8], 6);
        Assert.Equal(-0.005, output.Mapping[0].WarpOffsets[9], 6);
        var section = Assert.Single(OutputMapping.Spec(output, 1920, 1080)!.Sections);
        Assert.Equal(9, section.MeshPoints!.Count);
    }

    /// <summary>
    /// Growing a mesh keeps the handles the operator has already placed.
    /// </summary>
    /// <remarks>
    /// Wanting one more column is an ordinary adjustment, and re-throwing the whole mesh for it means
    /// re-aligning every point that was already right — which on a warped surface is the slow part.
    /// </remarks>
    [Fact]
    public void ResizingAMeshCarriesTheOffsetsThatStillHaveAHome()
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

        video.MeshEnabled = true;
        video.SelectedWarpPoint = 0;
        video.NudgeWarp(0.02, 0.03);

        video.MeshColumns = 5;

        Assert.Equal(5, output.Mapping[0].MeshColumns);
        Assert.Equal(3, output.Mapping[0].MeshRows);
        Assert.Equal(30, output.Mapping[0].WarpOffsets.Count);
        // Row 0, column 0 exists in both grids, so its offset survives at the same place.
        Assert.Equal(0.02, output.Mapping[0].WarpOffsets[0], 6);
        Assert.Equal(0.03, output.Mapping[0].WarpOffsets[1], 6);
    }

    /// <summary>
    /// The splitter divides the SOURCE, not the destination.
    /// </summary>
    /// <remarks>
    /// A wall is N outputs each showing its own slice whole. Dividing both ends would put a
    /// quarter-size picture in the corner of every screen, which is the shape of mistake that only
    /// shows up once the projectors are hung.
    /// </remarks>
    [Fact]
    public void SplittingDividesTheCanvasAndFillsEachOutput()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition { Name = "Wall", CompositionId = composition.Id };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        video.SplitColumns = 3;
        video.SplitRows = 1;
        video.SplitIntoGrid();

        Assert.Equal(3, output.Mapping.Count);
        Assert.Equal(1d / 3, output.Mapping[0].SourceWidth, 6);
        Assert.Equal(1d / 3, output.Mapping[1].SourceX, 6);
        Assert.Equal(2d / 3, output.Mapping[2].SourceX, 6);

        foreach (var section in output.Mapping)
        {
            Assert.Equal(0, section.TargetX);
            Assert.Equal(1, section.TargetWidth);
            Assert.Equal(1, section.TargetHeight);
        }
    }

    /// <summary>
    /// Renaming a section keeps the row that is being typed into.
    /// </summary>
    /// <remarks>
    /// The name box lives inside the list, and a rename goes through the journal — which refreshes the
    /// whole view. Rebuilding the rows on that refresh would replace the text box after every
    /// keystroke, taking the caret with it, so a section could only be renamed one letter at a time.
    /// </remarks>
    [Fact]
    public void RenamingASectionDoesNotReplaceItsRow()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition
        {
            Name = "Projector",
            CompositionId = composition.Id,
            Mapping = [new MappingSection { Name = "Left" }, new MappingSection { Name = "Right" }],
        };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        var row = video.Sections[0];
        row.Name = "Left wall";

        Assert.Equal("Left wall", output.Mapping[0].Name);
        Assert.Same(row, video.Sections[0]);
        Assert.Equal("Left wall", video.Sections[0].Name);

        // Toggling is the same story — and it must not journal a rename for the row beside it.
        video.Sections[1].Enabled = false;
        Assert.False(output.Mapping[1].Enabled);
        Assert.Same(row, video.Sections[0]);
        Assert.Equal("Left wall", output.Mapping[0].Name);
    }

    /// <summary>Reordering DOES replace the rows, because a row's place in the list is its draw order.</summary>
    [Fact]
    public void ReorderingASectionRenumbersTheList()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition
        {
            Name = "Projector",
            CompositionId = composition.Id,
            Mapping = [new MappingSection { Name = "Left" }, new MappingSection { Name = "Right" }],
        };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        video.SelectedSection = 1;
        video.MoveSection(-1);

        Assert.Equal("Right", output.Mapping[0].Name);
        Assert.Equal("Right", video.Sections[0].Name);
        Assert.Equal("1", video.Sections[0].Position);
        Assert.Equal(0, video.SelectedSection);
    }

    /// <summary>Splitting REPLACES, so pressing it twice does not double the panels.</summary>
    [Fact]
    public void SplittingTwiceIsOneSplit()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition { Name = "Wall", CompositionId = composition.Id };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        video.SplitColumns = 2;
        video.SplitIntoGrid();
        video.SplitIntoGrid();

        Assert.Equal(2, output.Mapping.Count);
    }

    /// <summary>
    /// A whole split is ONE undo step.
    /// </summary>
    /// <remarks>
    /// Undoing a 3×1 split one section at a time would leave the output showing a mapping nobody
    /// authored — a state that is worse than either the before or the after.
    /// </remarks>
    [Fact]
    public void SplittingIsOneUndoStep()
    {
        var composition = new CompositionDefinition { Name = "Canvas" };
        var output = new VideoOutputDefinition
        {
            Name = "Wall",
            CompositionId = composition.Id,
            Mapping = [new MappingSection { Name = "Whole frame" }],
        };
        var project = new HaCueProject { Compositions = [composition], VideoOutputs = [output] };
        var journal = new ProjectJournal(project);
        var video = new VideoViewModel(project, new ShowRuntime(), journal);

        video.SplitColumns = 3;
        video.SplitIntoGrid();
        Assert.Equal(3, output.Mapping.Count);

        Assert.True(journal.Undo());

        var restored = Assert.Single(output.Mapping);
        Assert.Equal("Whole frame", restored.Name);
    }

    /// <summary>
    /// The tab labels carry the document's live counts.
    /// </summary>
    /// <remarks>
    /// They were frozen at construction because the strings doubled as the tabs' identity, so a project
    /// that gained five devices spent the evening insisting it had none.
    /// </remarks>
    [Fact]
    public void SectionTabCountsFollowTheDocument()
    {
        var project = new HaCueProject();
        var journal = new ProjectJournal(project);
        var video = new VideoViewModel(project, new ShowRuntime(), journal);

        Assert.Equal("OUTPUTS · 0", video.OutputsTab.Label);

        project.VideoOutputs.Add(new VideoOutputDefinition { Name = "Projector" });
        project.Compositions.Add(new CompositionDefinition { Name = "Cyc" });
        video.Refresh();

        Assert.Equal("OUTPUTS · 1", video.OutputsTab.Label);
        Assert.Equal("COMPOSITIONS · 1", video.CompositionsTab.Label);

        // The selection has to survive its own tab being relabelled — the whole reason the counts were
        // frozen in the first place.
        Assert.True(video.IsOutputsPane);
    }

    /// <summary>
    /// Assignment is authored on the composition and written onto the output.
    /// </summary>
    /// <remarks>
    /// One output shows one canvas, so assigning a taken output MOVES it rather than adding a second
    /// binding — and the picker says so before it is pressed.
    /// </remarks>
    [Fact]
    public void AssigningAnOutputMovesItToTheSelectedComposition()
    {
        var cyc = new CompositionDefinition { Name = "Cyc" };
        var portal = new CompositionDefinition { Name = "Portal" };
        var output = new VideoOutputDefinition { Name = "Projector", CompositionId = cyc.Id };
        var project = new HaCueProject
        {
            Compositions = [cyc, portal],
            VideoOutputs = [output],
        };
        var video = new VideoViewModel(project, new ShowRuntime(), new ProjectJournal(project));

        video.SelectedCompositionId = portal.Id;
        video.Refresh();

        Assert.Equal("Projector · move from Cyc", Assert.Single(video.AssignableOutputs));

        video.AssignableIndex = 0;
        video.AssignSelectedOutput();

        Assert.Equal(portal.Id, output.CompositionId);
        Assert.Equal("Projector", Assert.Single(video.SelectedCompositionFeeds).Name);

        video.UnassignOutput(output.Id);
        Assert.Null(output.CompositionId);
    }

    /// <summary>
    /// A new output is created unbound, and its screen choice is stored as a number.
    /// </summary>
    /// <remarks>
    /// The dialog used to write the picker's whole label ("2 · 1920×1080") into the hint, which every
    /// reader of it then failed to parse — so the chosen screen was silently discarded and the window
    /// opened wherever SDL felt like.
    /// </remarks>
    [Fact]
    public void AddingALocalOutputStoresTheScreenNumberAndNoComposition()
    {
        var project = new HaCueProject
        {
            Compositions = [new CompositionDefinition { Name = "Cyc" }],
        };
        var journal = new ProjectJournal(project);

        var prompt = Dialogs.AddVideoOutput(
            journal, VideoOutputKind.LocalScreen, ["anywhere", "1 · 1920×1080", "2 · 3840×2160"]);

        prompt["Name"].Value = "Projector A";
        prompt["Target"].SelectedIndex = 2;
        prompt.Commit();

        var output = Assert.Single(project.VideoOutputs);
        Assert.Equal("2", output.TargetHint);
        Assert.Equal(2, ProjectVideoOutputs.ScreenNumber(output.TargetHint));
        Assert.Null(output.CompositionId);
    }

    /// <summary>An output authored by the old dialog still lands on the screen it names.</summary>
    [Fact]
    public void AScreenHintWrittenAsALabelStillResolves()
    {
        Assert.Equal(2, ProjectVideoOutputs.ScreenNumber("2 · 1920×1080"));
        Assert.Null(ProjectVideoOutputs.ScreenNumber("anywhere"));
        Assert.Null(ProjectVideoOutputs.ScreenNumber(""));
    }

    /// <summary>
    /// Removing a logical output cleans up after itself — the cascade the footer has always promised.
    /// </summary>
    /// <remarks>
    /// <c>ProjectEdits.DeleteLogicalChannel</c> existed with nothing in the app calling it, so an
    /// operator could add a logical output and never remove one.
    /// </remarks>
    [Fact]
    public void RemovingALogicalOutputStripsTheCuesThatFedIt()
    {
        var channel = new LogicalAudioChannel { Name = "Lobby" };
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Bed",
            Sends = [new CueAudioSend { LogicalChannelId = channel.Id }],
        };
        var project = new HaCueProject
        {
            AudioPatch = new ProjectAudioPatch { LogicalChannels = [channel] },
            CueLists = [new CueList { Name = "Main", Cues = [cue] }],
        };
        var journal = new ProjectJournal(project);

        var prompt = Dialogs.RemoveLogicalOutput(journal, channel.Id);
        Assert.NotNull(prompt);
        prompt.Commit();

        Assert.Empty(project.AudioPatch.LogicalChannels);
        Assert.Empty(cue.Sends);

        // ONE undo step for the whole cascade: half a removal is a state nobody authored.
        Assert.True(journal.Undo());
        Assert.Single(project.AudioPatch.LogicalChannels);
        Assert.Single(cue.Sends);
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

