using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The NDI output's format options and its CARRIER (2026-08-26): one sender name stored once, a row
/// in each tab referencing it, one undo step either way - and each half removable without taking
/// the other down (review B1/B4/B6).
/// </summary>
public class NdiOutputOptionTests
{
    private static PromptViewModel Ndi(ProjectJournal journal) =>
        Dialogs.AddVideoOutput(journal, VideoOutputKind.Ndi, ["1 · 1920×1080"]);

    [Fact]
    public void AVideoOnlyNdiOutputCarriesItsFormatOptionsAndOneCarrier()
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

        var carrier = Assert.Single(journal.Project.NdiCarriers, item => item.Name == "Program");
        Assert.Equal(carrier.Id, output.CarrierId);
        Assert.Null(journal.Project.AudioHalfOf(carrier.Id));
        Assert.DoesNotContain(journal.Project.AudioLines, line => line.Name == "Program");
    }

    [Fact]
    public void VideoPlusAudioAddsThePatchedLineInOneUndoStep()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var linesBefore = journal.Project.AudioLines.Count;
        var cellsBefore = journal.Project.AudioPatch.Cells.Count;
        var prompt = Ndi(journal);

        prompt["Name"].Value = "Stage feed";
        prompt["Carries"].SelectedIndex = 1;
        prompt["Audio channels"].Value = "8";
        prompt.Commit();

        var output = Assert.Single(journal.Project.VideoOutputs, item => item.Name == "Stage feed");
        var line = Assert.Single(journal.Project.AudioLines, item => item.Name == "Stage feed");

        Assert.Equal(AudioLineKind.Ndi, line.Kind);
        Assert.Equal(8, line.Channels);
        // One carrier joins the two halves - the name is stored once.
        Assert.NotNull(output.CarrierId);
        Assert.Equal(output.CarrierId, line.CarrierId);

        // The line arrives PATCHED (review B1: an unpatched line is skipped by the bay, so the old
        // flow shipped a silent feed): one cell per channel up to the bus width, unity, channel 1
        // of the line fed by the first logical output in bus order.
        var seeded = journal.Project.AudioPatch.Cells.Where(cell => cell.LineId == line.Id).ToList();
        var busWidth = journal.Project.AudioPatch.LogicalChannels.Count;
        Assert.Equal(Math.Min(busWidth, line.Channels), seeded.Count);
        Assert.All(seeded, cell => Assert.Equal(0, cell.GainDb));

        // ONE operator action - one undo step removes the pair, the carrier and the seed.
        journal.Undo();
        Assert.DoesNotContain(journal.Project.VideoOutputs, item => item.Name == "Stage feed");
        Assert.DoesNotContain(journal.Project.NdiCarriers, item => item.Name == "Stage feed");
        Assert.Equal(linesBefore, journal.Project.AudioLines.Count);
        Assert.Equal(cellsBefore, journal.Project.AudioPatch.Cells.Count);
    }

    [Fact]
    public void RemovingTheVideoHalfKeepsTheAudioHalfSending()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Linked";
        prompt["Carries"].SelectedIndex = 1;
        prompt.Commit();

        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Linked");
        var remove = Dialogs.RemoveVideoOutput(journal, output.Id);
        Assert.NotNull(remove);
        // The prompt SAYS the sender downgrades rather than cascading (review B6).
        Assert.Contains("keeps sending audio", remove.Hint);
        remove.Commit();

        Assert.DoesNotContain(journal.Project.VideoOutputs, item => item.Name == "Linked");
        // The audio half - and the carrier it references - survive: audio-only is first-class.
        var line = Assert.Single(journal.Project.AudioLines, item => item.Name == "Linked");
        Assert.NotNull(line.CarrierId);
        Assert.Single(journal.Project.NdiCarriers, item => item.Name == "Linked");
    }

    [Fact]
    public void RemovingTheLastHalfTakesTheCarrierRowWithIt()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Solo";
        prompt.Commit();

        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Solo");
        Dialogs.RemoveVideoOutput(journal, output.Id)!.Commit();

        // A video-only sender's carrier goes with its one half, or the validator would flag an
        // orphan name nothing sends under.
        Assert.DoesNotContain(journal.Project.NdiCarriers, item => item.Name == "Solo");
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
        Assert.Equal(output.CarrierId, line.CarrierId);
        Assert.Contains(journal.Project.AudioPatch.Cells, cell => cell.LineId == line.Id);

        var shed = Dialogs.EditVideoOutput(journal, output.Id, ["1 · 1920×1080"]);
        Assert.NotNull(shed);
        shed["Carries"].SelectedIndex = 0;
        shed.Commit();

        Assert.DoesNotContain(journal.Project.AudioLines, item => item.Name == "Grow");
        // The carrier stays: the sender still has its video half.
        Assert.Single(journal.Project.NdiCarriers, item => item.Name == "Grow");
    }

    [Fact]
    public void TheEditDialogCanDowngradeToAudioOnly()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Downshift";
        prompt["Carries"].SelectedIndex = 1;
        prompt.Commit();
        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Downshift");

        var edit = Dialogs.EditVideoOutput(journal, output.Id, ["1 · 1920×1080"]);
        Assert.NotNull(edit);
        edit["Carries"].SelectedIndex = 2; // audio only - this video row goes
        edit.Commit();

        // The video ROW is gone; the sender continues as an audio-only feed on the same carrier.
        Assert.DoesNotContain(journal.Project.VideoOutputs, item => item.Name == "Downshift");
        var line = Assert.Single(journal.Project.AudioLines, item => item.Name == "Downshift");
        var carrier = Assert.Single(journal.Project.NdiCarriers, item => item.Name == "Downshift");
        Assert.Equal(carrier.Id, line.CarrierId);
    }

    [Fact]
    public void RenamingTheCarrierRenamesTheSenderAndBothRows()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Ndi(journal);
        prompt["Name"].Value = "Old name";
        prompt["Carries"].SelectedIndex = 1;
        prompt.Commit();
        var output = journal.Project.VideoOutputs.Single(item => item.Name == "Old name");

        var edit = Dialogs.EditVideoOutput(journal, output.Id, ["1 · 1920×1080"]);
        Assert.NotNull(edit);
        edit["Name"].Value = "New name";
        edit.Commit();

        // One rename, three rows in step (review B5/B7: the old model renamed one row and split
        // the sender on the network).
        Assert.Single(journal.Project.NdiCarriers, item => item.Name == "New name");
        Assert.Single(journal.Project.VideoOutputs, item => item.Name == "New name");
        Assert.Single(journal.Project.AudioLines, item => item.Name == "New name");
        Assert.DoesNotContain(journal.Project.NdiCarriers, item => item.Name == "Old name");
    }

    [Fact]
    public void TheAudioLineEditorCanGrowTheVideoHalf()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var add = Dialogs.AddAudioLine(journal, AudioLineKind.Ndi);
        add["Name"].Value = "Audio first";
        add["Channels"].Value = "2";
        add.Commit();

        var added = Assert.Single(journal.Project.AudioLines, item => item.Name == "Audio first");
        // An audio-only NDI line arrives patched, for the same reason the A/V add's line does.
        Assert.Contains(journal.Project.AudioPatch.Cells, cell => cell.LineId == added.Id);

        var edit = Dialogs.EditAudioLine(journal, added.Id, devices: null);
        Assert.NotNull(edit);
        edit["Carries"].SelectedIndex = 1; // audio + video
        edit.Commit();

        // The mirror of the video tab growing an audio half (review B3/B4): the sender's video row
        // arrives on the same carrier, unassigned until a composition is chosen.
        var output = Assert.Single(journal.Project.VideoOutputs, item => item.Name == "Audio first");
        Assert.Equal(added.CarrierId, output.CarrierId);
        Assert.Null(output.CompositionId);
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
