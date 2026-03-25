using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Decoders.Internal;
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
        Assert.Equal(VideoPixelFormat.Unknown, converted.MappedPixelFormat);
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

    private static FFVideoConvertResult ConvertOrThrow(FFPixelConverter converter, FFVideoDecodeResult decoded)
    {
        var code = converter.Convert(decoded, out var converted);
        Assert.Equal(MediaResult.Success, code);
        return converted;
    }
}

