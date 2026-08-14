using System.Text.RegularExpressions;
using Xunit;

namespace S.Media.Arch.Tests;

/// <summary>
/// F-19 (2026-08-14 review): documentation must not contradict the native truth. The FFmpeg
/// identity lives in ONE place - <c>.github/native-manifest/ffmpeg.lock</c> - and these assertions
/// break the build when a doc, workflow or script claims a different ABI or reaches for a mutable
/// download again. The review found four documents simultaneously claiming "8.1 / avcodec-62"
/// while the binding required avcodec-63; that class of drift now fails CI instead of accumulating.
/// </summary>
public sealed class NativeTruthDocTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }

    private static string LockedAbi()
    {
        var lockFile = Path.Combine(RepoRoot(), ".github", "native-manifest", "ffmpeg.lock");
        Assert.True(File.Exists(lockFile), $"the FFmpeg native lock is missing: {lockFile}");
        var match = Regex.Match(File.ReadAllText(lockFile), "^FFMPEG_ABI_AVCODEC=\"(\\d+)\"", RegexOptions.Multiline);
        Assert.True(match.Success, "ffmpeg.lock does not declare FFMPEG_ABI_AVCODEC");
        return match.Groups[1].Value;
    }

    [Theory]
    [InlineData("Doc/Native-Dependencies.md")]
    [InlineData("Doc/Release-Tiers.md")]
    [InlineData("Doc/MediaFramework-Quickstart.md")]
    public void DocsNameTheLockedAvcodecAbiAndNoOther(string relativePath)
    {
        var abi = LockedAbi();
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.Contains($"avcodec-{abi}", text);

        // Any OTHER avcodec major in the doc is a stale claim (the drift the review caught was
        // exactly this: docs said avcodec-62 while the binding loaded avcodec-63).
        var strays = Regex.Matches(text, @"avcodec-(\d+)")
            .Select(m => m.Groups[1].Value)
            .Where(major => major != abi)
            .Distinct()
            .ToArray();
        Assert.True(strays.Length == 0,
            $"{relativePath} claims avcodec major(s) [{string.Join(", ", strays)}] but ffmpeg.lock pins {abi} - "
            + "update the doc from the lock, never the other way around.");
    }

    [Fact]
    public void TheBuildWorkflowNeverDownloadsAMutableFfmpeg()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "build.yml"));

        // The pre-F-02 shape: BtbN's rolling `latest` release for FFmpeg. Both CI stages must build
        // their URL from the lock's dated release tag instead.
        Assert.DoesNotContain("ffmpeg-master-latest", workflow);
        Assert.DoesNotContain("download/latest/ffmpeg", workflow);
        Assert.Contains("ffmpeg.lock", workflow);
    }

    [Fact]
    public void TheSbomAndVersionGateReadTheLockRatherThanHardcodingAVersion()
    {
        var root = RepoRoot();
        Assert.Contains("ffmpeg.lock",
            File.ReadAllText(Path.Combine(root, "scripts", "generate-native-sbom.sh")));
        Assert.Contains("ffmpeg.lock",
            File.ReadAllText(Path.Combine(root, "scripts", "check-native-versions.sh")));
        Assert.Contains("ffmpeg.lock",
            File.ReadAllText(Path.Combine(root, "scripts", "fetch-ffmpeg.sh")));
    }
}
