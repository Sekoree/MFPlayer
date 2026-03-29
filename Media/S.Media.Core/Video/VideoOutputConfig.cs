using S.Media.Core.Errors;

namespace S.Media.Core.Video;

public sealed record VideoOutputConfig
{
    public VideoOutputBackpressureMode BackpressureMode { get; init; } = VideoOutputBackpressureMode.DropOldest;

    public int QueueCapacity { get; init; } = 2;

    public double BackpressureWaitFrameMultiplier { get; init; } = 1.0;

    public TimeSpan? BackpressureTimeout { get; init; }

    /// <summary>
    /// Presentation scheduling policy applied by outputs that support timed delivery.
    /// Default: <see cref="VideoOutputPresentationMode.Unlimited"/>.
    /// </summary>
    public VideoOutputPresentationMode PresentationMode { get; init; } = VideoOutputPresentationMode.Unlimited;

    /// <summary>
    /// Timestamp monotonic normalization strategy used when <see cref="PresentationMode"/> is timestamp-driven.
    /// </summary>
    public VideoTimestampMode TimestampMode { get; init; } = VideoTimestampMode.RebaseOnDiscontinuity;

    /// <summary>
    /// Discontinuity threshold used by <see cref="VideoTimestampMode.RebaseOnDiscontinuity"/>.
    /// </summary>
    public TimeSpan TimestampDiscontinuityThreshold { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Optional stale-frame threshold. When set, outputs may drop frames that are too far behind schedule.
    /// </summary>
    public TimeSpan? StaleFrameDropThreshold { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Optional frame-rate cap used by <see cref="VideoOutputPresentationMode.MaxFps"/>.
    /// </summary>
    public double? MaxFps { get; init; }

    /// <summary>
    /// Maximum blocking time per scheduling wait. Default: 33ms.
    /// </summary>
    public TimeSpan MaxSchedulingWait { get; init; } = TimeSpan.FromMilliseconds(33);

    public int Validate(bool hasEffectiveFrameDuration)
    {
        if (QueueCapacity < 1)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (BackpressureWaitFrameMultiplier <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (BackpressureMode == VideoOutputBackpressureMode.Wait &&
            !BackpressureTimeout.HasValue &&
            !hasEffectiveFrameDuration)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (PresentationMode == VideoOutputPresentationMode.MaxFps)
        {
            if (!MaxFps.HasValue || !double.IsFinite(MaxFps.Value) || MaxFps.Value <= 0)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }
        }

        if (TimestampDiscontinuityThreshold < TimeSpan.Zero || MaxSchedulingWait < TimeSpan.Zero)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (StaleFrameDropThreshold.HasValue && StaleFrameDropThreshold.Value < TimeSpan.Zero)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return MediaResult.Success;
    }
}
