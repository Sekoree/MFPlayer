using System.Runtime.CompilerServices;
using S.Media.Core;

namespace HaPlay.Tests;

internal static class TestCacheSandbox
{
    /// <summary>
    /// Redirects HaPlay's per-machine cache root (via <c>HAPLAY_CACHE_ROOT</c>) into a throwaway temp directory
    /// for the whole test run. Constructing a <c>MainViewModel</c> spins up session recovery, which creates a
    /// folder under the cache root; without this, every such test would litter the real user cache. Runs once at
    /// assembly load, before any test executes.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("HAPLAY_CACHE_ROOT") is null or "")
        {
            var dir = Path.Combine(Path.GetTempPath(), "haplay-test-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("HAPLAY_CACHE_ROOT", dir);
        }

        // The framework's SHARED media cache (prepared YouTube assets, baked MMD physics) is a separate
        // root: it is keyed by source material rather than by app, so it deliberately lives outside
        // HAPLAY_CACHE_ROOT. Redirecting only the app root left both of those writing into the developer's
        // real cache directory for the whole suite.
        if (Environment.GetEnvironmentVariable(MediaCachePaths.RootOverrideVariable) is null or "")
        {
            var mediaDir = Path.Combine(Path.GetTempPath(), "mfplayer-test-cache", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mediaDir);
            Environment.SetEnvironmentVariable(MediaCachePaths.RootOverrideVariable, mediaDir);
        }
        // MainViewModel-heavy tests explicitly exercise flush/recovery where relevant. Disabling its recurring
        // dispatcher timer prevents hundreds of short-lived test VMs being retained by timer event handlers.
        Environment.SetEnvironmentVariable("HAPLAY_DISABLE_RECOVERY_TIMER", "1");
    }
}
