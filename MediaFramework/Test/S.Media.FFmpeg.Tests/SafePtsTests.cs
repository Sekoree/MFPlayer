using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Tests for <see cref="FFmpegVideoChannel.SafePts"/>.
/// Accesses the internal method via InternalsVisibleTo.
/// No FFmpeg native libraries required.
/// </summary>
public sealed class SafePtsTests
{
    // Helper to invoke the internal static method.
    private static TimeSpan SafePts(long pts, double tbSeconds)
        => FFmpegVideoChannel.SafePts(pts, tbSeconds);

    [Fact]
    public void SafePts_AVNoptsValue_ReturnsZero()
    {
        // AV_NOPTS_VALUE == long.MinValue
        var result = SafePts(long.MinValue, 1.0 / 90000);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_ZeroTimebase_ReturnsZero()
    {
        var result = SafePts(1000, 0.0);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_NegativeTimebase_ReturnsZero()
    {
        var result = SafePts(1000, -1.0 / 90000);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_InfiniteTimebase_ReturnsZero()
    {
        var result = SafePts(1, double.PositiveInfinity);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_ValidPts_ReturnsCorrectTimeSpan()
    {
        // 90000 ticks at 1/90000 timebase == exactly 1 second
        double tb     = 1.0 / 90000;
        var    result = SafePts(90000, tb);
        Assert.InRange(result.TotalSeconds, 0.999, 1.001);
    }

    [Fact]
    public void SafePts_ZeroPts_ReturnsZero()
    {
        var result = SafePts(0, 1.0 / 90000);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_NegativePts_ReturnsZero()
    {
        // Negative PTS (e.g. audio pre-roll) should clamp to zero.
        var result = SafePts(-1000, 1.0 / 90000);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void SafePts_HugeValue_ClampsToMaxValue()
    {
        // A PTS that would overflow TimeSpan should clamp to TimeSpan.MaxValue.
        var result = SafePts(long.MaxValue / 2, 1.0);
        Assert.Equal(TimeSpan.MaxValue, result);
    }

    [Fact]
    public void SafePts_HalfSecond_VideoTimebase()
    {
        // 12500 ticks at 1/25000 timebase == 0.5 s
        double tb     = 1.0 / 25000;
        var    result = SafePts(12500, tb);
        Assert.InRange(result.TotalSeconds, 0.499, 0.501);
    }

    [Fact]
    public void SafePts_TypicalAudioPts_AudioTimebase()
    {
        // 48000 ticks at 1/48000 timebase == exactly 1 second
        double tb     = 1.0 / 48000;
        var    result = SafePts(48000, tb);
        Assert.InRange(result.TotalSeconds, 0.999, 1.001);
    }
}

