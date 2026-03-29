using FFmpeg.AutoGen;

namespace S.Media.FFmpeg.Runtime;

/// <summary>
/// Bootstrap helper for the FFmpeg native bindings.
/// </summary>
public static class FFmpegRuntime
{
    private static int _initialized;

    private static readonly string[] DefaultSearchPaths =
    [
        "/lib",
        "/usr/lib",
        "/usr/local/lib",
        "/usr/lib/x86_64-linux-gnu",
    ];

    /// <summary>
    /// Ensures the FFmpeg native library bindings are initialized.  Safe to call multiple times —
    /// only the first call performs any work.
    /// <para>
    /// Resolution order:
    /// <list type="number">
    ///   <item><description>The <c>FFMPEG_ROOT</c> environment variable (if set and the directory exists).</description></item>
    ///   <item><description>Standard system paths: <c>/lib</c>, <c>/usr/lib</c>, <c>/usr/local/lib</c>, <c>/usr/lib/x86_64-linux-gnu</c>.</description></item>
    ///   <item><description>OS default library search (no <see cref="ffmpeg.RootPath"/> override applied).</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        var envRoot = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
        var root = !string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot)
            ? envRoot
            : DefaultSearchPaths.FirstOrDefault(Directory.Exists);

        if (!string.IsNullOrEmpty(root))
            ffmpeg.RootPath = root;

        DynamicallyLoadedBindings.Initialize();
    }
}

