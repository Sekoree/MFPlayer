namespace Seko.OwnAudioNET.Video.NDI;

/// <summary>
/// Tunables for mapping NDI timestamps/timecode to playback timeline seconds.
/// </summary>
public sealed class NdiExternalTimelineClockOptions
{
    public const double DefaultFallbackFrameDurationSeconds = 1.0 / 30.0;
    public const double DefaultPipelineLatencySmoothingFactor = 0.10;
    public const double DefaultMinVideoAdvanceFrameRatio = 0.25;

    /// <summary>Fallback frame duration when stream metadata is missing/invalid.</summary>
    public double DefaultFrameDurationSeconds { get; init; } = DefaultFallbackFrameDurationSeconds;

    /// <summary>EMA blend factor for buffered-audio latency smoothing (0..1).</summary>
    public double PipelineLatencySmoothingFactor { get; init; } = DefaultPipelineLatencySmoothingFactor;

    /// <summary>Minimum per-frame forward progress ratio when timestamps jitter/regress.</summary>
    public double MinVideoAdvanceFrameRatio { get; init; } = DefaultMinVideoAdvanceFrameRatio;

    public NdiExternalTimelineClockOptions CloneNormalized()
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

        return new NdiExternalTimelineClockOptions
        {
            DefaultFrameDurationSeconds = defaultFrameDuration,
            PipelineLatencySmoothingFactor = smoothing,
            MinVideoAdvanceFrameRatio = minAdvanceRatio
        };
    }
}

