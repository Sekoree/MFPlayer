using S.Media.Core;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// The shared media-cache root and its override.
/// </summary>
/// <remarks>
/// The override is the point: both call sites used to compute this path inline, so nothing could redirect
/// them. HaPlay's own cache redirect covered settings, recovery and scripts and silently missed these -
/// which meant a test that built the YouTube module (its default constructor takes no cache root) prepared
/// assets into the developer's real cache directory.
/// </remarks>
public sealed class MediaCachePathsTests
{
    /// <summary>Runs <paramref name="body"/> with the override set, restoring it afterwards.</summary>
    private static void WithRoot(string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(MediaCachePaths.RootOverrideVariable);
        Environment.SetEnvironmentVariable(MediaCachePaths.RootOverrideVariable, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MediaCachePaths.RootOverrideVariable, previous);
        }
    }

    [Fact]
    public void TheOverrideRedirectsTheRoot()
    {
        WithRoot("/tmp/some-cache", static () => Assert.Equal("/tmp/some-cache", MediaCachePaths.Root));
    }

    [Fact]
    public void TheOverrideIsReadOnEveryAccess_NotCapturedOnce()
    {
        // A value captured at type-initialisation would be decided by whichever type happened to be touched
        // first - the ordering trap that makes an override look like it works until tests run in a
        // different order.
        WithRoot("/tmp/first", static () => Assert.Equal("/tmp/first", MediaCachePaths.Root));
        WithRoot("/tmp/second", static () => Assert.Equal("/tmp/second", MediaCachePaths.Root));
    }

    [Fact]
    public void AnUnsetOrBlankOverride_FallsBackToTheSharedDefault()
    {
        WithRoot(null, static () => Assert.EndsWith("mfplayer", MediaCachePaths.Root, StringComparison.Ordinal));
        WithRoot("   ", static () => Assert.EndsWith("mfplayer", MediaCachePaths.Root, StringComparison.Ordinal));
    }

    [Fact]
    public void SubCachesHangOffTheRoot()
    {
        WithRoot("/tmp/root", static () =>
        {
            Assert.Equal(Path.Combine("/tmp/root", "youtube-cache"), MediaCachePaths.For("youtube-cache"));
            Assert.Equal(Path.Combine("/tmp/root", "mmd-bake"), MediaCachePaths.For("mmd-bake"));
        });
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData("/absolute")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANameThatIsNotASingleSegment_IsRefused(string name)
    {
        // Honouring these would put the override back in the position of not sandboxing anything, which is
        // the whole defect being fixed.
        Assert.ThrowsAny<ArgumentException>(() => MediaCachePaths.For(name));
    }
}
