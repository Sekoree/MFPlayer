using HaCue2.Machine;
using Xunit;

namespace HaCue2.Core.Tests;

public sealed class MediaCachePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hacue-cache-" + Guid.NewGuid().ToString("N"));

    public MediaCachePolicyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("2.0 GB", 2L << 30)]
    [InlineData("512 MB", 512L << 20)]
    [InlineData("1.5GiB", 1610612736L)]
    public void HumanBudgetsAreParsed(string text, long expected) =>
        Assert.Equal(expected, MediaCache.ParseBudget(text));

    [Fact]
    public void YouTubeLivesUnderTheSameConfiguredRoot()
    {
        var settings = new AppSettings { CacheRoot = _root };

        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "youtube"), MediaCache.YouTubeRootFor(settings));
    }

    [Fact]
    public void OldestWaveformsAreEvictedToTheBudget()
    {
        var folder = Path.Combine(_root, "waveforms");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.peaks");
        var keep = Path.Combine(folder, "new.peaks");
        File.WriteAllBytes(old, new byte[80]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-1));
        File.WriteAllBytes(keep, new byte[80]);

        MediaCache.EnforceBudget(_root, "waveforms", 100, keep);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void ClearingYouTubeReportsAndRemovesOnlyDerivedData()
    {
        var youtube = Path.Combine(_root, "youtube");
        Directory.CreateDirectory(youtube);
        File.WriteAllBytes(Path.Combine(youtube, "asset.mkv"), new byte[2_048]);
        var unrelated = Path.Combine(_root, "show.hacue2proj");
        File.WriteAllText(unrelated, "show");

        var result = MediaCache.ClearRoot(_root, "youtube");

        Assert.Contains("2 kB", result, StringComparison.Ordinal);
        Assert.False(Directory.Exists(youtube));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
