using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class RationalFrameGridTests
{
    [Theory]
    [InlineData(60, 1)]
    [InlineData(60000, 1001)]
    [InlineData(24000, 1001)]
    [InlineData(2997, 50)]
    public void Absolute_timestamps_never_accumulate_period_rounding_error(int num, int den)
    {
        var grid = new RationalFrameGrid(num, den);
        var framesInSixHours = (long)Math.Floor(6 * 60 * 60 * (double)num / den);
        var actual = grid.TimestampAt(framesInSixHours);
        var exactTicks = (decimal)framesInSixHours * TimeSpan.TicksPerSecond * den / num;

        Assert.InRange(Math.Abs((decimal)actual.Ticks - exactTicks), 0m, 1m);
    }

    [Fact]
    public void Sixty_fps_uses_alternating_representable_boundaries_instead_of_one_rounded_period()
    {
        var grid = new RationalFrameGrid(60, 1);

        Assert.Equal(166_667, grid.DeadlineAt(1).Ticks);
        Assert.Equal(333_334, grid.DeadlineAt(2).Ticks);
        Assert.Equal(500_000, grid.DeadlineAt(3).Ticks);
        Assert.Equal(TimeSpan.FromHours(1), grid.TimestampAt(216_000));
    }

    [Fact]
    public void Snap_and_frame_index_are_stable_at_broadcast_rate()
    {
        var grid = new RationalFrameGrid(60_000, 1_001);
        const long index = 3_000_000;
        var timestamp = grid.TimestampAt(index);

        Assert.Equal(index, grid.FrameAtOrBefore(grid.DeadlineAt(index)));
        Assert.Equal(timestamp, grid.SnapAtOrBefore(timestamp));
        Assert.True(grid.TimestampAt(index + 1) > timestamp);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(60, 0)]
    [InlineData(-60, 1)]
    public void Rejects_non_positive_rates(int num, int den)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RationalFrameGrid(num, den));
    }
}
