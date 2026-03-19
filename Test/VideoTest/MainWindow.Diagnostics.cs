using System;
using System.IO;
using System.Linq;

namespace VideoTest;

public partial class MainWindow
{
    private static readonly string[] SupportedVideoExtensions =
    [
        ".mov", ".mp4", ".mkv", ".avi", ".m4v", ".webm"
    ];

    private static int GetSafeVideoThreadCount()
    {
        var suggested = Math.Max(4, Environment.ProcessorCount / 2);
        return Math.Min(16, suggested);
    }

    private static string? ResolveFfmpegRootPath()
    {
        var envOverride = Environment.GetEnvironmentVariable("FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(envOverride) && Directory.Exists(envOverride))
            return envOverride;

        string[] candidates =
        [
            "/lib",
            "/usr/lib",
            "/usr/local/lib",
            "/usr/lib/x86_64-linux-gnu"
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveTestMediaPath(string[]? launchArgs)
    {
        if (launchArgs is { Length: > 0 })
        {
            var argPath = ExpandPath(launchArgs[0]);
            if (File.Exists(argPath))
                return argPath;
        }

        var envPath = Environment.GetEnvironmentVariable("MFPLAYER_TEST_VIDEO");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var expanded = ExpandPath(envPath);
            if (File.Exists(expanded))
                return expanded;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] searchRoots =
        [
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.Combine(home, "Videos"),
            Path.Combine(home, "Downloads")
        ];

        foreach (var root in searchRoots.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var match = Directory.EnumerateFiles(root)
                    .Where(static path => SupportedVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (match != null)
                    return match;
            }
            catch
            {
                // Best effort only.
            }
        }

        return null;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Overwrites the current console line in-place using a carriage return.
    /// Pads with spaces so any shorter previous content is fully cleared.
    /// </summary>
    private static void ConsoleOverwriteLine(string message)
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch
        {
            width = 0;
        }

        var line = width > 1 ? message.PadRight(width - 1) : message;
        Console.Write("\r" + line);
    }

    /// <summary>
    /// Prints <paramref name="message"/> on its own line, first advancing past any
    /// in-place overwritten stat line that has no trailing newline yet.
    /// </summary>
    private static void ConsolePrintLine(string message)
    {
        Console.WriteLine("\n" + message);
    }

    /// <summary>
    /// Returns a compact lowercase pixel-format name, stripping the verbose
    /// <c>AV_PIX_FMT_</c> prefix that FFmpeg uses for source-format strings.
    /// </summary>
    private static string FmtName(string name)
    {
        const string prefix = "AV_PIX_FMT_";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..].ToLowerInvariant()
            : name.ToLowerInvariant();
    }
}
