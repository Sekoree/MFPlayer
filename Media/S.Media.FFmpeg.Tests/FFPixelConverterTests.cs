using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Decoders.Internal;
using FFmpeg.AutoGen;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFPixelConverterTests
{
    [Fact]
    public void Convert_RequiresInitialize()
    {
        using var converter = new FFPixelConverter();

        var code = converter.Convert(new FFVideoDecodeResult(0, 0, TimeSpan.Zero, true, 2, 2), out _);

        Assert.Equal((int)MediaErrorCode.FFmpegPixelConversionFailed, code);
    }

    [Fact]
    public void Convert_PreservesDeterministicMetadata()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(3, 12, TimeSpan.FromSeconds(0.4), IsKeyFrame: false, Width: 2, Height: 2);
        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(3, converted.Generation);
        Assert.Equal(12, converted.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(0.4), converted.PresentationTime);
        Assert.False(converted.IsKeyFrame);
        Assert.Equal(2, converted.Width);
        Assert.Equal(2, converted.Height);
        Assert.Equal(VideoPixelFormat.Rgba32, converted.MappedPixelFormat);
    }

    [Fact]
    public void Convert_InvalidNativePixelFormat_FallsBackAndDisablesNativePath()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 1,
            FrameIndex: 9,
            PresentationTime: TimeSpan.FromSeconds(0.3),
            IsKeyFrame: true,
            Width: 2,
            Height: 2,
            NativePixelFormat: int.MaxValue);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(9, converted.FrameIndex);
        Assert.Equal(int.MaxValue, converted.NativePixelFormat);
        Assert.False(converter.IsNativeConvertEnabled);
    }

    [Fact]
    public void Convert_ResultMetadata_IsSnapshotAndUnaffectedBySourceReassignment()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 4,
            FrameIndex: 3,
            PresentationTime: TimeSpan.FromSeconds(0.1),
            IsKeyFrame: false,
            Width: 640,
            Height: 360,
            NativePixelFormat: 26);

        var first = ConvertOrThrow(converter, decoded);
        Assert.Equal(3, first.FrameIndex);

        var mutated = decoded with
        {
            Width = 1920,
            Height = 1080,
            FrameIndex = 33,
            NativePixelFormat = int.MaxValue,
        };

        Assert.Equal(1920, mutated.Width);
        Assert.Equal(1080, mutated.Height);

        Assert.Equal(640, first.Width);
        Assert.Equal(360, first.Height);
        Assert.Null(first.NativeTimeBaseNumerator);
        Assert.Null(first.NativeTimeBaseDenominator);
        Assert.Null(first.NativeFrameRateNumerator);
        Assert.Null(first.NativeFrameRateDenominator);
    }

    [Fact]
    public void Convert_Fallback_PreservesSecondaryPlanePayloads()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 1,
            FrameIndex: 2,
            PresentationTime: TimeSpan.FromSeconds(0.2),
            IsKeyFrame: true,
            Width: 4,
            Height: 4,
            Plane0: new byte[16],
            Plane0Stride: 4,
            Plane1: new byte[4],
            Plane1Stride: 2,
            Plane2: new byte[4],
            Plane2Stride: 2);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(4, converted.Plane1.Length);
        Assert.Equal(2, converted.Plane1Stride);
        Assert.Equal(4, converted.Plane2.Length);
        Assert.Equal(2, converted.Plane2Stride);
    }

    [Fact]
    public void Convert_PlaneAwarePolicy_PreservesMappedYuv420PayloadWithoutRgbaRewrite()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var plane0 = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var plane1 = Enumerable.Range(100, 4).Select(i => (byte)i).ToArray();
        var plane2 = Enumerable.Range(200, 4).Select(i => (byte)i).ToArray();

        var decoded = new FFVideoDecodeResult(
            Generation: 8,
            FrameIndex: 4,
            PresentationTime: TimeSpan.FromSeconds(0.5),
            IsKeyFrame: true,
            Width: 4,
            Height: 4,
            Plane0: plane0,
            Plane0Stride: 4,
            Plane1: plane1,
            Plane1Stride: 2,
            Plane2: plane2,
            Plane2Stride: 2,
            NativePixelFormat: (int)AVPixelFormat.AV_PIX_FMT_YUV420P);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(VideoPixelFormat.Yuv420P, converted.MappedPixelFormat);
        Assert.Equal(16, converted.Plane0.Length);
        Assert.Equal(4, converted.Plane1.Length);
        Assert.Equal(4, converted.Plane2.Length);
        Assert.True(converted.Plane0.Span.SequenceEqual(plane0));
        Assert.True(converted.Plane1.Span.SequenceEqual(plane1));
        Assert.True(converted.Plane2.Span.SequenceEqual(plane2));
        Assert.True(converter.IsNativeConvertEnabled);
    }

    [Fact]
    public void Convert_PlaneAwarePolicy_PreservesNv12WhenUvPlaneIsPresent()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var plane0 = Enumerable.Range(0, 16).Select(i => (byte)(255 - i)).ToArray();
        var plane1 = Enumerable.Range(0, 8).Select(i => (byte)(i * 3)).ToArray();

        var decoded = new FFVideoDecodeResult(
            Generation: 9,
            FrameIndex: 5,
            PresentationTime: TimeSpan.FromSeconds(0.6),
            IsKeyFrame: false,
            Width: 4,
            Height: 4,
            Plane0: plane0,
            Plane0Stride: 4,
            Plane1: plane1,
            Plane1Stride: 4,
            Plane2: default,
            Plane2Stride: 0,
            NativePixelFormat: (int)AVPixelFormat.AV_PIX_FMT_NV12);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(VideoPixelFormat.Nv12, converted.MappedPixelFormat);
        Assert.Equal(16, converted.Plane0.Length);
        Assert.Equal(8, converted.Plane1.Length);
        Assert.Equal(0, converted.Plane2.Length);
        Assert.True(converted.Plane0.Span.SequenceEqual(plane0));
        Assert.True(converted.Plane1.Span.SequenceEqual(plane1));
        Assert.Equal(4, converted.Plane1Stride);
    }

    [Fact]
    public void Convert_PlaneAwarePolicy_IncompleteYuv420Payload_NormalizesToRgbaFallback()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 10,
            FrameIndex: 6,
            PresentationTime: TimeSpan.FromSeconds(0.7),
            IsKeyFrame: true,
            Width: 4,
            Height: 4,
            Plane0: new byte[16],
            Plane0Stride: 4,
            Plane1: new byte[4],
            Plane1Stride: 2,
            Plane2: default,
            Plane2Stride: 0,
            NativePixelFormat: (int)AVPixelFormat.AV_PIX_FMT_YUV420P);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(VideoPixelFormat.Rgba32, converted.MappedPixelFormat);
        Assert.True(converted.Plane0Stride >= 16);
        Assert.True(converted.Plane0.Length >= 64);
        Assert.Equal(0, converted.Plane1.Length);
        Assert.Equal(0, converted.Plane2.Length);
    }

    [Fact]
    public void Convert_PlaneAwarePolicy_IncompleteNv12Payload_NormalizesToRgbaFallback()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 11,
            FrameIndex: 7,
            PresentationTime: TimeSpan.FromSeconds(0.8),
            IsKeyFrame: true,
            Width: 4,
            Height: 4,
            Plane0: new byte[16],
            Plane0Stride: 4,
            Plane1: default,
            Plane1Stride: 0,
            Plane2: default,
            Plane2Stride: 0,
            NativePixelFormat: (int)AVPixelFormat.AV_PIX_FMT_NV12);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(VideoPixelFormat.Rgba32, converted.MappedPixelFormat);
        Assert.True(converted.Plane0Stride >= 16);
        Assert.True(converted.Plane0.Length >= 64);
        Assert.Equal(0, converted.Plane1.Length);
        Assert.Equal(0, converted.Plane2.Length);
    }

    [Fact]
    public void Convert_PlaneAwarePolicy_IncompleteP010Payload_NormalizesToRgbaFallback()
    {
        using var converter = new FFPixelConverter();
        Assert.Equal(MediaResult.Success, converter.Initialize());

        var decoded = new FFVideoDecodeResult(
            Generation: 12,
            FrameIndex: 8,
            PresentationTime: TimeSpan.FromSeconds(0.9),
            IsKeyFrame: true,
            Width: 4,
            Height: 4,
            Plane0: new byte[32],
            Plane0Stride: 8,
            Plane1: default,
            Plane1Stride: 0,
            Plane2: default,
            Plane2Stride: 0,
            NativePixelFormat: (int)AVPixelFormat.AV_PIX_FMT_P010LE);

        var code = converter.Convert(decoded, out var converted);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(VideoPixelFormat.Rgba32, converted.MappedPixelFormat);
        Assert.True(converted.Plane0Stride >= 16);
        Assert.True(converted.Plane0.Length >= 64);
        Assert.Equal(0, converted.Plane1.Length);
        Assert.Equal(0, converted.Plane2.Length);
    }

    private static FFVideoConvertResult ConvertOrThrow(FFPixelConverter converter, FFVideoDecodeResult decoded)
    {
        var code = converter.Convert(decoded, out var converted);
        Assert.Equal(MediaResult.Success, code);
        return converted;
    }
}
