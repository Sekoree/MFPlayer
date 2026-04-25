using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class HudStatsTests
{
    [Fact]
    public void ToLines_IncludesClockSourceAndDrift_WhenSet()
    {
        var stats = new HudStats
        {
            Width = 1920,
            Height = 1080,
            PixelFormat = PixelFormat.Bgra32,
            Fps = 59.94,
            ClockPosition = TimeSpan.FromSeconds(12.345),
            ClockName = "StopwatchClock",
            Drift = TimeSpan.FromMilliseconds(12.3)
        };

        var lines = stats.ToLines();

        Assert.Contains("clock src: StopwatchClock", lines);
        Assert.Contains("drift: +12.3ms", lines);
    }

    [Fact]
    public void ToLines_OmitsClockSourceAndDrift_WhenNotSet()
    {
        var stats = new HudStats
        {
            Width = 640,
            Height = 360,
            PixelFormat = PixelFormat.Rgba32,
            Fps = 30,
            ClockPosition = TimeSpan.Zero,
            Drift = TimeSpan.Zero
        };

        var lines = stats.ToLines();

        Assert.DoesNotContain(lines, l => l.StartsWith("clock src:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("drift:", StringComparison.Ordinal));
    }
}
