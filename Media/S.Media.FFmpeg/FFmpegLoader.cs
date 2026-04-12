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
    /// Resolves FFmpeg shared libraries. On Linux/macOS the OS loader handles this
    /// via ldconfig/DYLD_LIBRARY_PATH. On Windows supply a folder via
    /// <paramref name="searchPath"/>.
    /// </summary>
    public static void EnsureLoaded(string? searchPath = null)
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
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
