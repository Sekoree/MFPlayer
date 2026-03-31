using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Config;

public sealed record FFmpegDecodeOptions
{
    /// <summary>
    /// Reserved for future hardware-accelerated decode support (VAAPI, DXVA2, VideoToolbox).
    /// <b>Not yet implemented</b> — setting this has no effect on the current decode pipeline.
    /// </summary>
    public bool EnableHardwareDecode { get; init; }

    /// <summary>
    /// Reserved for low-latency / live-stream mode (e.g. disabling B-frame reorder buffers).
    /// <b>Not yet implemented</b> — setting this has no effect on the current decode pipeline.
    /// </summary>
    public bool LowLatencyMode { get; init; }

    /// <summary>
    /// Reserved for configuring the number of libavcodec decode threads.
    /// Currently validated (negative values are rejected) and normalised (clamped to CPU count),
    /// but the clamped value is not yet passed to the native codec context.
    /// </summary>
    public int DecodeThreadCount { get; init; }

    /// <summary>
    /// Reserved for splitting demux and decode onto separate OS threads.
    /// <b>Not yet implemented</b> — setting this has no effect on the current decode pipeline.
    /// </summary>
    public bool UseDedicatedDecodeThread { get; init; } = true;

    public int MaxQueuedPackets { get; init; } = 4;

    public int MaxQueuedFrames { get; init; } = 4;

    /// <summary>
    /// Preferred output pixel format for the software pixel converter fallback (sws_scale).
    /// Only packed single-plane formats (<see cref="VideoPixelFormat.Rgba32"/>,
    /// <see cref="VideoPixelFormat.Bgra32"/>) are supported.
    /// <see langword="null"/> (default) falls back to <see cref="VideoPixelFormat.Rgba32"/>.
    /// Native multi-plane formats (NV12, YUV420P, etc.) are always passed through unchanged
    /// and are unaffected by this setting.
    /// </summary>
    public VideoPixelFormat? PreferredOutputPixelFormat { get; init; }

    public int Validate()
    {
        return DecodeThreadCount < 0 ? (int)MediaErrorCode.FFmpegInvalidConfig : MediaResult.Success;
    }

    public FFmpegDecodeOptions Normalize()
    {
        var maxThreads = Math.Max(1, Environment.ProcessorCount);
        var threadCount = DecodeThreadCount;

        if (threadCount > maxThreads)
        {
            threadCount = maxThreads;
        }

        return this with
        {
            DecodeThreadCount = threadCount,
            MaxQueuedPackets = Math.Max(1, MaxQueuedPackets),
            MaxQueuedFrames = Math.Max(1, MaxQueuedFrames),
        };
    }
}
