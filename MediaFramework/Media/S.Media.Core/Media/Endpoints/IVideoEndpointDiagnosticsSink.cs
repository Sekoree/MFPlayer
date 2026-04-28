namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// §heavy-media-fixes phase 4/6 — opt-in interface for video endpoints that
/// want to display upstream drop counters in their HUD / diagnostics overlay.
/// The host (typically <c>S.Media.Playback.MediaPlayer</c>) polls
/// <see cref="S.Media.Core.Video.IVideoChannel.SubscriptionDroppedFrames"/>
/// and any decoder-side drop counter and fans the values out via
/// <see cref="UpdateDropCounters"/> at a low cadence (e.g. on each drift
/// loop tick).
/// </summary>
public interface IVideoEndpointDiagnosticsSink
{
    /// <summary>
    /// Pushes the latest cumulative drop counters into the endpoint. Negative
    /// values mean "no update for this stage" so the host can push only the
    /// counter it actually has fresh data for.
    /// </summary>
    /// <param name="subscriptionDropped">
    /// Total frames evicted by the router video subscription's overflow
    /// policy across all active subscriptions on the channel feeding this
    /// endpoint. Pass a negative number to leave the previous value alone.
    /// </param>
    /// <param name="decoderDropped">
    /// Total frames the decoder skipped before <c>sws_scale</c> because they
    /// were past their deadline. Pass a negative number to leave the
    /// previous value alone.
    /// </param>
    void UpdateDropCounters(long subscriptionDropped, long decoderDropped);
}
