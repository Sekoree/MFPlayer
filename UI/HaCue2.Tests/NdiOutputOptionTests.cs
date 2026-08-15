using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The NDI output's own format options and its linked audio half (2026-08-14): one sender on the
/// network, one row in each tab, one undo step either way.
/// </summary>
public class NdiOutputOptionTests
{
    private static PromptViewModel Ndi(ProjectJournal journal) =>
        Dialogs.AddVideoOutput(journal, VideoOutputKind.Ndi, ["1 · 1920×1080"]);

    [Fact]
    public void AVideoOnlyNdiOutputCarriesItsFormatOptions()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);

        prompt["Name"].Value = "Program";
        prompt["Feed size"].Value = "1280x720";
        prompt["Feed rate"].Value = "30";
        prompt["Pixel format"].SelectedIndex = 2; // UYVY
        prompt.Commit();

        var output = Assert.Single(journal.Project.VideoOutputs, item => item.Name == "Program");
        Assert.Equal((1280, 720), (output.NdiWidth, output.NdiHeight));
        Assert.Equal(30, output.NdiFrameRate);
        Assert.Equal(NdiWireFormat.Uyvy, output.NdiPixelFormat);
        Assert.False(output.NdiCarriesAudio);
        Assert.Null(output.LinkedAudioLineId);
        Assert.DoesNotContain(journal.Project.AudioLines, line => line.Name == "Program");
    }

    [Fact]
    public void VideoPlusAudioAddsTheLinkedLineInOneUndoStep()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var linesBefore = journal.Project.AudioLines.Count;
        var prompt = Ndi(journal);

        prompt["Name"].Value = "Stage feed";
        prompt["Carries"].SelectedIndex = 1;
        prompt["Audio channels"].Value = "8";
        prompt.Commit();

        var output = Assert.Single(journal.Project.VideoOutputs, item => item.Name == "Stage feed");
        var line = Assert.Single(journal.Project.AudioLines, item => item.Name == "Stage feed");

        Assert.True(output.NdiCarriesAudio);
        Assert.Equal(AudioLineKind.Ndi, line.Kind);
        Assert.Equal(8, line.Channels);
        // Linked BOTH ways, so either tab can name (and remove) its twin.
        Assert.Equal(line.Id, output.LinkedAudioLineId);
        Assert.Equal(output.Id, line.LinkedVideoOutputId);

        // ONE operator action - one undo step removes the pair.
        journal.Undo();
        Assert.DoesNotContain(journal.Project.VideoOutputs, item => item.Name == "Stage feed");
        Assert.Equal(linesBefore, journal.Project.AudioLines.Count);
    }

    [Fact]
    public void RemovingTheVideoHalfTakesTheAudioLineWithIt()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Linked";
        prompt["Carries"].SelectedIndex = 1;
        prompt.Commit();

        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Linked");
        var remove = Dialogs.RemoveVideoOutput(journal, output.Id);
        Assert.NotNull(remove);
        Assert.Contains("audio half", remove.Hint);
        remove.Commit();

        Assert.DoesNotContain(journal.Project.VideoOutputs, item => item.Name == "Linked");
        Assert.DoesNotContain(journal.Project.AudioLines, line => line.Name == "Linked");
    }

    [Fact]
    public void TheEditDialogCanGrowAndShedTheAudioHalf()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Grow";
        prompt.Commit();
        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Grow");

        var edit = Dialogs.EditVideoOutput(journal, output.Id, ["1 · 1920×1080"]);
        Assert.NotNull(edit);
        edit["Carries"].SelectedIndex = 1;
        edit["Audio channels"].Value = "4";
        edit.Commit();

        var line = Assert.Single(journal.Project.AudioLines, item => item.Name == "Grow");
        Assert.Equal(4, line.Channels);
        Assert.Equal(line.Id, output.LinkedAudioLineId);

        var shed = Dialogs.EditVideoOutput(journal, output.Id, ["1 · 1920×1080"]);
        Assert.NotNull(shed);
        shed["Carries"].SelectedIndex = 0;
        shed.Commit();

        Assert.Null(output.LinkedAudioLineId);
        Assert.DoesNotContain(journal.Project.AudioLines, item => item.Name == "Grow");
    }

    [Fact]
    public void TheLayoutEditorNowListsNdiFeedsBesideScreens()
    {
        var project = ShellFixture.Project();
        var composition = project.Compositions[0];
        project.VideoOutputs.Add(new VideoOutputDefinition
        {
            Name = "NDI slice",
            Kind = VideoOutputKind.Ndi,
            CompositionId = composition.Id,
            NdiWidth = 960,
            NdiHeight = 540,
        });

        var screens = VideoPresentation.Screens(project, composition.Id);

        Assert.Contains(screens, output => output.Name == "NDI slice");
    }
}
