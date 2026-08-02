using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Per-app storage roots: a second app on the same machine must not write its settings, recent-projects,
/// recovery folders and script scratch into HaPlay's.
/// </summary>
/// <remarks>
/// These mutate process-wide state (<see cref="HaPlayStoragePaths.AppName"/> and an environment variable),
/// so they run in their own non-parallel collection and restore what they touched. Everything is put back
/// in a finally, because leaving the app name changed would silently redirect every later test's storage.
/// </remarks>
[Collection(nameof(StoragePathsTests))]
[CollectionDefinition(nameof(StoragePathsTests), DisableParallelization = true)]
public sealed class StoragePathsTests
{
    /// <summary>Runs <paramref name="body"/> with a given app name, restoring the previous one after.</summary>
    private static void WithAppName(string name, Action body)
    {
        var previous = HaPlayStoragePaths.AppName;
        var previousOverride = Environment.GetEnvironmentVariable(
            name.ToUpperInvariant() + "_CACHE_ROOT");
        HaPlayStoragePaths.ResetForTests();
        HaPlayStoragePaths.AppName = name;
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name.ToUpperInvariant() + "_CACHE_ROOT", previousOverride);
            HaPlayStoragePaths.ResetForTests();
            HaPlayStoragePaths.AppName = previous;
        }
    }

    [Fact]
    public void TheDefaultAppIsHaPlay_AndItsOverrideKeepsItsHistoricalName()
    {
        // HAPLAY_CACHE_ROOT is baked into CI and the test sandbox; deriving the name must not rename it.
        Assert.Equal("HaPlay", HaPlayStoragePaths.AppName);
        Assert.Equal("HAPLAY_CACHE_ROOT", HaPlayStoragePaths.RootOverrideVariable);
    }

    [Fact]
    public void ASecondAppGetsItsOwnRootAndItsOwnOverride()
    {
        WithAppName("HaCue2", static () =>
        {
            Assert.Equal("HACUE2_CACHE_ROOT", HaPlayStoragePaths.RootOverrideVariable);

            // Its root must not be HaPlay's - that collision is the whole point of parameterising this.
            Environment.SetEnvironmentVariable("HACUE2_CACHE_ROOT", null);
            Assert.EndsWith("HaCue2", HaPlayStoragePaths.LocalAppRoot, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EachAppsOverrideRedirectsOnlyItsOwnRoot()
    {
        WithAppName("HaCue2", static () =>
        {
            Environment.SetEnvironmentVariable("HACUE2_CACHE_ROOT", "/tmp/hacue2-root");
            Assert.Equal("/tmp/hacue2-root", HaPlayStoragePaths.LocalAppRoot);
            Assert.Equal(Path.Combine("/tmp/hacue2-root", "recovery"), HaPlayStoragePaths.RecoveryRoot);
        });
    }

    [Fact]
    public void ChangingTheAppNameAfterPathsResolved_Throws()
    {
        WithAppName("HaCue2", static () =>
        {
            _ = HaPlayStoragePaths.LocalAppRoot; // resolve once

            // A late change would leave one run writing under two roots - which surfaces later as
            // "my settings vanished" and is near-impossible to trace back to the assignment.
            Assert.Throws<InvalidOperationException>(() => HaPlayStoragePaths.AppName = "Something Else");
        });
    }

    [Fact]
    public void ReassigningTheSameNameAfterResolving_IsAllowed()
    {
        WithAppName("HaCue2", static () =>
        {
            _ = HaPlayStoragePaths.LocalAppRoot;
            HaPlayStoragePaths.AppName = "HaCue2"; // no-op, must not throw
            Assert.Equal("HaCue2", HaPlayStoragePaths.AppName);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    public void AnUnusableAppName_IsRefused(string name)
    {
        var previous = HaPlayStoragePaths.AppName;
        try
        {
            HaPlayStoragePaths.ResetForTests();
            Assert.ThrowsAny<ArgumentException>(() => HaPlayStoragePaths.AppName = name);
            Assert.Equal(previous, HaPlayStoragePaths.AppName); // rejected, not half-applied
        }
        finally
        {
            HaPlayStoragePaths.ResetForTests();
            HaPlayStoragePaths.AppName = previous;
        }
    }
}
