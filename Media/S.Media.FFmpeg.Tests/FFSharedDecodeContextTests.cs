using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Runtime;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFSharedDecodeContextTests
{
    [Fact]
    public void Open_IncrementsRefCount_OnSuccess()
    {
        var context = new FFSharedDecodeContext();
        var open = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var decode = new FFmpegDecodeOptions { DecodeThreadCount = int.MaxValue, MaxQueuedFrames = 0, MaxQueuedPackets = 0 };

        var code = context.Open(open, decode);

        Assert.Equal(MediaResult.Success, code);
        Assert.True(context.IsOpen);
        Assert.Equal(1, context.RefCount);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), context.ResolvedDecodeOptions.DecodeThreadCount);
        Assert.Equal(1, context.ResolvedDecodeOptions.MaxQueuedFrames);
        Assert.Equal(1, context.ResolvedDecodeOptions.MaxQueuedPackets);
    }

    [Fact]
    public void Open_AfterDispose_ReturnsDisposedCode()
    {
        var context = new FFSharedDecodeContext();
        context.Dispose();

        var code = context.Open(new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" }, new FFmpegDecodeOptions());

        Assert.Equal((int)MediaErrorCode.FFmpegSharedContextDisposed, code);
    }

    [Fact]
    public void Open_ReturnsInvalidConfig_ForNegativeDecodeThreadCount()
    {
        var context = new FFSharedDecodeContext();

        var code = context.Open(
            new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" },
            new FFmpegDecodeOptions { DecodeThreadCount = -1 });

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, code);
    }

    [Fact]
    public void ApplyResolvedStreamDescriptors_OverridesInitialDescriptors_WhenOpen()
    {
        var context = new FFSharedDecodeContext();
        var openCode = context.Open(new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" }, new FFmpegDecodeOptions());

        Assert.Equal(MediaResult.Success, openCode);

        context.ApplyResolvedStreamDescriptors(
            new FFStreamDescriptor { StreamIndex = 2, CodecName = "aac", SampleRate = 44_100 },
            new FFStreamDescriptor { StreamIndex = 1, CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 59.94d });

        Assert.Equal(2, context.AudioStream!.Value.StreamIndex);
        Assert.Equal("aac", context.AudioStream.Value.CodecName);
        Assert.Equal(44_100, context.AudioStream.Value.SampleRate);
        Assert.Equal(1, context.VideoStream!.Value.StreamIndex);
        Assert.Equal("h264", context.VideoStream.Value.CodecName);
        Assert.Equal(1920, context.VideoStream.Value.Width);
        Assert.Equal(1080, context.VideoStream.Value.Height);
    }
}

