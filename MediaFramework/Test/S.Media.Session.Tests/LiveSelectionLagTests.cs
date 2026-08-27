using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Pins the live layer's selection lag: two source periods for ordinary rates, and the 100 ms cap
/// that keeps a slideshow-rate live sender (1 fps stills) from buying a few milliseconds of phase
/// margin with a two-second standing latency.
/// </summary>
public class LiveSelectionLagTests
{
    [Theory]
    [InlineData(60, 1)]
    [InlineData(30, 1)]
    [InlineData(30000, 1001)]
    public void OrdinaryRatesGetTwoSourcePeriods(int numerator, int denominator)
    {
        var rate = new Rational(numerator, denominator);
        Assert.Equal(
            TimeSpan.FromTicks(ShowSession.SourceFramePeriod(rate).Ticks * 2),
            ShowSession.LiveSelectionLag(rate));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(12, 1)]
    public void LowRateSourcesAreCappedInsteadOfInheritingSecondsOfLatency(int numerator, int denominator)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            ShowSession.LiveSelectionLag(new Rational(numerator, denominator)));
    }

    [Fact]
    public void AnUnknownRateGetsTheSaneDefaultPeriodDoubled()
    {
        Assert.Equal(
            TimeSpan.FromTicks(ShowSession.SourceFramePeriod(default).Ticks * 2),
            ShowSession.LiveSelectionLag(default));
    }
}
