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

    [Fact]
    public void ResolvePreferredPixelFormat_UnknownNative_WithThreePlanes_ChoosesYuv420P()
    {
        var format = FFNativeFormatMapper.ResolvePreferredPixelFormat(
            nativePixelFormat: int.MaxValue,
            width: 4,
            height: 4,
            plane0: new byte[16],
            plane0Stride: 4,
            plane1: new byte[4],
            plane1Stride: 2,
            plane2: new byte[4],
            plane2Stride: 2);

        Assert.Equal(VideoPixelFormat.Yuv420P, format);
    }

    [Fact]
    public void ResolvePreferredPixelFormat_UnknownNative_WithNoPlanes_FallsBackToRgba32()
    {
        var format = FFNativeFormatMapper.ResolvePreferredPixelFormat(
            nativePixelFormat: int.MaxValue,
            width: 4,
            height: 4,
            plane0: default,
            plane0Stride: 0,
            plane1: default,
            plane1Stride: 0,
            plane2: default,
            plane2Stride: 0);

        Assert.Equal(VideoPixelFormat.Rgba32, format);
    }
}
