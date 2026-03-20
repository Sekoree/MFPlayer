using System;
using System.IO;
using System.Linq;
using Seko.OwnAudioNET.Video.Probing;

namespace VideoTest;

public partial class MainWindow
{

    private static int GetSafeVideoThreadCount(MediaStreamInfoEntry videoStream)
    {
        var envOverride = Environment.GetEnvironmentVariable("VIDEOTEST_VIDEO_THREADS");
        if (int.TryParse(envOverride, out var overrideThreads) && overrideThreads > 0)
            return Math.Clamp(overrideThreads, 1, 32);

        var width = Math.Max(0, videoStream.Width ?? 0);
        var height = Math.Max(0, videoStream.Height ?? 0);
        var fps = videoStream.FrameRate.GetValueOrDefault(30);
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            fps = 30;

        var codec = videoStream.Codec ?? string.Empty;
        var codecWeight = codec.Contains("prores", StringComparison.OrdinalIgnoreCase)
            ? 1.6
            : codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) || codec.Contains("h265", StringComparison.OrdinalIgnoreCase)
                ? 1.35
                : codec.Contains("h264", StringComparison.OrdinalIgnoreCase)
                    ? 1.0
                    : 1.1;

        var weightedScore = width * (double)height * fps * codecWeight;
        var isUltraHeavy = weightedScore >= (3840d * 2160d * 60d * 1.25d);
        var reservedCores = isUltraHeavy ? 2 : 3;
        var availableCores = Math.Max(2, Environment.ProcessorCount - reservedCores);
        var suggested = (int)Math.Round(availableCores * (isUltraHeavy ? 0.50 : 0.40), MidpointRounding.AwayFromZero);
        var minThreads = isUltraHeavy ? 6 : 4;
        return Math.Clamp(Math.Max(minThreads, suggested), minThreads, 16);
    }

    private static bool IsSharedDemuxEnabled()
    {
        return !string.Equals(
            Environment.GetEnvironmentVariable("VIDEOTEST_USE_SHARED_DEMUX"),
            "0",
            StringComparison.Ordinal);
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
