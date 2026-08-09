namespace S.Media.Core.Video;

/// <summary>
/// Exact integer ratio. Used for things like 29.97 fps (30000/1001) where
/// a <see cref="double"/> would silently round.
/// </summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    public static readonly Rational Zero = new(0, 1);

    public double ToDouble() => Denominator == 0 ? 0.0 : (double)Numerator / Denominator;

    public override string ToString() => $"{Numerator}/{Denominator}";

    /// <summary>
    /// How far a frame rate may sit from a broadcast rate and still be taken as that rate. Wide enough
    /// to catch "59.94" typed or rounded from a display mode, narrow enough that a genuinely different
    /// panel rate (59.95, say) is left alone.
    /// </summary>
    private const double BroadcastSnapTolerance = 0.005;

    /// <summary>
    /// The 1001-denominator family, which is the whole reason this type exists. A rate stored as a
    /// <see cref="double"/> can only ever be 59.94, and 59.94 is not 60000/1001 - the difference is
    /// about a frame an hour, but it is also the difference between an exact match to a panel and a
    /// slow beat against it.
    /// </summary>
    private static readonly Rational[] BroadcastRates =
    [
        new(24000, 1001),   // 23.976
        new(30000, 1001),   // 29.97
        new(48000, 1001),   // 47.952
        new(60000, 1001),   // 59.94
        new(120000, 1001),  // 119.88
    ];

    /// <summary>
    /// Recovers an exact ratio from a frame rate that was only ever stored as a <see cref="double"/>.
    /// </summary>
    /// <remarks>
    /// Snaps to a whole number, then to the 1001-denominator broadcast family, and otherwise falls back
    /// to thousandths reduced to lowest terms. Non-finite or non-positive input returns
    /// <see cref="Zero"/>, which callers treat as "unknown".
    /// </remarks>
    public static Rational FromFramesPerSecond(double framesPerSecond)
    {
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            return Zero;

        var whole = Math.Round(framesPerSecond);
        if (whole is > 0 and <= int.MaxValue && Math.Abs(framesPerSecond - whole) < 1e-9)
            return new Rational((int)whole, 1);

        foreach (var candidate in BroadcastRates)
        {
            if (Math.Abs(framesPerSecond - candidate.ToDouble()) < BroadcastSnapTolerance)
                return candidate;
        }

        var thousandths = Math.Round(framesPerSecond * 1000);
        if (thousandths is <= 0 or > int.MaxValue)
            return Zero;

        return Reduce((int)thousandths, 1000);
    }

    /// <summary>Lowest terms, so 59940/1000 reads as 2997/50 rather than carrying a dead factor of 20.</summary>
    public static Rational Reduce(int numerator, int denominator)
    {
        if (denominator == 0)
            return Zero;

        var divisor = Gcd(Math.Abs(numerator), Math.Abs(denominator));
        if (divisor <= 1)
            return new Rational(numerator, denominator);

        return new Rational(numerator / divisor, denominator / divisor);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
