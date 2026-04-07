using Xunit;

namespace S.Media.FFmpeg.Tests.Helpers;

/// <summary>
/// xUnit collection fixture that initialises FFmpeg native libraries once per test run.
/// Tries common system locations so tests work across distros.
/// </summary>
public sealed class FfmpegFixture
{
    public FfmpegFixture()
    {
        // Try common Linux/macOS locations for FFmpeg shared libraries.
        string[] candidates =
        [
            "/usr/lib",
            "/usr/local/lib",
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib/aarch64-linux-gnu",
            "/opt/homebrew/lib",        // macOS Homebrew
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path) &&
                (Directory.GetFiles(path, "libavformat*").Length > 0 ||
                 Directory.GetFiles(path, "libavformat.*").Length > 0))
            {
                FFmpegLoader.EnsureLoaded(path);
                return;
            }
        }

        // Fall back to letting the OS loader find it on PATH / LD_LIBRARY_PATH.
        FFmpegLoader.EnsureLoaded();
    }
}

[Xunit.CollectionDefinition("FFmpeg")]
public sealed class FfmpegCollection : Xunit.ICollectionFixture<FfmpegFixture> { }

