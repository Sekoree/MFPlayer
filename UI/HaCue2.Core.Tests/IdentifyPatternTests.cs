using HaCue2.Engine;
using HaCue2.Core.Model;
using S.Media.Core.Video;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The IDENTIFY frame: an output's name, drawn in pixels.
/// </summary>
/// <remarks>
/// It carries its own five-by-seven font because the engine has no window, no Skia and no font stack,
/// and a booth machine's font set is exactly the kind of thing that differs from the laptop a show was
/// authored on. That makes the glyph table something a test can and should read back.
/// </remarks>
public class IdentifyPatternTests
{
    /// <summary>Whether a pixel is ink (white) rather than field (the identify blue).</summary>
    private static bool IsInk(byte[] pixels, int stride, int x, int y)
    {
        var at = (y * stride) + (x * 4);
        return pixels[at] > 0xC0 && pixels[at + 1] > 0xC0 && pixels[at + 2] > 0xC0;
    }

    /// <summary>
    /// The rendered frame's one plane, read back.
    /// </summary>
    /// <remarks>
    /// A plain managed BGRA buffer, which is the whole reason this needs no GPU - and the whole reason
    /// a test can look at what was drawn.
    /// </remarks>
    private static (byte[] Pixels, int Stride, int Width, int Height) Render(
        string text, int width = 320, int height = 240)
    {
        using var frame = IdentifyPattern.Render(text, width, height);
        return (frame.Planes[0].ToArray(), frame.Strides[0], width, height);
    }

    [Fact]
    public void EveryGlyphIsFiveBySevenOrTheTypeWouldNotHaveLoaded() =>
        // The table validates itself at class-init. If a row were the wrong width this call throws a
        // TypeInitializationException rather than drawing a letter shifted sideways on a projector.
        IdentifyPattern.Render("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -.:/?", 640, 480).Dispose();

    [Fact]
    public void TheFrameIsTheSizeItWasAskedFor()
    {
        using var frame = IdentifyPattern.Render("Projector A", 1920, 1080);

        Assert.Equal(1920, frame.Format.Width);
        Assert.Equal(1080, frame.Format.Height);
        Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);
    }

    [Fact]
    public void ThereIsABorderOnEveryEdge()
    {
        var (pixels, stride, width, height) = Render("A");

        // Not decoration: it is how an operator sees whether the projector is showing the WHOLE
        // canvas. A feed cropped by an overscanning display looks normal until something is at the edge.
        Assert.True(IsInk(pixels, stride, 0, 0));
        Assert.True(IsInk(pixels, stride, width - 1, 0));
        Assert.True(IsInk(pixels, stride, 0, height - 1));
        Assert.True(IsInk(pixels, stride, width - 1, height - 1));
        Assert.True(IsInk(pixels, stride, width / 2, 0));
        Assert.True(IsInk(pixels, stride, 0, height / 2));
    }

    [Fact]
    public void TheFieldIsNotWhiteWhereNothingIsDrawn()
    {
        var (pixels, stride, width, height) = Render("I");

        // Just inside the border, above the letters. A frame that came back all-ink would pass every
        // other assertion here and be useless on a screen.
        Assert.False(IsInk(pixels, stride, width / 2, height / 8));
    }

    [Fact]
    public void CalibrationCarriesOrientationColoursGridAndSectionBoundaries()
    {
        using var frame = IdentifyPattern.Render("A", 400, 300,
        [
            new MappingSection { SourceX = .25, SourceY = .25, SourceWidth = .5, SourceHeight = .5 },
        ]);
        var pixels = frame.Planes[0].ToArray();
        var stride = frame.Strides[0];

        // Inside the four coloured corner patches (the outermost pixel is the white overscan border).
        Assert.NotEqual(Pixel(pixels, stride, 10, 10), Pixel(pixels, stride, 390, 10));
        Assert.NotEqual(Pixel(pixels, stride, 10, 10), Pixel(pixels, stride, 10, 290));
        // Ten-by-ten reference grid and cyan source-section edge both differ from the field.
        Assert.NotEqual(Pixel(pixels, stride, 41, 41), Pixel(pixels, stride, 70, 41));
        Assert.NotEqual(Pixel(pixels, stride, 80, 80), Pixel(pixels, stride, 100, 80));
    }

    private static (byte B, byte G, byte R) Pixel(byte[] pixels, int stride, int x, int y)
    {
        var at = y * stride + x * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    [Fact]
    public void DifferentNamesDrawDifferentPictures()
    {
        var a = Render("PROJECTOR A").Pixels;
        var b = Render("PROJECTOR B").Pixels;

        // The whole point of the feature: two outputs must not flash the same thing.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LowerCaseDrawsTheSamePictureAsUpper() =>
        // The font has one case. An operator who named an output "Lobby TV" must not get a row of
        // question marks for the letters they happened to type in lower case.
        Assert.Equal(Render("lobby tv").Pixels, Render("LOBBY TV").Pixels);

    [Fact]
    public void ACharacterTheFontDoesNotHaveDrawsAQuestionMarkRatherThanNothing()
    {
        var known = Render("A?A").Pixels;
        var unknown = Render("A€A").Pixels;

        // Silently drawing nothing would shorten the name and could make two outputs identical.
        Assert.Equal(known, unknown);
    }

    [Fact]
    public void ALongNameIsScaledDownRatherThanDrawnAtFullSizeAndClipped()
    {
        // The same name on a canvas four times as wide gets bigger letters. Scaling down is what keeps
        // the whole name on screen - half a name identifies the wrong output, which is worse than
        // small letters.
        var narrow = Ink(Render("PROJECTOR A", 320, 240));
        var wide = Ink(Render("PROJECTOR A", 1280, 960));

        Assert.True(wide > narrow * 4, $"{wide} ink pixels at 1280 wide vs {narrow} at 320");
    }

    [Fact]
    public void ANameTooLongForTheCanvasDrawsWhatItCanRatherThanThrowing()
    {
        // Past a point even one pixel per cell does not fit. The clamp in the blitter is what keeps
        // that from writing outside the buffer, and there is no smaller size left to fall back to.
        using var frame = IdentifyPattern.Render(new string('W', 400), 160, 120);

        Assert.Equal(160, frame.Format.Width);
        Assert.Equal(120, frame.Format.Height);
    }

    /// <summary>How many pixels were drawn on - a proxy for how big the letters came out.</summary>
    private static int Ink((byte[] Pixels, int Stride, int Width, int Height) frame)
    {
        var (pixels, stride, width, height) = frame;
        var count = 0;

        // The interior only: the border scales with the canvas too and would swamp the comparison.
        for (var y = height / 10; y < height * 9 / 10; y++)
            for (var x = width / 10; x < width * 9 / 10; x++)
                if (IsInk(pixels, stride, x, y))
                    count++;

        return count;
    }

    [Fact]
    public void AnEmptyNameStillProducesAReadableFrame()
    {
        // An unnamed output is a document mistake, not a reason to hand the compositor a blank card
        // that looks exactly like a dead feed.
        var (pixels, stride, width, height) = Render("");

        var ink = 0;

        for (var y = height / 3; y < height * 2 / 3; y++)
            for (var x = width / 3; x < width * 2 / 3; x++)
                if (IsInk(pixels, stride, x, y))
                    ink++;

        Assert.True(ink > 0, "the middle of the frame is blank");
    }
}
