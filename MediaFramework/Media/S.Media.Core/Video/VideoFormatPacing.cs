namespace S.Media.Core.Video;

/// <summary>Wall-clock pacing helpers for video outputs (NDI throttling, etc.).</summary>
public static class VideoFormatPacing
{
    /// <summary>Wall throttle slightly below one frame period (e.g. NDI video pacing).</summary>
    public static TimeSpan PaceBelowFramePeriod(VideoFormat fmt)
    {
        var fps = fmt.FrameRate.ToDouble();
        if (fps <= 0 || double.IsNaN(fps)) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(1.0 / fps * 0.93);
    }

    /// <summary>
    /// How much faster than the source's frame rate a scheduled presentation tick runs by default.
    /// </summary>
    /// <remarks>
    /// Must be greater than 1. A tick running AT the source rate beats against it: the two have
    /// independent phase, so periodically two frames come due in one tick and periodically none does.
    /// A scheduler that keeps only the newest due frame therefore discards a decoded frame outright on
    /// every beat crossing - before any downstream consumer can see it. Oversampling guarantees at most
    /// one frame comes due per tick, and halves the worst-case wait between a frame becoming due and
    /// being handed on.
    /// </remarks>
    public const double DefaultPresentationOversample = 2d;

    /// <summary>
    /// Floor for <see cref="PresentationTickInterval"/>. Very low frame-rate content still needs a tick
    /// often enough to stay responsive to a seek and to re-submit a held final frame at a usable rate.
    /// </summary>
    public const double MinPresentationTickHz = 30d;

    /// <summary>
    /// Ceiling for <see cref="PresentationTickInterval"/>. Past this the driver thread's wakeup cost
    /// outweighs the extra placement precision.
    /// </summary>
    public const double MaxPresentationTickHz = 240d;

    /// <summary>
    /// Presentation-tick period for a source running at <paramref name="frameRate"/>: the cadence at
    /// which a scheduled player should re-check whether a decoded frame has come due.
    /// </summary>
    /// <param name="frameRate">The SOURCE's frame rate. A non-positive or non-finite rate returns
    /// <see cref="TimeSpan.Zero"/>, meaning "unknown - keep your own default".</param>
    /// <param name="oversample">Multiple of <paramref name="frameRate"/> to tick at. Non-positive or
    /// non-finite falls back to <see cref="DefaultPresentationOversample"/>.</param>
    /// <returns>The tick period, clamped to
    /// [<see cref="MinPresentationTickHz"/>, <see cref="MaxPresentationTickHz"/>]; or
    /// <see cref="TimeSpan.Zero"/> when the rate is unknown.</returns>
    public static TimeSpan PresentationTickInterval(
        Rational frameRate,
        double oversample = DefaultPresentationOversample)
    {
        var fps = frameRate.ToDouble();
        if (!double.IsFinite(fps) || fps <= 0)
            return TimeSpan.Zero;

        if (!double.IsFinite(oversample) || oversample <= 0)
            oversample = DefaultPresentationOversample;

        var hz = Math.Clamp(fps * oversample, MinPresentationTickHz, MaxPresentationTickHz);
        return TimeSpan.FromTicks(Math.Max(1L, (long)Math.Round(TimeSpan.TicksPerSecond / hz)));
    }
}
