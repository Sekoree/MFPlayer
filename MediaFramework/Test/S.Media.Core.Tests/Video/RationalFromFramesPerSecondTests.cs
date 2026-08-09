using Xunit;

namespace S.Media.Core.Tests.Video;

public class RationalFromFramesPerSecondTests
{
    [Theory]
    [InlineData(23.976, 24000, 1001)]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(47.952, 48000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(119.88, 120000, 1001)]
    public void Recovers_the_1001_family_from_the_rounded_decimal(double fps, int num, int den)
    {
        // The whole point: "59.94" stored as a double is not 60000/1001, and the naive fps*1000/1000
        // conversion preserved the error instead of the intent.
        Assert.Equal(new Rational(num, den), Rational.FromFramesPerSecond(fps));
    }

    [Theory]
    [InlineData(23.976023976, 24000, 1001)]
    [InlineData(59.940059940, 60000, 1001)]
    public void Recovers_the_1001_family_from_the_exact_value_too(double fps, int num, int den)
    {
        Assert.Equal(new Rational(num, den), Rational.FromFramesPerSecond(fps));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(165)]
    public void Whole_rates_stay_whole(double fps)
    {
        var rate = Rational.FromFramesPerSecond(fps);

        Assert.Equal((int)fps, rate.Numerator);
        Assert.Equal(1, rate.Denominator);
    }

    [Fact]
    public void A_nearby_but_genuinely_different_rate_is_not_snapped()
    {
        // 59.95 is its own rate, not a mistyped 59.94. Snapping it would silently retune a canvas the
        // operator deliberately matched to an unusual panel.
        var rate = Rational.FromFramesPerSecond(59.95);

        Assert.NotEqual(new Rational(60000, 1001), rate);
        Assert.Equal(59.95, rate.ToDouble(), precision: 3);
    }

    [Fact]
    public void Non_broadcast_fractional_rates_reduce_to_lowest_terms()
    {
        // 59940/1000 carries a dead factor of 20; the reduced form is the same number, legibly.
        var rate = Rational.FromFramesPerSecond(12.5);

        Assert.Equal(new Rational(25, 2), rate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unusable_input_returns_zero_so_callers_can_fall_back(double fps)
    {
        Assert.Equal(Rational.Zero, Rational.FromFramesPerSecond(fps));
    }

    [Fact]
    public void Round_trips_close_enough_that_a_canvas_matches_its_panel()
    {
        foreach (var fps in new[] { 23.976, 24, 25, 29.97, 30, 50, 59.94, 60, 120 })
        {
            var rate = Rational.FromFramesPerSecond(fps);
            Assert.True(Math.Abs(rate.ToDouble() - fps) < 0.01,
                $"{fps} round-tripped to {rate} = {rate.ToDouble()}");
        }
    }

    [Theory]
    [InlineData(59940, 1000, 2997, 50)]
    [InlineData(60, 1, 60, 1)]
    [InlineData(30000, 1001, 30000, 1001)]
    public void Reduce_divides_out_the_common_factor(int num, int den, int expectedNum, int expectedDen)
    {
        Assert.Equal(new Rational(expectedNum, expectedDen), Rational.Reduce(num, den));
    }

    [Fact]
    public void Reduce_of_a_zero_denominator_is_zero_rather_than_a_divide_by_zero()
    {
        Assert.Equal(Rational.Zero, Rational.Reduce(60, 0));
    }
}
