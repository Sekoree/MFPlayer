using System.Diagnostics;

namespace S.Media.Core.Video;

/// <summary>
/// Shared timing helpers for source-FPS-paced render loops.
/// </summary>
public static class RenderCadenceHelper
{
    /// <summary>
    /// Controls how late ticks are handled when pacing renders.
    /// </summary>
    public enum LateTickPolicy
    {
        /// <summary>
        /// If late, keep cadence anchored and wait for the next interval boundary.
        /// </summary>
        WaitForNextBoundary,

        /// <summary>
        /// If late, render immediately and continue from "now".
        /// </summary>
        PresentImmediately,
    }

    /// <summary>
    /// Converts an FPS value into stopwatch ticks per frame.
    /// </summary>
    public static bool TryGetFrameIntervalTicks(double fps, out long intervalTicks)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
        {
            intervalTicks = 0;
            return false;
        }

        intervalTicks = Math.Max(1L, (long)Math.Round(Stopwatch.Frequency / fps));
        return true;
    }

    /// <summary>
    /// Returns the initial due-tick when pacing is enabled.
    /// </summary>
    public static long InitialDue(long nowTicks, long intervalTicks, bool immediateFirstTick)
        => immediateFirstTick ? nowTicks : nowTicks + intervalTicks;

    /// <summary>
    /// Normalizes due-ticks relative to the current time according to late-tick policy.
    /// </summary>
    public static long NormalizeDue(long dueTicks, long nowTicks, long intervalTicks, LateTickPolicy latePolicy)
    {
        if (dueTicks <= 0)
            return InitialDue(nowTicks, intervalTicks, immediateFirstTick: latePolicy == LateTickPolicy.PresentImmediately);

        if (dueTicks > nowTicks)
            return dueTicks;

        if (latePolicy == LateTickPolicy.PresentImmediately)
            return nowTicks;

        long missed = (nowTicks - dueTicks) / intervalTicks + 1;
        return dueTicks + missed * intervalTicks;
    }

    /// <summary>
    /// Computes the next due-tick after a render.
    /// </summary>
    public static long ComputeNextDue(long dueTicks, long nowTicks, long intervalTicks)
    {
        long nextDue = dueTicks + intervalTicks;
        if (nextDue <= nowTicks)
        {
            long missed = (nowTicks - dueTicks) / intervalTicks + 1;
            nextDue = dueTicks + missed * intervalTicks;
        }

        return nextDue;
    }

    /// <summary>
    /// Converts due/now stopwatch ticks into a non-negative delay.
    /// </summary>
    public static TimeSpan ComputeDelay(long nowTicks, long dueTicks)
    {
        long delayTicks = dueTicks - nowTicks;
        if (delayTicks <= 0)
            return TimeSpan.Zero;

        return TimeSpan.FromSeconds((double)delayTicks / Stopwatch.Frequency);
    }

    /// <summary>
    /// Computes remaining milliseconds until due.
    /// </summary>
    public static double ComputeRemainingMilliseconds(long nowTicks, long dueTicks)
    {
        long remainingTicks = dueTicks - nowTicks;
        if (remainingTicks <= 0)
            return 0.0;

        return remainingTicks * 1000.0 / Stopwatch.Frequency;
    }
}
