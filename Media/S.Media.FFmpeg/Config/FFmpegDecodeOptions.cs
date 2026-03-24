using S.Media.Core.Errors;

namespace S.Media.FFmpeg.Config;

public sealed record FFmpegDecodeOptions
{
    public bool EnableHardwareDecode { get; init; }

    public bool LowLatencyMode { get; init; }

    public int DecodeThreadCount { get; init; }

    public bool UseDedicatedDecodeThread { get; init; } = true;

    public int MaxQueuedPackets { get; init; } = 1;

    public int MaxQueuedFrames { get; init; } = 1;

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

