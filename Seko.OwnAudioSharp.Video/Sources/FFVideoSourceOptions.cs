using Seko.OwnAudioSharp.Video.Decoders;

namespace Seko.OwnAudioSharp.Video.Sources;

/// <summary>Tuning options for <see cref="FFVideoSource"/>.</summary>
public sealed class FFVideoSourceOptions
{
    /// <summary>
    /// Maximum number of decoded frames held in the pre-fetch queue.
    /// Higher values smooth over decode spikes at the cost of memory. Default: <c>6</c>.
    /// </summary>
    public int QueueCapacity { get; init; } = 6;

    /// <summary>
    /// When <see langword="true"/> a dedicated background thread continuously decodes frames into
    /// the queue, keeping the render path free of decode latency. Default: <see langword="true"/>.
    /// </summary>
    public bool UseDedicatedDecodeThread { get; init; } = true;

    /// <summary>
    /// A pending frame whose PTS falls more than this many seconds behind the target time is
    /// considered late and eligible for dropping. The effective threshold is also bounded below
    /// by <see cref="LateDropFrameMultiplier"/> × frame duration. Default: <c>0.020</c> s.
    /// </summary>
    public double LateDropThresholdSeconds { get; init; } = 0.020;

    /// <summary>
    /// Minimum late-drop threshold expressed as a multiple of the stream's frame duration.
    /// Prevents over-aggressive dropping under transient jitter. Example: <c>1.5</c> at 60 fps
    /// yields an effective minimum of ~25 ms. Default: <c>1.5</c>.
    /// </summary>
    public double LateDropFrameMultiplier { get; init; } = 1.5;

    /// <summary>
    /// Maximum number of pending frames that may be dropped in a single <see cref="FFVideoSource.RequestNextFrame"/> call.
    /// Caps burst-drop events caused by transient jitter. Default: <c>1</c>.
    /// </summary>
    public int MaxDropsPerRequest { get; init; } = 1;

    /// <summary>
    /// When the absolute drift between the current frame PTS and the target time exceeds this
    /// threshold a hard seek is performed to recover. Default: <c>0.500</c> s.
    /// </summary>
    public double HardSeekThresholdSeconds { get; init; } = 0.500;

    /// <summary>
    /// Multiple consumers calling <see cref="FFVideoSource.TryGetFrameAtTime"/> within this window
    /// of the last served master timestamp receive the same frame without advancing the stream.
    /// Default: <c>0.002</c> s.
    /// </summary>
    public double DuplicateRequestWindowSeconds { get; init; } = 0.002;

    /// <summary>
    /// Enables gradual PTS offset correction to track the master clock without triggering hard seeks
    /// on small drifts. Default: <see langword="false"/>.
    /// </summary>
    public bool EnableDriftCorrection { get; init; } = false;

    /// <summary>
    /// Drift correction is suppressed when |drift| is below this value, preventing unnecessary
    /// micro-adjustments. Default: <c>0.004</c> s.
    /// </summary>
    public double DriftCorrectionDeadZoneSeconds { get; init; } = 0.004;

    /// <summary>
    /// Proportional gain applied to the drift measurement each frame. Smaller values produce
    /// smoother but slower correction. Default: <c>0.05</c>.
    /// </summary>
    public double DriftCorrectionRate { get; init; } = 0.05;

    /// <summary>
    /// Upper bound on the PTS offset change applied per request, preventing oscillation.
    /// Default: <c>0.004</c> s.
    /// </summary>
    public double MaxCorrectionStepSeconds { get; init; } = 0.004;

    /// <summary>Decoder options forwarded to <see cref="FFVideoDecoder"/> when the source owns the decoder.</summary>
    public FFVideoDecoderOptions DecoderOptions { get; init; } = new();
}
