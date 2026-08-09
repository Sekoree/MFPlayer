using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoFormatPacingTests
{
    private static double Hz(TimeSpan period) => TimeSpan.TicksPerSecond / (double)period.Ticks;

    [Fact]
    public void Unknown_rate_returns_zero_so_the_caller_keeps_its_own_default()
    {
        Assert.Equal(TimeSpan.Zero, VideoFormatPacing.PresentationTickInterval(new Rational(0, 1)));
        Assert.Equal(TimeSpan.Zero, VideoFormatPacing.PresentationTickInterval(new Rational(30, 0)));
        Assert.Equal(TimeSpan.Zero, VideoFormatPacing.PresentationTickInterval(new Rational(-30, 1)));
    }

    [Theory]
    [InlineData(60000, 1001)]   // 59.94
    [InlineData(60, 1)]
    [InlineData(30000, 1001)]   // 29.97
    [InlineData(25, 1)]
    [InlineData(24000, 1001)]   // 23.976
    [InlineData(50, 1)]
    public void Tick_runs_strictly_faster_than_the_source_so_two_frames_never_come_due_together(int num, int den)
    {
        var rate = new Rational(num, den);

        var hz = Hz(VideoFormatPacing.PresentationTickInterval(rate));

        // The whole point: at or below the source rate, a beat between the two periodically retires
        // two frames in one tick and the older one is discarded before anything downstream sees it.
        Assert.True(hz > rate.ToDouble(), $"{hz} Hz tick must exceed the {rate.ToDouble()} fps source");
    }

    [Fact]
    public void Default_oversample_doubles_the_source_rate()
    {
        var hz = Hz(VideoFormatPacing.PresentationTickInterval(new Rational(60000, 1001)));

        Assert.Equal(2 * (60000 / 1001d), hz, precision: 1);
    }

    [Fact]
    public void Slow_content_is_floored_so_seek_response_and_held_frames_stay_usable()
    {
        // 2 fps doubled is 4 Hz - too sluggish to re-submit a held final frame or answer a seek.
        var hz = Hz(VideoFormatPacing.PresentationTickInterval(new Rational(2, 1)));

        Assert.Equal(VideoFormatPacing.MinPresentationTickHz, hz, precision: 1);
    }

    [Fact]
    public void Fast_content_is_capped_so_driver_wakeups_stay_bounded()
    {
        var hz = Hz(VideoFormatPacing.PresentationTickInterval(new Rational(240, 1)));

        Assert.Equal(VideoFormatPacing.MaxPresentationTickHz, hz, precision: 1);
    }

    [Fact]
    public void Oversample_is_configurable_between_the_clamps()
    {
        var hz = Hz(VideoFormatPacing.PresentationTickInterval(new Rational(60, 1), oversample: 1.5));

        Assert.Equal(90d, hz, precision: 1);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void Nonsense_oversample_falls_back_to_the_default_rather_than_producing_a_degenerate_tick(double oversample)
    {
        var rate = new Rational(60, 1);

        var actual = VideoFormatPacing.PresentationTickInterval(rate, oversample);

        Assert.Equal(VideoFormatPacing.PresentationTickInterval(rate), actual);
    }

    [Fact]
    public void A_59_94_source_no_longer_beats_against_the_old_fixed_60_Hz_tick()
    {
        // Regression guard for the reported stutter: the fixed ~60 Hz default sat BELOW 59.94 x2 and,
        // sharing no phase with the source, dropped a decoded frame every ~17 s at this stage alone.
        var rate = new Rational(60000, 1001);

        var derived = VideoFormatPacing.PresentationTickInterval(rate);

        Assert.True(derived < MediaClockDefaultVideoTick, "derived tick must be shorter than the old fixed default");
        Assert.True(Hz(derived) > rate.ToDouble());
    }

    // Mirrors MediaClock.DefaultVideoTickInterval without taking a dependency on S.Media.Time
    // (Core sits below it in the layering).
    private static readonly TimeSpan MediaClockDefaultVideoTick = TimeSpan.FromTicks(166_667);
}
