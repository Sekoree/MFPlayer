using Xunit;

namespace S.Media.FFmpeg.Tests;

internal sealed class HeavyFfmpegFactAttribute : FactAttribute
{
    public HeavyFfmpegFactAttribute()
    {
        if (!HeavyFfmpegTestConfig.IsEnabled())
        {
            Skip = "Set RUN_HEAVY_FFMPEG_TESTS=1 (or SMEDIA_RUN_HEAVY_STRESS=1) to enable heavy FFmpeg stress tests.";
            return;
        }

        var path = HeavyFfmpegTestConfig.ResolveVideoPath();
        if (!File.Exists(path))
        {
            Skip = $"Heavy FFmpeg test asset not found: {path}";
        }
    }
}

internal static class HeavyFfmpegTestConfig
{
    private const string DefaultHeavyVideoPath = "/home/sekoree/Videos/shootingstar_0611_1.mov";

    public static string ResolveVideoPath()
    {
        var heavyVideoPath = Environment.GetEnvironmentVariable("SMEDIA_HEAVY_VIDEO_PATH");
        return string.IsNullOrWhiteSpace(heavyVideoPath) ? DefaultHeavyVideoPath : heavyVideoPath;
    }

    public static bool IsEnabled()
    {
        var primary = Environment.GetEnvironmentVariable("RUN_HEAVY_FFMPEG_TESTS");
        var legacy = Environment.GetEnvironmentVariable("SMEDIA_RUN_HEAVY_STRESS");
        return IsTruthy(primary) || IsTruthy(legacy);
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

