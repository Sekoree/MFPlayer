using NDILib;
using S.Media.Core.Video;
using S.Media.NDI.Clock;

namespace S.Media.NDI;

/// <summary>Options for <see cref="NDISource.Open"/>.</summary>
public sealed class NDISourceOptions
{
    public static NDISourceOptions Default { get; } = new();

    public bool ReceiveAudio { get; init; } = true;

    public bool ReceiveVideo { get; init; } = true;

    public string? ReceiverName { get; init; }

    public TimeSpan AudioRingCapacityDuration { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan? AudioMinBufferedDuration { get; init; }

    public int MaxQueuedVideoFrames { get; init; } = 8;

    public NDIRecvBandwidth Bandwidth { get; init; } = NDIRecvBandwidth.Highest;

    public NDIRecvColorFormat ColorFormat { get; init; } = NDIRecvColorFormat.BgrxBgra;

    public NDIIngestPlaybackClock? IngestClock { get; init; }

    /// <summary>
    /// Opt-in (default <see langword="false"/>): advertise this receiver's <see cref="NDISource.IngestClock"/>
    /// through <see cref="S.Media.Routing.IIngestPacedSource"/> so a player slaves its
    /// <see cref="S.Media.Routing.AudioRouter"/> to the sender's ingest timeline instead of the local wall
    /// clock (genlock to ingest). Left off, the receiver behaves exactly as before - the ingest clock is still
    /// maintained, just not used for pacing.
    /// </summary>
    /// <remarks>
    /// Only meaningful with <see cref="ReceiveAudio"/>: the ingest clock is driven by captured audio frames,
    /// so a video-only or silent sender never advances it and a slaved router would produce nothing. The same
    /// applies while the sender is disconnected - production halts (no silence is emitted) until ingest
    /// resumes, which is the intended genlock behaviour but is why this is not the default.
    /// </remarks>
    public bool PaceRouterFromIngestClock { get; init; }

    /// <summary>
    /// Present received video at the sender's <strong>absolute</strong> egress timecode instead of the default
    /// first-frame-relative timeline. For multi-receiver wall sync: every receiver of one sender resolves a
    /// frame to the same time, so - driven by a shared/synced reference clock - they present in lock-step.
    /// Default <c>false</c> (smooth single-receiver playback rebased to play start). Cross-receiver alignment
    /// also requires the receivers' clocks to share a reference (PTP / genlock); the framework supplies the
    /// absolute timeline, the time reference is a deployment concern. See <c>Doc/HaPlay-MultiOutput-Sync.md</c>.
    /// </summary>
    public bool PresentVideoByAbsoluteTimecode { get; init; }

    /// <summary>
    /// Overrides the color range stamped on received video frames. NDI carries no range metadata, so
    /// frames default to full-range BT.709 - right for OBS/NDI-HX senders (the field-verified case),
    /// washed out for a limited-range hardware sender. Set <see cref="VideoColorRange.Limited"/> for
    /// such a sender; <see langword="null"/> keeps the default.
    /// </summary>
    public VideoColorRange? ColorRangeOverride { get; init; }
}
