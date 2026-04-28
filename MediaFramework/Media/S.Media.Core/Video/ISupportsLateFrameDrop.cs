namespace S.Media.Core.Video;

/// <summary>
/// §heavy-media-fixes phase 6 — opt-in interface for video channels that can
/// drop their own non-reference frames at decode time when they fall behind
/// realtime. Implementations expose two knobs:
/// <list type="bullet">
///   <item><description>
///     <see cref="LateFrameDropDeadline"/> — how far behind the consumer
///     reference (the larger of the channel's own <c>Position</c> and the
///     external clock hint) a non-key frame may be before it gets skipped
///     instead of decoded / converted / published. <see cref="System.TimeSpan.Zero"/>
///     disables the feature.
///   </description></item>
///   <item><description>
///     <see cref="SetExternalClockHint"/> — pushes the master clock's
///     current position into the channel so the late-drop logic can detect
///     "audio is way ahead" even when the channel's own <c>Position</c> is
///     still pinned at the renderer's last presented frame (which is the
///     normal case under pull-mode backpressure).
///   </description></item>
/// </list>
/// <para>
/// Plumbed by <c>S.Media.Playback.MediaPlayer</c>'s drift correction loop:
/// when the loop computes an A/V drift sample, it pushes the new clock
/// position and an updated deadline into the active video channel before
/// applying its own time-offset nudge. This keeps the decoder-side and
/// router-side correctors aligned without either knowing about the other's
/// internals.
/// </para>
/// </summary>
public interface ISupportsLateFrameDrop
{
    /// <summary>
    /// Maximum lateness (relative to the consumer reference) past which a
    /// non-reference frame is dropped at decode time. <see cref="System.TimeSpan.Zero"/>
    /// disables late-frame drop.
    /// </summary>
    TimeSpan LateFrameDropDeadline { get; set; }

    /// <summary>
    /// Pushes the master clock's current position into the channel. The
    /// channel uses <c>max(Position, hint)</c> as the reference point for
    /// the late-drop comparison, which is necessary to detect lateness in
    /// pull-mode setups where the channel's own <c>Position</c> never gets
    /// ahead of the decoder.
    /// </summary>
    void SetExternalClockHint(TimeSpan masterPosition);

    /// <summary>
    /// Running count of frames dropped by the late-frame drop logic. Used by
    /// the HUD to surface decoder-side losses.
    /// </summary>
    long DecoderDroppedFrames { get; }
}
