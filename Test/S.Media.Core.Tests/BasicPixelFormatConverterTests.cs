using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class BasicPixelFormatConverterTests
{
    private static void WithLibYuvDisabled(Action action)
    {
        bool old = BasicPixelFormatConverter.LibYuvEnabled;
        BasicPixelFormatConverter.LibYuvEnabled = false;
        try
        {
            action();
        }
        finally
        {
            BasicPixelFormatConverter.LibYuvEnabled = old;
        }
    }

    [Fact]
    public void Convert_BgraToRgba_SwapsChannels()
    {
        using var converter = new BasicPixelFormatConverter();
        var source = new VideoFrame(
            1,
            1,
            PixelFormat.Bgra32,
            new byte[] { 10, 20, 30, 255 },
            TimeSpan.Zero);

        var converted = converter.Convert(source, PixelFormat.Rgba32);
        var s = converted.Data.Span;

        Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
        Assert.Equal(30, s[0]);
        Assert.Equal(20, s[1]);
        Assert.Equal(10, s[2]);
        Assert.Equal(255, s[3]);
    }

    [Fact]
    public void Convert_RgbaToBgra_SwapsChannels()
    {
        using var converter = new BasicPixelFormatConverter();
        var source = new VideoFrame(
            1,
            1,
            PixelFormat.Rgba32,
            new byte[] { 30, 20, 10, 255 },
            TimeSpan.Zero);

        var converted = converter.Convert(source, PixelFormat.Bgra32);
        var s = converted.Data.Span;

        Assert.Equal(PixelFormat.Bgra32, converted.PixelFormat);
        Assert.Equal(10, s[0]);
        Assert.Equal(20, s[1]);
        Assert.Equal(30, s[2]);
        Assert.Equal(255, s[3]);
    }

    [Fact]
    public void Convert_BgraToRgba_WorksWithLibYuvDisabled()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            var source = new VideoFrame(
                1,
                1,
                PixelFormat.Bgra32,
                new byte[] { 10, 20, 30, 255 },
                TimeSpan.Zero);

            var converted = converter.Convert(source, PixelFormat.Rgba32);
            var s = converted.Data.Span;

            Assert.Equal(30, s[0]);
            Assert.Equal(20, s[1]);
            Assert.Equal(10, s[2]);
            Assert.Equal(255, s[3]);
        });
    }

    [Fact]
    public void Convert_Nv12ToRgba_WithLibYuvDisabled_ReturnsBlackFrame()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            var source = new VideoFrame(
                2,
                2,
                PixelFormat.Nv12,
                new byte[] { 10, 20, 30, 40, 128, 64 },
                TimeSpan.FromMilliseconds(33));

            var converted = converter.Convert(source, PixelFormat.Rgba32);
            var s = converted.Data.Span;

            Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
            Assert.Equal(source.Width, converted.Width);
            Assert.Equal(source.Height, converted.Height);
            Assert.Equal(source.Pts, converted.Pts);
            Assert.All(s.ToArray(), b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Convert_Yuv420pToBgra_WithLibYuvDisabled_ReturnsBlackFrame()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            var source = new VideoFrame(
                2,
                2,
                PixelFormat.Yuv420p,
                new byte[] { 10, 20, 30, 40, 128, 64 },
                TimeSpan.FromMilliseconds(50));

            var converted = converter.Convert(source, PixelFormat.Bgra32);
            var s = converted.Data.Span;

            Assert.Equal(PixelFormat.Bgra32, converted.PixelFormat);
            Assert.Equal(source.Width, converted.Width);
            Assert.Equal(source.Height, converted.Height);
            Assert.Equal(source.Pts, converted.Pts);
            Assert.All(s.ToArray(), b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Convert_UyvyToRgba_WithLibYuvDisabled_ReturnsBlackFrame()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            var source = new VideoFrame(
                2,
                2,
                PixelFormat.Uyvy422,
                new byte[] { 128, 16, 128, 235, 128, 16, 128, 235 },
                TimeSpan.FromMilliseconds(75));

            var converted = converter.Convert(source, PixelFormat.Rgba32);
            var s = converted.Data.Span;

            Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
            Assert.Equal(source.Width, converted.Width);
            Assert.Equal(source.Height, converted.Height);
            Assert.Equal(source.Pts, converted.Pts);
            Assert.All(s.ToArray(), b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Convert_Yuv422p10ToRgba_WithLibYuvDisabled_ReturnsBlackFrame()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            // 2×2 Yuv422p10 layout:
            //   Y plane  = w*2*h = 2*2*2 = 8 bytes
            //   U plane  = (w/2)*2*h = 1*2*2 = 4 bytes
            //   V plane  = same = 4 bytes  → total 16 bytes
            var source = new VideoFrame(
                2,
                2,
                PixelFormat.Yuv422p10,
                new byte[16],
                TimeSpan.FromMilliseconds(100));

            var converted = converter.Convert(source, PixelFormat.Rgba32);
            var s = converted.Data.Span;

            Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
            Assert.Equal(2, converted.Width);
            Assert.Equal(2, converted.Height);
            Assert.Equal(source.Pts, converted.Pts);
            Assert.Equal(2 * 2 * 4, converted.Data.Length); // w*h*4
            Assert.All(s.ToArray(), b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Convert_Yuv422p10ToBgra_WithLibYuvDisabled_ReturnsBlackFrame()
    {
        WithLibYuvDisabled(() =>
        {
            using var converter = new BasicPixelFormatConverter();
            var source = new VideoFrame(
                2,
                2,
                PixelFormat.Yuv422p10,
                new byte[16],
                TimeSpan.FromMilliseconds(200));

            var converted = converter.Convert(source, PixelFormat.Bgra32);

            Assert.Equal(PixelFormat.Bgra32, converted.PixelFormat);
            Assert.Equal(2 * 2 * 4, converted.Data.Length);
            Assert.All(converted.Data.Span.ToArray(), b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Convert_SameFormat_ReturnsSourceFrame()
    {
        using var converter = new BasicPixelFormatConverter();
        var source = new VideoFrame(
            2,
            1,
            PixelFormat.Rgba32,
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            TimeSpan.FromMilliseconds(12));

        var converted = converter.Convert(source, PixelFormat.Rgba32);

        Assert.Equal(source.PixelFormat, converted.PixelFormat);
        Assert.Equal(source.Width, converted.Width);
        Assert.Equal(source.Height, converted.Height);
        Assert.Equal(source.Pts, converted.Pts);
        Assert.Equal(source.Data.Span.ToArray(), converted.Data.Span.ToArray());
    }

    [Fact]
    public void Convert_UnsupportedDestination_ThrowsNotSupportedException()
    {
        using var converter = new BasicPixelFormatConverter();
        var source = new VideoFrame(
            1,
            1,
            PixelFormat.Bgra32,
            new byte[] { 10, 20, 30, 255 },
            TimeSpan.Zero);

        Assert.Throws<NotSupportedException>(() => converter.Convert(source, PixelFormat.Nv12));
    }

    [Fact]
    public void Convert_AfterDispose_ThrowsObjectDisposedException()
    {
        var converter = new BasicPixelFormatConverter();
        converter.Dispose();

        var source = new VideoFrame(
            1,
            1,
            PixelFormat.Bgra32,
            new byte[] { 10, 20, 30, 255 },
            TimeSpan.Zero);

        Assert.Throws<ObjectDisposedException>(() => converter.Convert(source, PixelFormat.Rgba32));
    }
}

