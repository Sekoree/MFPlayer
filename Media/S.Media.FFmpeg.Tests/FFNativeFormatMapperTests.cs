using FFmpeg.AutoGen;
using S.Media.Core.Video;
using S.Media.FFmpeg.Runtime;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFNativeFormatMapperTests
{
    [Fact]
    public void MapPixelFormat_MapsKnownFormats()
    {
        Assert.Equal(VideoPixelFormat.Rgba32, FFNativeFormatMapper.MapPixelFormat((int)AVPixelFormat.AV_PIX_FMT_RGBA));
        Assert.Equal(VideoPixelFormat.Bgra32, FFNativeFormatMapper.MapPixelFormat((int)AVPixelFormat.AV_PIX_FMT_BGRA));
        Assert.Equal(VideoPixelFormat.Nv12, FFNativeFormatMapper.MapPixelFormat((int)AVPixelFormat.AV_PIX_FMT_NV12));
    }

    [Fact]
    public void MapPixelFormat_UnknownValue_ReturnsUnknown()
    {
        var mapped = FFNativeFormatMapper.MapPixelFormat(int.MaxValue);

        Assert.Equal(VideoPixelFormat.Unknown, mapped);
    }
}

