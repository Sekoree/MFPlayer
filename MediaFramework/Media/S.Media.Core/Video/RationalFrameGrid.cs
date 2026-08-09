namespace S.Media.Core.Video;

/// <summary>
/// An exact, zero-anchored frame grid. Every timestamp and deadline is derived from the absolute frame
/// index, so a rate such as 60/1 or 60000/1001 never accumulates the rounding error caused by repeatedly
/// adding a rounded <see cref="TimeSpan"/> period.
/// </summary>
public readonly struct RationalFrameGrid
{
    public RationalFrameGrid(Rational frameRate)
        : this(frameRate.Numerator, frameRate.Denominator)
    {
    }

    public RationalFrameGrid(int numerator, int denominator)
    {
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator));

        FrameRate = Rational.Reduce(numerator, denominator);
    }

    public Rational FrameRate { get; }

    /// <summary>A display-only approximation. Scheduling must use <see cref="DeadlineAt"/>.</summary>
    public TimeSpan ApproximatePeriod => TimeSpan.FromTicks((long)Math.Round(
        TimeSpan.TicksPerSecond * (double)FrameRate.Denominator / FrameRate.Numerator));

    /// <summary>
    /// Canonical PTS for <paramref name="frameIndex"/>, rounded to the first representable TimeSpan tick
    /// at or after the exact boundary. The error is always less than 100 ns and, because this is
    /// absolute-index arithmetic, never grows.
    /// </summary>
    public TimeSpan TimestampAt(long frameIndex)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        if (frameIndex == 0)
            return TimeSpan.Zero;
        var numerator = (Int128)frameIndex * TimeSpan.TicksPerSecond * FrameRate.Denominator;
        var ticks = (numerator + FrameRate.Numerator - 1) / FrameRate.Numerator;
        return TimeSpan.FromTicks(CheckedTicks(ticks));
    }

    /// <summary>
    /// First representable instant at or after the exact frame boundary. Suitable for a wake deadline:
    /// it never asks the driver to fire a frame before its rational boundary.
    /// </summary>
    public TimeSpan DeadlineAt(long frameIndex)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (frameIndex == 0)
            return TimeSpan.Zero;

        var numerator = (Int128)frameIndex * TimeSpan.TicksPerSecond * FrameRate.Denominator;
        var ticks = (numerator + FrameRate.Numerator - 1) / FrameRate.Numerator;
        return TimeSpan.FromTicks(CheckedTicks(ticks));
    }

    /// <summary>Index of the newest frame boundary not later than <paramref name="time"/>.</summary>
    public long FrameAtOrBefore(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
            return 0;

        var index = (Int128)time.Ticks * FrameRate.Numerator
                    / ((Int128)TimeSpan.TicksPerSecond * FrameRate.Denominator);
        return CheckedTicks(index);
    }

    /// <summary>Canonical timestamp of the newest grid frame not later than <paramref name="time"/>.</summary>
    public TimeSpan SnapAtOrBefore(TimeSpan time) => TimestampAt(FrameAtOrBefore(time));

    private static long CheckedTicks(Int128 value)
    {
        if (value > long.MaxValue)
            throw new OverflowException("frame-grid timestamp exceeds TimeSpan range");
        return (long)value;
    }
}
