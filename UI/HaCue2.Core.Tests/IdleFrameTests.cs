using HaCue2.Engine;
using S.Media.Core.Video;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// What an output shows when its composition has nothing on it.
/// </summary>
/// <remarks>
/// This is not decoration. An <c>IVideoOutput</c> is configured by its FIRST submitted frame, so a
/// composition that submits nothing leaves a window that was never created - no error, no window, and
/// nothing to tell an operator why the projector they just added is dark. Black is the last resort
/// that makes every attached output exist, and it is also the right picture: black is what every desk
/// in the building means by "nothing", and holding the last frame of the previous cue is how a video
/// ends on a freeze-frame of somebody mid-blink.
/// </remarks>
public class IdleFrameTests
{
    [Fact]
    public void BlackIsOpaqueAtTheCanvasSize()
    {
        using var frame = IdleFrames.Black(64, 48);

        Assert.Equal(64, frame.Format.Width);
        Assert.Equal(48, frame.Format.Height);
        Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);

        var pixels = frame.Planes[0].Span;

        for (var at = 0; at < pixels.Length; at += 4)
        {
            Assert.Equal(0, pixels[at]);
            Assert.Equal(0, pixels[at + 1]);
            Assert.Equal(0, pixels[at + 2]);
            // Explicit, because a zero-filled buffer is already black with a ZERO alpha - and a
            // premultiplied frame at alpha 0 composites as nothing at all, so a sink that blends would
            // show whatever was underneath rather than black.
            Assert.Equal(255, pixels[at + 3]);
        }
    }

    [Fact]
    public void ADegenerateSizeIsRefusedRatherThanAllocated()
    {
        // A canvas of 0×0 would produce an empty buffer the sink then configures itself against.
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleFrames.Black(0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleFrames.Black(1920, 0));
    }

    /// <summary>
    /// The screen hint is a number, and the old dialog's label still resolves to one.
    /// </summary>
    /// <remarks>
    /// The add-output dialog used to store the picker's whole label - "2 · 1920×1080" - while every
    /// reader of the hint parsed it as an integer. So the chosen screen was silently discarded and the
    /// window opened on whichever display SDL answered with, with nothing on screen disagreeing: the
    /// inspector's own picker read the same hint the same way and showed "anywhere".
    /// </remarks>
    [Theory]
    [InlineData("2", 2)]
    [InlineData("2 · 1920×1080", 2)]
    [InlineData("10 · 3840×2160 · primary", 10)]
    public void AScreenHintResolvesToItsDisplayNumber(string hint, int expected) =>
        Assert.Equal(expected, ProjectVideoOutputs.ScreenNumber(hint));

    [Theory]
    [InlineData("")]
    [InlineData("anywhere")]
    [InlineData("screen 2")]
    [InlineData("0")]
    public void AnythingElseMeansWhereverItOpens(string hint) =>
        // Refused rather than guessed: moving a feed to the wrong screen is worse than not moving it.
        Assert.Null(ProjectVideoOutputs.ScreenNumber(hint));
}
