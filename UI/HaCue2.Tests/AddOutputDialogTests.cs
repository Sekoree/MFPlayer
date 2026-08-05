using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The add-output dialog, where the two presentation modes ask for different things.
/// </summary>
/// <remarks>
/// It used to offer both at once. A fullscreen output takes the SCREEN's size, so a window size typed
/// beside it did nothing; a windowed one opens where the desktop puts it, so the screen picker did
/// nothing either. Both controls looked live and one of them was always a lie.
/// </remarks>
public class AddOutputDialogTests
{
    private static PromptViewModel LocalScreen() =>
        Dialogs.AddVideoOutput(
            new ProjectJournal(ShellFixture.Project()),
            VideoOutputKind.LocalScreen,
            ["1 · 1920×1080", "2 · 3840×2160"]);

    [Fact]
    public void FullscreenOffersTheScreenAndNotTheWindowSize()
    {
        var prompt = LocalScreen();

        // Fullscreen is the default, so this is the state the dialog opens in.
        Assert.Equal("fullscreen", prompt["Presentation"].Choice);
        Assert.True(prompt["Target"].IsEnabled, "a fullscreen output is placed by choosing its screen");
        Assert.False(prompt["Window size"].IsEnabled, "a fullscreen output takes the screen's size");
        Assert.False(prompt["Lock aspect"].IsEnabled);
        Assert.False(prompt["Lock resolution"].IsEnabled);
    }

    [Fact]
    public void WindowedOffersTheWindowSizeAndNotTheScreen()
    {
        var prompt = LocalScreen();

        prompt["Presentation"].SelectedIndex = 1;

        Assert.Equal("windowed", prompt["Presentation"].Choice);
        Assert.True(prompt["Window size"].IsEnabled);
        Assert.True(prompt["Lock aspect"].IsEnabled);
        Assert.True(prompt["Lock resolution"].IsEnabled);
        Assert.False(prompt["Target"].IsEnabled);
    }

    [Fact]
    public void ChangingBackRestoresTheOtherField()
    {
        var prompt = LocalScreen();

        prompt["Presentation"].SelectedIndex = 1;
        prompt["Presentation"].SelectedIndex = 0;

        // Greyed rather than hidden, and reversible: a change of mind must not cost the value.
        Assert.True(prompt["Target"].IsEnabled);
        Assert.False(prompt["Window size"].IsEnabled);
    }

    [Fact]
    public void TheWindowSizeOffersPresetsAndStaysTypable()
    {
        var prompt = LocalScreen();
        var size = prompt["Window size"];

        Assert.Equal(PromptFieldKind.Suggestion, size.Kind);
        Assert.NotEmpty(size.Options);

        // A preset is a shortcut, never a fence — an old projector at 1024×768 has to be sayable.
        size.Value = "1366×768";
        Assert.Equal("1366×768", size.Value);
    }

    [Fact]
    public void AWindowedOutputIsCreatedWindowedAtTheChosenSize()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Dialogs.AddVideoOutput(journal, VideoOutputKind.LocalScreen, ["1 · 1920×1080"]);
        var before = journal.Project.VideoOutputs.Count;

        prompt["Name"].Value = "Confidence";
        prompt["Presentation"].SelectedIndex = 1;
        prompt["Window size"].Value = "1280×720";
        prompt["Lock aspect"].IsOn = true;
        prompt["Lock resolution"].IsOn = true;
        prompt.Commit();

        var output = journal.Project.VideoOutputs[^1];

        Assert.Equal(before + 1, journal.Project.VideoOutputs.Count);
        Assert.False(output.Fullscreen);
        Assert.Equal(1280, output.WindowWidth);
        Assert.Equal(720, output.WindowHeight);
        Assert.Equal(1280, output.MappingWidth);
        Assert.Equal(720, output.MappingHeight);
        Assert.True(output.WindowAspectLocked);
        Assert.True(output.WindowResolutionLocked);
    }

    [Fact]
    public void AFullscreenOutputCarriesItsSelectedScreensRasterIntoTheLayout()
    {
        var journal = new ProjectJournal(ShellFixture.Project());
        var prompt = Dialogs.AddVideoOutput(
            journal, VideoOutputKind.LocalScreen,
            ["1 · 1920×1080", "2 · 3840×2160 · primary"]);

        prompt["Target"].SelectedIndex = 1;
        prompt.Commit();

        var output = journal.Project.VideoOutputs[^1];
        Assert.Equal(3840, output.MappingWidth);
        Assert.Equal(2160, output.MappingHeight);
    }

    [Fact]
    public void ANonLocalOutputHasNoPresentationChoiceAtAll()
    {
        var prompt = Dialogs.AddVideoOutput(
            new ProjectJournal(ShellFixture.Project()), VideoOutputKind.Ndi, []);

        // An NDI sender has no window and no screen; offering either would be a control that lies.
        Assert.DoesNotContain(prompt.Fields, field => field.Label == "Presentation");
        Assert.DoesNotContain(prompt.Fields, field => field.Label == "Window size");
        Assert.DoesNotContain(prompt.Fields, field => field.Label == "Lock aspect");
        Assert.DoesNotContain(prompt.Fields, field => field.Label == "Lock resolution");
    }
}
