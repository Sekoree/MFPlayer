using Seko.OwnAudioSharp.Video.Decoders;

namespace Seko.OwnAudioSharp.Video.Sources;

public sealed class FFVideoSourceOptions
{
    // Prefetched frames in decoder thread queue.
    public int QueueCapacity { get; init; } = 6;

    // If enabled, creates a dedicated decode worker thread.
    public bool UseDedicatedDecodeThread { get; init; } = true;

    // Frames older than targetTime - LateDropThresholdSeconds are dropped.
    public double LateDropThresholdSeconds { get; init; } = 0.020;

    // If drift exceeds this threshold, source performs a hard seek to target time.
    public double HardSeekThresholdSeconds { get; init; } = 0.500;

    // Multiple consumers requesting nearly identical master timestamps reuse current frame.
    public double DuplicateRequestWindowSeconds { get; init; } = 0.002;

    // Enables gradual drift correction against master clock before hard-seek is needed.
    public bool EnableDriftCorrection { get; init; } = false;

    // No correction is applied when |drift| is below this dead-zone.
    public double DriftCorrectionDeadZoneSeconds { get; init; } = 0.004;

    // Proportional correction factor applied to drift (small values are smoother).
    public double DriftCorrectionRate { get; init; } = 0.05;

    // Maximum correction step applied per request to avoid oscillation.
    public double MaxCorrectionStepSeconds { get; init; } = 0.004;

    // Forwarded to FFVideoDecoder when source owns decoder.
    public FFVideoDecoderOptions DecoderOptions { get; init; } = new();
}

