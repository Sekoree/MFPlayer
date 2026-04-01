using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Config;

public sealed record FFmpegDecodeOptions
{
    /// <summary>
    /// When <see langword="true"/>, attempts hardware-accelerated decode (VAAPI on Linux,
    /// DXVA2/D3D11VA on Windows, VideoToolbox on macOS) with automatic software fallback.
    /// </summary>
    public bool EnableHardwareDecode { get; init; }

    /// <summary>
    /// When <see langword="true"/>, enables low-latency decode flags
    /// (e.g. <c>AV_CODEC_FLAG_LOW_DELAY</c>, disabling B-frame reorder buffers).
    /// Useful for live/real-time streams.
    /// </summary>
    public bool LowLatencyMode { get; init; }

    /// <summary>
    /// Number of libavcodec decode threads. <c>0</c> = auto (let FFmpeg decide).
    /// Validated (negative values rejected) and normalised (clamped to CPU count).
    /// Passed to <c>AVCodecContext.thread_count</c>.
    /// </summary>
    public int DecodeThreadCount { get; init; }

    /// <summary>
    /// When <see langword="true"/>, demux and decode run on separate OS threads,
    /// connected by a bounded packet queue (<see cref="MaxQueuedPackets"/>).
    /// </summary>
    public bool UseDedicatedDecodeThread { get; init; } = true;

    /// <summary>
    /// Maximum number of demuxed packets buffered between demux and decode.
    /// Higher values improve throughput at the cost of memory.
    /// Clamped to a minimum of 1 during <see cref="Normalize"/>.
    /// </summary>
    public int MaxQueuedPackets { get; init; } = 16;

    /// <summary>
    /// Maximum number of decoded frames buffered before consumption.
    /// Higher values smooth out decode jitter; lower values reduce latency.
    /// Clamped to a minimum of 1 during <see cref="Normalize"/>.
    /// </summary>
    public int MaxQueuedFrames { get; init; } = 8;

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
