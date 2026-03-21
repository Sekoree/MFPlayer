namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Tunables for NDI receive audio capture buffering and pacing.
/// </summary>
public sealed class NDIAudioStreamSourceOptions
{
    public const int DefaultRingCapacityMultiplier = 8;
    public const double DefaultCaptureHighWatermarkRatio = 0.40;
    public const int DefaultCaptureSleepMilliseconds = 2;
    public const int DefaultMinimumCaptureFrames = 64;
    public const int DefaultCaptureFrameTargetDivisor = 2;

    /// <summary>Ring capacity multiplier relative to one mixer callback block.</summary>
    public int RingCapacityMultiplier { get; init; } = DefaultRingCapacityMultiplier;

    /// <summary>Capture thread pauses when ring fill exceeds this ratio (0..1).</summary>
    public double CaptureHighWatermarkRatio { get; init; } = DefaultCaptureHighWatermarkRatio;

    /// <summary>Thread sleep in milliseconds while above high-water mark.</summary>
    public int CaptureSleepMilliseconds { get; init; } = DefaultCaptureSleepMilliseconds;

    /// <summary>Lower bound for per-capture request frame count.</summary>
    public int MinimumCaptureFrames { get; init; } = DefaultMinimumCaptureFrames;

    /// <summary>Target capture request size as BufferSize / divisor.</summary>
    public int CaptureFrameTargetDivisor { get; init; } = DefaultCaptureFrameTargetDivisor;

    public NDIAudioStreamSourceOptions CloneNormalized()
    {
        var ringMultiplier = RingCapacityMultiplier > 0 ? RingCapacityMultiplier : DefaultRingCapacityMultiplier;

        var highWatermark = CaptureHighWatermarkRatio;
        if (double.IsNaN(highWatermark) || double.IsInfinity(highWatermark))
            highWatermark = DefaultCaptureHighWatermarkRatio;
        highWatermark = Math.Clamp(highWatermark, 0.0, 1.0);

        var sleepMs = Math.Max(0, CaptureSleepMilliseconds);
        var minimumCaptureFrames = Math.Max(1, MinimumCaptureFrames);
        var targetDivisor = Math.Max(1, CaptureFrameTargetDivisor);

        return new NDIAudioStreamSourceOptions
        {
            RingCapacityMultiplier = ringMultiplier,
            CaptureHighWatermarkRatio = highWatermark,
            CaptureSleepMilliseconds = sleepMs,
            MinimumCaptureFrames = minimumCaptureFrames,
            CaptureFrameTargetDivisor = targetDivisor
        };
    }
}

