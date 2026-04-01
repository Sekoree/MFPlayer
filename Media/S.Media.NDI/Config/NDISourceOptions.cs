using S.Media.Core.Errors;
using NDILib;

namespace S.Media.NDI.Config;

/// <summary>
/// Per-source configuration for NDI audio and video sources.
/// These settings are <b>per-source overrides</b> that take precedence over the engine-wide
/// <see cref="NDILimitsOptions"/> defaults passed to <c>NDIEngine.Initialize</c>.
/// </summary>
public sealed record NDISourceOptions
{
    /// <summary>
    /// Per-source override: queue behaviour when the jitter buffer is full.
    /// Takes precedence over <see cref="NDILimitsOptions.QueueOverflowPolicy"/>.
    /// </summary>
    public NDIQueueOverflowPolicy QueueOverflowPolicy { get; init; } = NDIQueueOverflowPolicy.DropOldest;

    /// <summary>
    /// Per-source override: video fallback when no new frame is available.
    /// Takes precedence over <see cref="NDILimitsOptions.VideoFallbackMode"/>.
    /// </summary>
    public NDIVideoFallbackMode VideoFallbackMode { get; init; } = NDIVideoFallbackMode.NoFrame;

    public TimeSpan DiagnosticsTickInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Per-source override: number of video frames held in the jitter buffer.
    /// Takes precedence over <see cref="NDILimitsOptions.VideoJitterBufferFrames"/>.
    /// </summary>
    public int VideoJitterBufferFrames { get; init; } = 4;

    /// <summary>
    /// Per-source override: audio jitter buffer depth in milliseconds.
    /// Takes precedence over <see cref="NDILimitsOptions.AudioJitterBufferMs"/>.
    /// </summary>
    public int AudioJitterBufferMs { get; init; } = 90;

    /// <summary>
    /// Receiver bandwidth selection for the NDI connection.
    /// Controls whether the receiver requests the highest quality stream, a lower-bandwidth
    /// proxy, audio-only, or metadata-only from the sender.
    /// <para>
    /// <b>Note:</b> This setting is applied at <see cref="NDILib.NDIReceiver"/> creation time
    /// via <see cref="NDILib.NDIReceiverSettings.Bandwidth"/>. When creating sources through
    /// <c>NDIEngine.CreateAudioSource</c>/<c>CreateVideoSource</c> with a pre-created receiver,
    /// this property is informational only — the bandwidth was already set on the receiver.
    /// </para>
    /// </summary>
    public NdiRecvBandwidth ReceiverBandwidth { get; init; } = NdiRecvBandwidth.Highest;

    /// <summary>
    /// Minimal buffering for the lowest possible latency. May drop frames on jittery networks.
    /// VideoJitterBufferFrames=1, AudioJitterBufferMs=20, DiagnosticsTickInterval=50ms.
    /// </summary>
    public static NDISourceOptions LowLatency => new()
    {
        VideoJitterBufferFrames = 1,
        AudioJitterBufferMs = 20,
        DiagnosticsTickInterval = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>
    /// Good trade-off between latency and resilience. Matches the default constructor values.
    /// VideoJitterBufferFrames=4, AudioJitterBufferMs=90, DiagnosticsTickInterval=100ms.
    /// </summary>
    public static NDISourceOptions Balanced => new();

    /// <summary>
    /// Deep buffers for unreliable or high-jitter networks. Adds latency but avoids drops.
    /// VideoJitterBufferFrames=6, AudioJitterBufferMs=150, DiagnosticsTickInterval=200ms.
    /// </summary>
    public static NDISourceOptions Safe => new()
    {
        VideoJitterBufferFrames = 6,
        AudioJitterBufferMs = 150,
        DiagnosticsTickInterval = TimeSpan.FromMilliseconds(200),
    };

    public int Validate()
    {
        if (!Enum.IsDefined(QueueOverflowPolicy))
        {
            return (int)MediaErrorCode.NDIInvalidQueueOverflowPolicyOverride;
        }

        if (!Enum.IsDefined(VideoFallbackMode))
        {
            return (int)MediaErrorCode.NDIInvalidVideoFallbackOverride;
        }

        if (DiagnosticsTickInterval < TimeSpan.Zero)
        {
            return (int)MediaErrorCode.NDIInvalidDiagnosticsTickOverride;
        }

        if (VideoJitterBufferFrames <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        if (AudioJitterBufferMs <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        return MediaResult.Success;
    }

    public NDISourceOptions Normalize()
    {
        var tick = DiagnosticsTickInterval;
        if (tick < TimeSpan.Zero)
        {
            tick = TimeSpan.Zero;
        }

        if (tick < TimeSpan.FromMilliseconds(16))
        {
            tick = TimeSpan.FromMilliseconds(16);
        }

        return this with
        {
            DiagnosticsTickInterval = tick,
            VideoJitterBufferFrames = Math.Max(1, VideoJitterBufferFrames),
            AudioJitterBufferMs = Math.Max(1, AudioJitterBufferMs),
        };
    }
}
