using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// F-11: the per-pane editor boundary, stated as a rule (the pattern of the framework's
/// architecture source-text guards). The Audio pane's send feature lives in
/// <c>AudioPaneEditor</c> - if <c>InspectorViewModel</c> can reach the send write path directly,
/// the seam stops being a boundary and becomes a suggestion.
/// </summary>
public sealed class InspectorEditorBoundaryTests
{
    [Fact]
    public void TheInspectorDoesNotReachTheSendWritePathDirectly()
    {
        var path = Path.Combine(RepoRoot(), "UI", "HaCue2", "ViewModels", "InspectorViewModel.cs");
        Assert.True(File.Exists(path), $"expected the inspector at {path}");

        // Comments may legitimately NAME the types when explaining the decoupling; code may not.
        var code = StripComments(File.ReadAllText(path));

        Assert.False(
            code.Contains("SetCueSendCommand", StringComparison.Ordinal),
            "InspectorViewModel must route send edits through AudioPaneEditor, never build "
            + "SetCueSendCommand itself.");
        Assert.False(
            code.Contains("ApplyActiveSendsAsync", StringComparison.Ordinal),
            "Live send pushes belong to AudioPaneEditor (PushLiveSends) - the inspector calls "
            + "Audio.PushLiveSends(), never the host directly.");
    }

    private static string StripComments(string source) =>
        string.Join("\n", source.Split('\n').Select(line =>
        {
            var slashes = line.IndexOf("//", StringComparison.Ordinal);
            return slashes >= 0 ? line[..slashes] : line;
        }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }
}
