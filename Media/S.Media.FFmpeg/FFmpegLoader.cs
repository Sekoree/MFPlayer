using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace S.Media.FFmpeg;

/// <summary>
/// One-time initialisation for FFmpeg native libraries.
/// Call <see cref="EnsureLoaded"/> before any FFmpeg API use.
/// </summary>
public static class FFmpegLoader
{
    private static readonly ILogger Log = FFmpegLogging.GetLogger(nameof(FFmpegLoader));
    private static bool _loaded;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Environment variable consulted by <see cref="EnsureLoaded"/> / <see cref="ResolveDefaultSearchPath"/>
    /// before falling back to OS-specific defaults.  Set this to pin a specific FFmpeg build per host.
    /// </summary>
    public const string SearchPathEnvVar = "MFPLAYER_FFMPEG_PATH";

    /// <summary>
    /// Picks a reasonable FFmpeg search path for the current platform.
    /// Priority: <see cref="SearchPathEnvVar"/> environment variable → OS default.
    /// OS defaults: Linux = <c>/usr/lib</c>, macOS = <c>/usr/local/lib</c>, Windows = <c>null</c>
    /// (the loader will search PATH; callers should set <c>ffmpeg.RootPath</c> explicitly if needed).
    /// </summary>
    public static string? ResolveDefaultSearchPath()
    {
        var env = Environment.GetEnvironmentVariable(SearchPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return "/usr/lib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return "/usr/local/lib";
        return null; // Windows: rely on PATH unless caller overrides
    }

    /// <summary>
    /// Resolves FFmpeg shared libraries. On Linux/macOS the OS loader handles this
    /// via ldconfig/DYLD_LIBRARY_PATH. On Windows supply a folder via
    /// <paramref name="searchPath"/>, or omit to auto-resolve via
    /// <see cref="ResolveDefaultSearchPath"/>.
    /// </summary>
    public static void EnsureLoaded(string? searchPath = null)
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            searchPath ??= ResolveDefaultSearchPath();
            Log.LogInformation("Loading FFmpeg libraries, searchPath={SearchPath}", searchPath ?? "(default)");
            if (!string.IsNullOrEmpty(searchPath))
                ffmpeg.RootPath = searchPath;
            DynamicallyLoadedBindings.Initialize();
            // Force a cheap FFmpeg call to trigger native library load and surface errors early.
            var version = ffmpeg.avformat_version();
            _loaded = true;
            Log.LogInformation("FFmpeg loaded: avformat version={Version}", version);
        }
    }
}
