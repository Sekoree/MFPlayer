using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegDecodeOptionsTests
{
    [Fact]
    public void Normalize_ClampsQueueLimits_ToAtLeastOne()
    {
        var options = new FFmpegDecodeOptions
        {
            MaxQueuedPackets = 0,
            MaxQueuedFrames = -3,
        };

        var normalized = options.Normalize();

        Assert.Equal(1, normalized.MaxQueuedPackets);
        Assert.Equal(1, normalized.MaxQueuedFrames);
    }

    [Fact]
    public void Normalize_ClampsDecodeThreadCount_ToLogicalCpuCount()
    {
        var options = new FFmpegDecodeOptions { DecodeThreadCount = int.MaxValue };

        var normalized = options.Normalize();

        Assert.Equal(Math.Max(1, Environment.ProcessorCount), normalized.DecodeThreadCount);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_ForNegativeDecodeThreadCount()
    {
        var options = new FFmpegDecodeOptions { DecodeThreadCount = -1 };

        var result = options.Validate();

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Normalize_PreservesZeroDecodeThreadCount_ForFfmpegAutoSelection()
    {
        var options = new FFmpegDecodeOptions { DecodeThreadCount = 0 };

        var normalized = options.Normalize();

        Assert.Equal(0, normalized.DecodeThreadCount);
    }
}
