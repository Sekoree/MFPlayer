using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Machine;
using HaCue2.Session;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The New-project prompt decides WHAT and WHERE in one dialog: CREATE writes the file to the
/// chosen folder immediately, so the shell never opens a second "where should this live?" picker,
/// and an empty media root honestly means "beside the project file" from the first minute.
/// </summary>
public class NewProjectPromptTests
{
    [Fact]
    public async Task Create_WritesTheFileAndAdoptsItsPath()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-newproj").FullName;
        try
        {
            var settings = new AppSettings();
            var launcher = new LauncherViewModel(settings, MachineFacts.Nothing);
            (HaCueProject Project, string Path)? adopted = null;
            launcher.ProjectOpened += (project, path) => adopted = (project, path);

            var prompt = launcher.NewProject();
            prompt["Name"].Value = "Rig check";
            prompt["Save in"].Value = directory;
            prompt.Commit();

            Assert.NotNull(adopted);
            Assert.Equal(Path.Combine(directory, "Rig check" + ProjectFiles.Extension), adopted.Value.Path);
            Assert.True(File.Exists(adopted.Value.Path), "CREATE must write the file, not promise one");
            // Media beside the project = an EMPTY stored root, which MediaPaths resolves against the
            // project's own folder once the file exists - which it now does.
            Assert.Equal("", adopted.Value.Project.Settings.MediaRoot);
            Assert.Equal("Rig check", (await HaCueProjectFile.LoadAsync(adopted.Value.Path)).Title);
            // The folder is remembered for the next prompt.
            Assert.Equal(directory, settings.NewProjectFolder);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Create_NeverClobbersAnExistingShow()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-newproj").FullName;
        try
        {
            var launcher = new LauncherViewModel(new AppSettings(), MachineFacts.Nothing);
            var paths = new List<string>();
            launcher.ProjectOpened += (_, path) => paths.Add(path);

            foreach (var _ in Enumerable.Range(0, 2))
            {
                var prompt = launcher.NewProject();
                prompt["Save in"].Value = directory;
                prompt.Commit();
            }

            Assert.Equal(2, paths.Distinct().Count());
            Assert.All(paths, path => Assert.True(File.Exists(path)));
            Assert.Contains("(2)", Path.GetFileName(paths[1]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Create_WithNoFolder_FallsBackToTheAskWhereRoute()
    {
        var launcher = new LauncherViewModel(new AppSettings(), MachineFacts.Nothing);
        string? adoptedPath = null;
        launcher.ProjectOpened += (_, path) => adoptedPath = path;

        var prompt = launcher.NewProject();
        prompt["Save in"].Value = "";
        prompt.Commit();

        Assert.Equal("", adoptedPath);
    }

    [Fact]
    public void MediaRootField_IsGreyedWhileMediaLivesBesideTheProject()
    {
        var prompt = new LauncherViewModel(new AppSettings(), MachineFacts.Nothing).NewProject();

        Assert.True(prompt["Media beside project"].IsOn, "beside the project is the default");
        Assert.False(prompt["Media root"].IsEnabled);

        prompt["Media beside project"].IsOn = false;
        Assert.True(prompt["Media root"].IsEnabled, "an explicit root only means something when beside is off");
    }
}
