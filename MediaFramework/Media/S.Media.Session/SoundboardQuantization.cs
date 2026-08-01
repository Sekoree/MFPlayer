namespace S.Media.Session;

/// <summary>
/// Launch-quantization math for host-owned soundboard surfaces (HaPlay's tile grid), so "when is the
/// next boundary" has exactly one definition. Pure and allocation-free.
/// </summary>
/// <remarks>
/// This is all that survives of the framework's abandoned soundboard-grid model: the rest of that
/// surface is owned by the host, and the unused pad/LED/binding records were removed rather than left
/// as a decoy suggesting the framework runs soundboards.
/// </remarks>
public static class SoundboardQuantization
{
    /// <summary>
    /// The next quantum boundary at or after <paramref name="when"/>, measured on the caller's own
    /// transport origin (boundaries sit at whole multiples of <paramref name="quantum"/> from that
    /// origin - the host owns what "zero" means). A non-positive quantum means "no quantization"
    /// and returns <paramref name="when"/> unchanged; a <paramref name="when"/> that already sits
    /// exactly on a boundary is returned as-is rather than pushed a full quantum out.
    /// </summary>
    public static TimeSpan NextBoundary(TimeSpan when, TimeSpan quantum)
    {
        if (quantum <= TimeSpan.Zero)
            return when;
        // Ceiling division on ticks; negative inputs (a transport origin ahead of `when`) floor to
        // the origin instead of walking backwards through boundaries.
        if (when.Ticks <= 0)
            return TimeSpan.Zero;
        var ticks = ((when.Ticks + quantum.Ticks - 1) / quantum.Ticks) * quantum.Ticks;
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>The length of <paramref name="beats"/> beats at <paramref name="bpm"/>, or
    /// <see cref="TimeSpan.Zero"/> ("no quantization") when either is non-positive.</summary>
    public static TimeSpan BeatsToQuantum(double bpm, double beats) =>
        bpm > 0 && beats > 0
            ? TimeSpan.FromSeconds(beats * 60d / bpm)
            : TimeSpan.Zero;
}
