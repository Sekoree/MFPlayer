using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

public sealed class FFmpegRuntimeResolverTests
{
    /// <summary>
    /// On unix the resolver defers to the platform loader when the SYSTEM has the ABI major this build
    /// binds, and only names a directory when it does not.
    /// </summary>
    /// <remarks>
    /// It used to return <c>""</c> unconditionally, i.e. "the system FFmpeg or nothing". That is fine
    /// until the distro moves to the next FFmpeg major, at which point the versioned soname stops
    /// resolving and there is no way to run short of downgrading the whole machine — with the failure
    /// showing up as every media file appearing offline. A staged build is the way out, so a non-empty
    /// answer is now correct precisely when the system cannot serve.
    /// </remarks>
    [Fact]
    public void ResolveDefaultRootPath_PrefersTheSystemLoaderAndFallsBackToAStagedBuild()
    {
        if (OperatingSystem.IsWindows())
            return;

        var resolved = FFmpegRuntime.ResolveDefaultRootPath();

        if (resolved.Length == 0)
            return; // system has a matching set; the loader is the right answer

        // Otherwise it must be a real directory holding a COMPLETE set — a partial one would let the
        // loader mix a staged avcodec with a system avutil, which is undefined behaviour, not a fallback.
        Assert.True(Directory.Exists(resolved), $"resolved root '{resolved}' does not exist");
        Assert.NotEmpty(Directory.GetFiles(resolved, "libavcodec.so.*"));
        Assert.NotEmpty(Directory.GetFiles(resolved, "libavutil.so.*"));
    }

    [Fact]
    public void ResolveDefaultRootPath_HonoursTheEnvironmentOverrideOnlyWhenTheSetIsComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mfplayer-ffmpeg-override-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable(FFmpegRuntime.EnvironmentOverride);
        try
        {
            Directory.CreateDirectory(root);
            // Deliberately empty: an override pointing somewhere useless must be ignored rather than
            // pinning the loader to a directory with nothing in it.
            Environment.SetEnvironmentVariable(FFmpegRuntime.EnvironmentOverride, root);

            Assert.NotEqual(Path.GetFullPath(root), FFmpegRuntime.ResolveDefaultRootPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(FFmpegRuntime.EnvironmentOverride, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindCompleteNativeDirectory_SkipsIncompleteAndAppLocalSets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mfplayer-ffmpeg-resolver-{Guid.NewGuid():N}");
        var incomplete = Path.Combine(root, "incomplete");
        var appLocal = Path.Combine(root, "app");
        var system = Path.Combine(root, "system");
        var files = new[] { "avcodec-62.dll", "avutil-60.dll" };

        try
        {
            Directory.CreateDirectory(incomplete);
            Directory.CreateDirectory(appLocal);
            Directory.CreateDirectory(system);
            File.WriteAllText(Path.Combine(incomplete, files[0]), "");
            foreach (var directory in new[] { appLocal, system })
                foreach (var file in files)
                    File.WriteAllText(Path.Combine(directory, file), "");

            var found = FFmpegRuntime.FindCompleteNativeDirectory(
                [incomplete, appLocal, system], files, appLocal);

            Assert.Equal(Path.GetFullPath(system), found);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
