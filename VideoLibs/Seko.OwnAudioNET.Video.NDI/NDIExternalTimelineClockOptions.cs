namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Tunables for mapping NDI timestamps/timecode to playback timeline seconds.
/// </summary>
public sealed class NDIExternalTimelineClockOptions
{
    public const double DefaultFallbackFrameDurationSeconds = 1.0 / 30.0;
    public const double DefaultPipelineLatencySmoothingFactor = 0.10;
    public const double DefaultMinVideoAdvanceFrameRatio = 0.25;
    public const double DefaultMaxLatencyCompensationSeconds = 0.25;
    public const double DefaultMaxTimestampJumpSeconds = 0.50;

    /// <summary>Fallback frame duration when stream metadata is missing/invalid.</summary>
    public double DefaultFrameDurationSeconds { get; init; } = DefaultFallbackFrameDurationSeconds;

    /// <summary>EMA blend factor for buffered-audio latency smoothing (0..1).</summary>
    public double PipelineLatencySmoothingFactor { get; init; } = DefaultPipelineLatencySmoothingFactor;

    /// <summary>Minimum per-frame forward progress ratio when timestamps jitter/regress.</summary>
    public double MinVideoAdvanceFrameRatio { get; init; } = DefaultMinVideoAdvanceFrameRatio;

    /// <summary>Upper bound for buffered-audio compensation added to video PTS.</summary>
    public double MaxLatencyCompensationSeconds { get; init; } = DefaultMaxLatencyCompensationSeconds;

    /// <summary>Maximum accepted one-step source timestamp jump before fallback progression.</summary>
    public double MaxTimestampJumpSeconds { get; init; } = DefaultMaxTimestampJumpSeconds;

    public NDIExternalTimelineClockOptions CloneNormalized()
    {
        var defaultFrameDuration = DefaultFrameDurationSeconds;
        if (double.IsNaN(defaultFrameDuration) || double.IsInfinity(defaultFrameDuration) || defaultFrameDuration <= 0)
            defaultFrameDuration = DefaultFallbackFrameDurationSeconds;

        var smoothing = PipelineLatencySmoothingFactor;
        if (double.IsNaN(smoothing) || double.IsInfinity(smoothing))
            smoothing = DefaultPipelineLatencySmoothingFactor;

        smoothing = Math.Clamp(smoothing, 0.0, 1.0);

        var minAdvanceRatio = MinVideoAdvanceFrameRatio;
        if (double.IsNaN(minAdvanceRatio) || double.IsInfinity(minAdvanceRatio))
            minAdvanceRatio = DefaultMinVideoAdvanceFrameRatio;

        minAdvanceRatio = Math.Max(0.0, minAdvanceRatio);

        var maxLatencyCompensation = MaxLatencyCompensationSeconds;
        if (double.IsNaN(maxLatencyCompensation) || double.IsInfinity(maxLatencyCompensation))
            maxLatencyCompensation = DefaultMaxLatencyCompensationSeconds;

        maxLatencyCompensation = Math.Max(0.0, maxLatencyCompensation);

        var maxTimestampJump = MaxTimestampJumpSeconds;
        if (double.IsNaN(maxTimestampJump) || double.IsInfinity(maxTimestampJump))
            maxTimestampJump = DefaultMaxTimestampJumpSeconds;

        maxTimestampJump = Math.Max(0.0, maxTimestampJump);

        return new NDIExternalTimelineClockOptions
        {
            DefaultFrameDurationSeconds = defaultFrameDuration,
            PipelineLatencySmoothingFactor = smoothing,
            MinVideoAdvanceFrameRatio = minAdvanceRatio,
            MaxLatencyCompensationSeconds = maxLatencyCompensation,
            MaxTimestampJumpSeconds = maxTimestampJump
        };
    }
}

