using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Conversion;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class YuvToRgbaConverterTests
{
    [Fact]
    public void Convert_Rgba32_CopiesInputPayload()
    {
        var converter = new YuvToRgbaConverter();
        var sourceBytes = new byte[]
        {
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120,
            130, 140, 150, 160,
        };
        using var frame = new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, sourceBytes, 8);

        var destination = new byte[16];
        var code = converter.Convert(frame, destination, out var bytesWritten);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(16, bytesWritten);
        Assert.Equal(sourceBytes, destination);
    }

    [Fact]
    public void Convert_Bgra32_SwizzlesToRgba()
    {
        var converter = new YuvToRgbaConverter();
        var bgra = new byte[]
        {
            1, 2, 3, 255,
            4, 5, 6, 255,
            7, 8, 9, 255,
            10, 11, 12, 255,
        };
        using var frame = new VideoFrame(2, 2, VideoPixelFormat.Bgra32, new Bgra32PixelFormatData(), TimeSpan.Zero, true, bgra, 8);

        var destination = new byte[16];
        var code = converter.Convert(frame, destination, out var bytesWritten);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(16, bytesWritten);
        Assert.Equal(3, destination[0]);
        Assert.Equal(2, destination[1]);
        Assert.Equal(1, destination[2]);
        Assert.Equal(255, destination[3]);
    }

    [Fact]
    public void Convert_Yuv420P_WritesRgbaPayload()
    {
        var converter = new YuvToRgbaConverter();
        using var frame = new VideoFrame(
            2,
            2,
            VideoPixelFormat.Yuv420P,
            new Yuv420PPixelFormatData(),
            TimeSpan.Zero,
            true,
            plane0: new byte[] { 16, 32, 48, 64 },
            plane0Stride: 2,
            plane1: new byte[] { 128 },
            plane1Stride: 1,
            plane2: new byte[] { 128 },
            plane2Stride: 1);

        var destination = new byte[16];
        var code = converter.Convert(frame, destination, out var bytesWritten);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(16, bytesWritten);
        Assert.Equal(255, destination[3]);
        Assert.Equal(255, destination[7]);
    }

    [Fact]
    public void Convert_ReturnsIncompatible_ForUnsupportedFormat()
    {
        var converter = new YuvToRgbaConverter();
        using var frame = new VideoFrame(2, 2, VideoPixelFormat.Yuv444P, new Yuv444PPixelFormatData(), TimeSpan.Zero, true, new byte[4], 2, new byte[4], 2, new byte[4], 2);

        var destination = new byte[16];
        var code = converter.Convert(frame, destination, out _);

        Assert.Equal((int)MediaErrorCode.OpenGLClonePixelFormatIncompatible, code);
    }
}

