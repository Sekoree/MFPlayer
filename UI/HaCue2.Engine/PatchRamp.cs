using HaCue2.Core.Model;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>
/// Interpolates between two patch states so a patch cue's fade is heard rather than jumped.
/// </summary>
/// <remarks>
/// <para>
/// The bay's own <c>UpdatePatch</c> reconciles a matrix over the next chunk - a few milliseconds, which
/// is right for an operator dragging a cell and wrong for a cue that says "over four seconds". So the
/// cue's ramp is stepped HERE, pushing a series of intermediate matrices, and the document is written
/// once with the destination values.
/// </para>
/// <para>
/// <b>Gains interpolate in decibels, not in linear gain.</b> A linear ramp from unity to silence spends
/// most of its time inaudibly quiet and sounds like a cut followed by a wait; dB is the scale the
/// operator authored the number on.
/// </para>
/// <para>
/// <b>Mute is not interpolated</b> - it is a state, not a level. A cell that ends muted rides its gain
/// down and mutes at the end; one that ends unmuted unmutes at the start and rides up. Either way the
/// audible result is a ramp rather than a step, and the end state is exactly what the document says.
/// </para>
/// </remarks>
public static class PatchRamp
{
    /// <summary>How often the ramp pushes a matrix. Fast enough to hear as smooth, slow enough that a
    /// four-second fade is forty updates rather than four hundred.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    /// <summary>The number of steps a ramp of this length takes, always at least one.</summary>
    public static int StepsFor(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 1 : Math.Max(1, (int)Math.Round(duration / Step));

    /// <summary>
    /// One frame of the ramp: every cell of <paramref name="destination"/>, moved
    /// <paramref name="progress"/> of the way from where it was.
    /// </summary>
    /// <remarks>
    /// Keyed off the DESTINATION, so a cell the patch cue does not mention keeps whatever it has -
    /// which is the partial-recall promise, held one frame at a time. A destination cell with no prior
    /// value fades up from silence rather than appearing at full level.
    /// </remarks>
    public static List<PatchCell> Blend(
        IReadOnlyList<PatchCell> origin, IReadOnlyList<PatchCell> destination, double progress)
        => Blend(origin, destination, progress, new FadeShape(FadeCurve.Linear));

    public static List<PatchCell> Blend(
        IReadOnlyList<PatchCell> origin,
        IReadOnlyList<PatchCell> destination,
        double progress,
        FadeShape curve)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);

        var eased = curve.Evaluate(Math.Clamp(progress, 0, 1));

        return
        [
            .. destination.Select(target =>
            {
                var before = origin.FirstOrDefault(cell =>
                    cell.Matches(target.LogicalChannelId, target.LineId, target.LineChannel));

                var from = Audible(before) ? before!.GainDb : GainRange.SilenceFloorDb;
                var to = Audible(target) ? target.GainDb : GainRange.SilenceFloorDb;

                return target with
                {
                    GainDb = from + ((to - from) * eased),
                    // Unmute at the START of a ramp up and mute at the END of a ramp down, so the
                    // interpolated gain is what is actually heard in both directions.
                    Muted = target.Muted && eased >= 1,
                };
            }),
        ];
    }

    /// <summary>Whether a cell contributes anything: present, unmuted, above the floor.</summary>
    private static bool Audible(PatchCell? cell) =>
        cell is { Muted: false } && cell.GainDb > GainRange.SilenceFloorDb;
}
