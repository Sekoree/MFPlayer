using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Core.Video;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// How a holding slate fills the canvas.
/// </summary>
/// <remarks>
/// A slate is rarely the canvas's own shape, and it used to be submitted at its own raster and
/// stretched — the one option that always looks wrong on a logo.
/// </remarks>
public sealed class IdleImageFitTests
{
    /// <summary>A solid BGRA still of the given size, in an unmistakable colour.</summary>
    private static VideoFrame Still(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = 0x20;      // B
            pixels[at + 1] = 0x40;  // G
            pixels[at + 2] = 0xE0;  // R
            pixels[at + 3] = 0xFF;  // A
        }

        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
            pixels,
            stride,
            new VideoFrameMetadata());
    }

    private static bool IsInk(VideoFrame frame, int x, int y)
    {
        var pixels = frame.Planes[0].Span;
        var at = (y * frame.Strides[0]) + (x * 4);

        // Anything that is not the black the canvas was built with.
        return pixels[at] != 0 || pixels[at + 1] != 0 || pixels[at + 2] != 0;
    }

    [Fact]
    public void TheFittedFrameIsAlwaysTheCanvasSize()
    {
        using var fitted = IdleFrames.Fitted(Still(800, 800), 1920, 1080, LayerFit.Contain);

        Assert.Equal(1920, fitted.Format.Width);
        Assert.Equal(1080, fitted.Format.Height);
    }

    [Fact]
    public void ContainLetterboxesASquareOnAWideCanvas()
    {
        using var fitted = IdleFrames.Fitted(Still(800, 800), 1920, 1080, LayerFit.Contain);

        // Scaled to the height, centred, with black bars left and right.
        Assert.False(IsInk(fitted, 10, 540), "the left bar should be black");
        Assert.False(IsInk(fitted, 1910, 540), "the right bar should be black");
        Assert.True(IsInk(fitted, 960, 540), "the middle should carry the picture");
        Assert.True(IsInk(fitted, 960, 5), "a contained square should reach the top edge");
    }

    [Fact]
    public void CoverFillsTheCanvasWithNoBars()
    {
        using var fitted = IdleFrames.Fitted(Still(800, 800), 1920, 1080, LayerFit.Cover);

        Assert.True(IsInk(fitted, 5, 5), "cover should leave no black corner");
        Assert.True(IsInk(fitted, 1914, 1074), "cover should leave no black corner");
    }

    [Fact]
    public void StretchFillsTheCanvasToo()
    {
        using var fitted = IdleFrames.Fitted(Still(800, 800), 1920, 1080, LayerFit.Stretch);

        Assert.True(IsInk(fitted, 5, 5));
        Assert.True(IsInk(fitted, 1914, 1074));
    }

    [Fact]
    public void CentreLeavesASmallStillAtItsOwnSize()
    {
        using var fitted = IdleFrames.Fitted(Still(400, 200), 1920, 1080, LayerFit.Center);

        // 400×200 centred on 1920×1080: ink from x 760 to 1160, y 440 to 640.
        Assert.True(IsInk(fitted, 960, 540), "the middle should carry the picture");
        Assert.False(IsInk(fitted, 700, 540), "a centred still must not be scaled up");
        Assert.False(IsInk(fitted, 960, 400), "a centred still must not be scaled up");
    }

    [Fact]
    public void ATransparentSlateShowsBlackRatherThanItsAlpha()
    {
        var stride = 4 * 4;
        var pixels = new byte[stride * 4];
        // Fully transparent white.
        for (var at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = 0xFF;
            pixels[at + 1] = 0xFF;
            pixels[at + 2] = 0xFF;
            pixels[at + 3] = 0x00;
        }

        using var source = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(25, 1)),
            pixels,
            stride,
            new VideoFrameMetadata());
        using var fitted = IdleFrames.Fitted(source, 16, 16, LayerFit.Stretch);

        Assert.False(IsInk(fitted, 8, 8), "a transparent slate must composite over black");
    }

    [Fact]
    public void AFrameItCannotReadIsHandedBackUntouched()
    {
        // Not BGRA: the caller submits it and the compositor does what it always did.
        using var source = new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(25, 1)),
            new byte[64 * 64 * 3 / 2],
            64,
            new VideoFrameMetadata());

        var fitted = IdleFrames.Fitted(source, 1920, 1080, LayerFit.Contain);

        Assert.Same(source, fitted);
    }

    [Fact]
    public void ACompositionDefaultsToSixtyFramesASecond()
    {
        // 30 halves the smoothness of every pan and fade a show contains, and juders on a 60 Hz panel.
        Assert.Equal(60, new CompositionDefinition().FramesPerSecond);
    }

    [Fact]
    public void IdleImagesDefaultToContain()
    {
        Assert.Equal(LayerFit.Contain, new CompositionDefinition().IdleImageFit);
        Assert.Equal(LayerFit.Contain, new VideoOutputDefinition().IdleFallbackFit);
    }
}
